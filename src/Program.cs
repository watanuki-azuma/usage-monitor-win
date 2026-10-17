// Program entry point (milestone M5).
//
//   UsageMonitor.exe [--data-dir <dir>] [--no-poll] [--render-test <outdir> [--skin <id>]]
//
//   --data-dir <dir>   use <dir> for config.json / snapshot.json / alert-state.json / log
//                      (sets USAGE_MONITOR_DATA_DIR). Default: the folder of the exe, exactly
//                      like the packaged Electron app.
//   --skin <id>        with --render-test: classic (default), rpg, fluent or terminal.
//   --no-poll          start the UI only, using the cached snapshot.
//   --render-test <d>  render chips.png / flyout.png into <d> and exit. No window, no network,
//                      no credential access; always safe to run.
//
// The default data dir is NEVER the real home directory. Language level: C# 5.
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace UsageMonitor
{
    public static class Program
    {
        public const string MutexName = "UsageMonitor-SingleInstance-4d1f9c07";
        public const string ActivateEventName = "UsageMonitor-Activate-4d1f9c07";

        [STAThread]
        public static int Main(string[] args)
        {
            string dataDir = null;
            string renderDir = null;
            string skin = null;
            bool noPoll = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null) continue;

                if (arg == "--data-dir" && i + 1 < args.Length)
                {
                    dataDir = args[++i];
                }
                else if (arg == "--render-test" && i + 1 < args.Length)
                {
                    renderDir = args[++i];
                }
                else if (arg == "--skin" && i + 1 < args.Length)
                {
                    skin = args[++i];
                }
                else if (StartsWith(arg, "--skin="))
                {
                    skin = arg.Substring("--skin=".Length);
                }
                else if (arg == "--no-poll")
                {
                    noPoll = true;
                }
                else if (StartsWith(arg, "--data-dir="))
                {
                    dataDir = arg.Substring("--data-dir=".Length);
                }
                else if (StartsWith(arg, "--render-test="))
                {
                    renderDir = arg.Substring("--render-test=".Length);
                }
            }

            // DPI first, system-wide: Theme.Scale is fixed to the system DPI and the OS bitmap-
            // scales the window on any monitor that uses a different one, so nothing is left at
            // the wrong size after a drag to a second display.
            try { Native.SetSystemDpiAware(); }
            catch (Exception) { /* keep going un-aware */ }
            try { SkinRegistry.InitScale(); }
            catch (Exception) { /* each Theme falls back to lazy init */ }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(dataDir)) dataDir = exeDir;

            try { dataDir = Path.GetFullPath(dataDir); }
            catch (Exception) { dataDir = exeDir; }

            // Paths reads USAGE_MONITOR_DATA_DIR, which is how --data-dir reaches the rest of the app.
            Environment.SetEnvironmentVariable("USAGE_MONITOR_DATA_DIR", dataDir);

            if (renderDir != null)
            {
                string outDir = renderDir;
                try { outDir = Path.GetFullPath(renderDir); }
                catch (Exception) { /* keep the raw value */ }
                return RenderTest.Run(outDir, dataDir, exeDir, skin);
            }

            bool createdNew;
            Mutex single = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // Another instance is already running: poke it into showing its flyout.
                try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); }
                catch (Exception) { /* the other instance is shutting down */ }
                return 0;
            }

            AppController controller = null;
            try
            {
                Application.SetCompatibleTextRenderingDefault(false);

                // Route WinForms (ThreadException) and AppDomain unhandled exceptions to the log
                // file instead of the WinForms crash dialog. The AppDomain handler logs only —
                // the process is terminating, so there is nothing to recover (item 4).
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs te)
                {
                    LogFatal(te == null ? null : te.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs ue)
                {
                    Exception unhandled = ue == null ? null : ue.ExceptionObject as Exception;
                    LogFatal(unhandled);
                };

                Paths paths = new Paths(Environment.GetEnvironmentVariable);
                AppConfig config = LoadConfig(paths);
                controller = new AppController(paths, config, dataDir, noPoll);
                controller.Start();
                Application.Run(controller);
            }
            catch (Exception ex)
            {
                LogFatal(ex);
                return 1;
            }
            finally
            {
                if (controller != null) controller.Dispose();
                GC.KeepAlive(single);
            }
            return 0;
        }

        private static bool StartsWith(string value, string prefix)
        {
            return value != null && value.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static AppConfig LoadConfig(Paths paths)
        {
            string json = null;
            try
            {
                if (File.Exists(paths.ConfigPath)) json = File.ReadAllText(paths.ConfigPath);
            }
            catch (Exception) { json = null; }
            return AppConfig.Parse(json);
        }

        private static void LogFatal(Exception ex)
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("USAGE_MONITOR_DATA_DIR");
                if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                string path = Path.Combine(dir, "usage-monitor.log");
                File.AppendAllText(path, DateTime.Now.ToString("s") + " [error] fatal: " + ex + "\r\n");
            }
            catch (Exception) { /* nothing else we can do */ }
        }
    }
}
