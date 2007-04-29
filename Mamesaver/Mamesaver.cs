/**
 * Licensed under The MIT License
 * Redistributions of files must retain the above copyright notice.
 */

using System;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using System.IO;
using System.Configuration;
using gma.System.Windows;
using System.Threading;
using System.Runtime.InteropServices;

namespace Mamesaver
{
    public class Mamesaver
    {
        #region Variables
        GameTimer timer = null;
        BackgroundForm frmBackground = null;
        UserActivityHook actHook = null;
        bool cancelled = false;
        #endregion

        #region DLL Imports
        [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
        public static extern int GetSystemMetrics(int which);

        [DllImport("user32.dll")]
        public static extern void SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int X, int Y, int width, int height, uint flags);
        #endregion

        #region Constants
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private static IntPtr HWND_TOP = IntPtr.Zero;
        private const int SWP_SHOWWINDOW = 64; // 0�0040
        #endregion

        #region Interops
        public static int ScreenX
        {
            get { return GetSystemMetrics(SM_CXSCREEN);}
        }

        public static int ScreenY
        {
            get { return GetSystemMetrics(SM_CYSCREEN);}
        }

        public static void SetWinFullScreen(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, ScreenX, ScreenY, SWP_SHOWWINDOW);
        }
        #endregion


        #region Public Methods
        public void ShowConfig()
        {
            ConfigForm frmConfig = new ConfigForm(this);
            Application.EnableVisualStyles();
            Application.Run(frmConfig);
        }

        public void Run()
        {
            try
            {
                // Load list and get only selected games from it
                List<SelectableGame> gameListFull = LoadGameList();
                List<Game> gameList = new List<Game>();

                if (gameListFull.Count == 0) return;

                foreach (SelectableGame game in gameListFull)
                    if (game.Selected) gameList.Add(game);

                // Set up the timer
                int minutes = Properties.Settings.Default.minutes;
                timer = new GameTimer(minutes * 60000, gameList);
                timer.Tick += new EventHandler(timer_Tick);

                // Set up the background form
                Cursor.Hide();
                frmBackground = new BackgroundForm();
                frmBackground.Capture = true;
                frmBackground.Load += new EventHandler(frmBackground_Load);

                // Set up the global hooks
                actHook = new UserActivityHook();
                actHook.OnMouseActivity += new MouseEventHandler(actHook_OnMouseActivity);
                actHook.KeyDown += new KeyEventHandler(actHook_KeyDown);

                // Run the application
                Application.EnableVisualStyles();
                Application.Run(frmBackground);
            }
            catch(Exception x)
            {
                MessageBox.Show(x.Message, "Error",  MessageBoxButtons.OK , MessageBoxIcon.Error);
            }
        }


        /// <summary>
        /// Save the selectable game list to an XML file.
        /// </summary>
        /// <param name="gameList"></param>
        public void SaveGameList(List<SelectableGame> gameList)
        {
            Configuration c = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            DirectoryInfo configPath  = Directory.GetParent(c.FilePath);
            string filename = Path.Combine(configPath.ToString(), "gamelist.xml");
            if (!configPath.Exists) configPath.Create();

            XmlSerializer serializer = new XmlSerializer(typeof(List<SelectableGame>));
            FileStream fileList = new FileStream(filename, FileMode.Create);
            XmlTextWriter writer = new XmlTextWriter(fileList, UTF8Encoding.UTF8);
            serializer.Serialize(writer, gameList);
            writer.Close();
        }

        /// <summary>
        /// Load the selectable game list from an XML file. Return an empty array if no file found.
        /// </summary>
        /// <returns><see cref="List"/> of <see cref="SelectableGame"/>s</returns>
        public List<SelectableGame> LoadGameList()
        {
            Configuration c = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            DirectoryInfo configPath = Directory.GetParent(c.FilePath);
            string filename = Path.Combine(configPath.ToString(), "gamelist.xml");

            if (!File.Exists(filename)) return new List<SelectableGame>();

            XmlSerializer serializer = new XmlSerializer(typeof(List<SelectableGame>));
            FileStream fileList = new FileStream(filename, FileMode.Open);
            XmlTextReader reader = new XmlTextReader(fileList);
            List<SelectableGame> gameList = serializer.Deserialize(reader) as List<SelectableGame>;
            reader.Close();

            return gameList;
        }

        /// <summary>
        /// Returns a <see cref="List"/> of <see cref="SelectableGame"/>s which are read from
        /// the full list and then merged with the verified rom's list. The games which are returned
        /// all have a "good" status on their drivers. This check also eliminates BIOS ROMS.
        /// </summary>
        /// <returns>Returns a <see cref="List"/> of <see cref="SelectableGame"/>s</returns>
        public List<SelectableGame> GetGameList()
        {
            Hashtable verifiedGames = GetVerifiedSets();
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(GetFullGameList());
            List<SelectableGame> games = new List<SelectableGame>();

            foreach (string key in verifiedGames.Keys)
            {
                XmlNode xmlGame = doc.SelectSingleNode(string.Format("mame/game[@name='{0}']", key));

                if ( xmlGame != null && xmlGame["driver"] != null && xmlGame["driver"].Attributes["status"].Value == "good" )
                    games.Add(new SelectableGame(xmlGame.Attributes["name"].Value, xmlGame["description"].InnerText, xmlGame["year"] != null ? xmlGame["year"].InnerText : "", xmlGame["manufacturer"] != null ? xmlGame["manufacturer"].InnerText : "", false));
            }

            return games;
        }
        #endregion

        #region Event Hanlders
        void frmBackground_Load(object sender, EventArgs e)
        {
            // Start the first game
            timer.Process = RunRandomGame(timer.GameList);
        }

        private void timer_Tick(object sender, EventArgs e)
        {
            timer.Stop();

            // End the currently playing game
            if (timer.Process != null && !timer.Process.HasExited) timer.Process.CloseMainWindow();

            // Start new game
            timer.Process = RunRandomGame(timer.GameList);
        }

        void actHook_KeyDown(object sender, KeyEventArgs e)
        {
            Close();
        }


        void actHook_OnMouseActivity(object sender, MouseEventArgs e)
        {
            Close();
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Stop the timer, set cancelled flag, close any current process and close the background form. 
        /// Once this has all been done, the application should end.
        /// </summary>
        private void Close()
        {
            timer.Stop();
            cancelled = true;
            if (timer.Process != null && !timer.Process.HasExited) timer.Process.CloseMainWindow();
            frmBackground.Close();
        }

        /// <summary>
        /// Gets a random number and then runs <see cref="RunGame"/> using the game in the
        /// <see cref="List"/>.
        /// </summary>
        /// <param name="gameList"></param>
        /// <returns>The <see cref="Process"/> running the game</returns>
        private Process RunRandomGame(List<Game> gameList)
        {
            // get random game
            Random r = new Random();
            int randomIndex = r.Next(0, gameList.Count - 1);
            Game randomGame = gameList[randomIndex];

            return RunGame(randomGame);
        }

        /// <summary>
        /// Runs the process
        /// </summary>
        /// <param name="game"></param>
        /// <returns>The <see cref="Process"/> running the game</returns>
        private Process RunGame(Game game)
        {
            // Set the game name and details on the background form
            frmBackground.lblData1.Text = game.Description;
            frmBackground.lblData2.Text = game.Year.ToString() + " " + game.Manufacturer;
            SetWinFullScreen(frmBackground.Handle);

            // Show the form for a couple of seconds
            DateTime end = DateTime.Now.AddSeconds(Properties.Settings.Default.backgroundSeconds);
            while (DateTime.Now < end)
            {
                if (cancelled) return null;
                Application.DoEvents();
            }

            // Set up the process
            string execPath = Properties.Settings.Default.execPath;
            ProcessStartInfo psi = new ProcessStartInfo(execPath);
            psi.Arguments = game.Name + " " + Properties.Settings.Default.cmdOptions; ;
            psi.WorkingDirectory = Directory.GetParent(execPath).ToString();

            // Start the timer and the process
            timer.Start();
            return Process.Start(psi);
        }

        /// <summary>
        /// Gets the full XML game list from <a href="http://www.mame.org/">Mame</a>.
        /// </summary>
        /// <returns><see cref="String"/> holding the Mame XML</returns>
        private string GetFullGameList()
        {
            string execPath = Properties.Settings.Default.execPath;
            ProcessStartInfo psi = new ProcessStartInfo(execPath);
            psi.Arguments = "-listxml";
            psi.WorkingDirectory = Directory.GetParent(execPath).ToString();
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            return output;
        }

        /// <summary>
        /// Returns a <see cref="HashTable"/> filled with the names of games which are
        /// verified to work. Only the ones marked as good are returned. The clone names
        /// are returned in the value of the hashtable while the name is used as the key.
        /// </summary>
        /// <returns><see cref="HashTable"/></returns>
        private Hashtable GetVerifiedSets()
        {
            string execPath = Properties.Settings.Default.execPath;
            ProcessStartInfo psi = new ProcessStartInfo(execPath);
            psi.Arguments = "-verifyroms";
            psi.WorkingDirectory = Directory.GetParent(execPath).ToString();
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();


            Regex r = new Regex(@"romset (\w*)(?:\s\[(\w*)\])? is good"); //only accept the "good" ROMS
            MatchCollection matches = r.Matches(output);
            Hashtable verifiedGames = new Hashtable();

            foreach (Match m in matches)
            {
                verifiedGames.Add(m.Groups[1].Value, m.Groups[2].Value);
            }

            return verifiedGames;
        }
        #endregion
    }
}