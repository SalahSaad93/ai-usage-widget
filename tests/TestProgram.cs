using System;

namespace AiUsageWidget
{
    // Entry point of AiUsageWidget.Tests.exe. The tests live outside the shipped exe and
    // reference it; build.ps1 compiles both and runs this after every build.
    //   --out <file>          write one PASS/FAIL line per check to a file as well
    //   --screenshots <dir>   render every window with made-up data into PNG files
    internal static class TestProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            string outPath = null;
            string screenshotDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--out" || args[i] == "--selftest-out") && i + 1 < args.Length)
                    outPath = args[++i];
                else if (args[i] == "--screenshots" && i + 1 < args.Length)
                    screenshotDir = args[++i];
            }
            if (screenshotDir != null) return Screenshots.RenderAll(screenshotDir);
            return SelfTest.RunAll(outPath);
        }
    }
}
