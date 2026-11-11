using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool selfTest = false;
            string selfTestOut = null;
            int updatedPid = -1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--selftest")
                {
                    selfTest = true;
                }
                else if (a == "--selftest-out" && i + 1 < args.Length)
                {
                    selfTestOut = args[++i];
                }
                else if (a == "--updated" && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out updatedPid);
                }
            }

            if (selfTest) return SelfTest.RunAll(selfTestOut);

            // --updated <oldPid>: wait for the old process, drop its .old file, then start.
            if (updatedPid >= 0) Updater.FinishUpdate(updatedPid);

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\AiUsageWidget", out createdNew))
            {
                if (!createdNew) return 0;   // single instance
                Application.EnableVisualStyles();
                Application.Run(new WidgetForm(VersionText()));
            }
            return 0;
        }

        static string VersionText()
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.ToString(3);
        }
    }
}
