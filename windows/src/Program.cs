using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace QuickAskWin
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [STAThread]
        private static void Main(string[] args)
        {
            string configArg = null;
            bool selftest = false;
            bool silent = false;
            bool uitest = false;
            bool searchtest = false;
            string searchQuery = null;
            string instance = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) configArg = args[++i];
                else if (args[i] == "--selftest") selftest = true;
                else if (args[i] == "--silent") silent = true;
                else if (args[i] == "--uitest") uitest = true;
                else if (args[i] == "--instance" && i + 1 < args.Length) instance = args[++i];
                else if (args[i] == "--searchtest")
                {
                    searchtest = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) searchQuery = args[++i];
                }
            }

            string configPath = ConfigStore.ConfigPath(configArg);

            if (selftest || searchtest)
            {
                AttachConsole(-1);
                StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                stdout.AutoFlush = true;
                Console.SetOut(stdout);
                Console.SetError(stdout);
                Environment.ExitCode = searchtest
                    ? SelfTest.Search(searchQuery, configPath)
                    : SelfTest.Run(configPath);
                return;
            }

            bool created;
            string mutexName = string.IsNullOrEmpty(instance)
                ? "QuickAskWin.SingleInstance"
                : "QuickAskWin.SingleInstance." + instance;
            Mutex mutex = new Mutex(true, mutexName, out created);
            if (!created)
            {
                MessageBox.Show("随手问已经在运行，请看任务栏右下角的托盘图标。", "随手问",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { LogCrash(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogCrash(e.ExceptionObject as Exception);
            };

            AppConfig cfg = ConfigStore.Load(configPath);
            PanelForm form = new PanelForm(cfg, configPath);
            form.UiTest = uitest;

            bool opened = false;
            Application.Idle += delegate
            {
                if (opened) return;
                opened = true;
                if (!silent) form.ShowPanel();
            };

            Application.Run(new ApplicationContext(form));
            GC.KeepAlive(mutex);
        }

        /// <summary>Append startup/runtime failures to %APPDATA%\QuickAskWin\error.log.</summary>
        private static void LogCrash(Exception error)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickAskWin");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" + (error == null ? "unknown" : error.ToString()) + "\n\n");
            }
            catch { }
        }
    }
}
