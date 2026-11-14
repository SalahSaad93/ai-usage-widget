using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    internal static class Program
    {
        // Broadcast by a second instance (HWND_BROADCAST) so the first one shows its
        // window; the widget handles it in WidgetForm.WndProc.
        public static readonly int ShowMessageId = (int)RegisterWindowMessage("AiUsageWidget.Show");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static readonly IntPtr HwndBroadcast = (IntPtr)0xffff;

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

            // --updated <oldPid>: wait for the old process, drop its .old file, then start.
            if (updatedPid >= 0) Updater.FinishUpdate(updatedPid);

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\AiUsageWidget", out createdNew))
            {
                if (!createdNew)
                {
                    // Second instance: poke the running one to show its window, then exit.
                    PostMessage(HwndBroadcast, (uint)ShowMessageId, IntPtr.Zero, IntPtr.Zero);
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
