using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    internal static class Program
    {
        // Named events the running widget listens on (see WidgetForm). A second launch sets
        // ShowEventName so the first instance shows its window; `--test-notification` sets
        // the other so it shows a sample tray notification. Events, not window messages:
        // the widget is an owned tool window, which a broadcast message does not reach.
        public const string ShowEventName = @"Local\AiUsageWidget.Show";
        public const string TestNotifyEventName = @"Local\AiUsageWidget.TestNotify";

        // Sets a named event of the running instance; nothing happens when none is running.
        static void Signal(string eventName)
        {
            try
            {
                using (EventWaitHandle handle = EventWaitHandle.OpenExisting(eventName))
                    handle.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            int updatedPid = -1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--updated" && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out updatedPid);
                }
            }

            if (Array.IndexOf(args, "--test-notification") >= 0)
            {
                Signal(TestNotifyEventName);
                return 0;
            }

            // --updated <oldPid>: wait for the old process, drop its .old file, then start.
            if (updatedPid >= 0) Updater.FinishUpdate(updatedPid);

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\AiUsageWidget", out createdNew))
            {
                if (!createdNew)
                {
                    // Second instance: poke the running one to show its window, then exit.
                    Signal(ShowEventName);
                    return 0;   // single instance
                }
                InstallExceptionHandlers();
                Log.Info("AI Usage Widget v" + VersionText() + " started");
                Application.EnableVisualStyles();
                Application.Run(new WidgetForm(VersionText()));
            }
            return 0;
        }

        // Every escape route logs and, where .NET allows it, the widget keeps running
        // instead of the crash dialog.
        static void InstallExceptionHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                Log.Error("UI thread exception", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender,
                UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Log.Error(e.IsTerminating ? "unhandled exception (terminating)" : "unhandled exception",
                    ex);
            };
            TaskScheduler.UnobservedTaskException += delegate(object sender,
                UnobservedTaskExceptionEventArgs e)
            {
                Log.Error("unobserved task exception", e.Exception);
                e.SetObserved();
            };
        }

        static string VersionText()
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.ToString(3);
        }
    }
}
