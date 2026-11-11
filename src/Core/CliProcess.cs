using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace AiUsageWidget.Core
{
    public sealed class CliResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
        public bool TimedOut;
    }

    // Hidden-process runner shared by the CLI providers (spec sections 12 and 14): runs one
    // command line via cmd.exe with no window, captures stdout and stderr, kills the whole
    // process tree after timeoutMs and reports whether the timeout fired. Providers map the
    // result onto their own snapshot states; injectable runner overrides keep real processes
    // out of the self-test.
    public static class CliProcess
    {
        public static CliResult Run(string commandLine, int timeoutMs)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c " + commandLine;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using (Process proc = Process.Start(psi))
            {
                if (proc == null)
                    return new CliResult { ExitCode = -1, StdErr = "no process" };
                Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
                Task<string> stderr = proc.StandardError.ReadToEndAsync();
                bool exited = proc.WaitForExit(timeoutMs);
                if (!exited)
                {
                    KillTree(proc);
                    return new CliResult
                    {
                        ExitCode = -1,
                        TimedOut = true,
                        StdErr = "timeout after " + (timeoutMs / 1000)
                            .ToString(CultureInfo.InvariantCulture) + " s"
                    };
                }
                return new CliResult
                {
                    ExitCode = proc.ExitCode,
                    StdOut = stdout.Result ?? "",
                    StdErr = stderr.Result ?? ""
                };
            }
        }

        static void KillTree(Process proc)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = "taskkill";
                psi.Arguments = "/PID " + proc.Id.ToString(CultureInfo.InvariantCulture) + " /T /F";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (Process killer = Process.Start(psi))
                {
                    if (killer != null) killer.WaitForExit(5000);
                }
            }
            catch (Win32Exception)
            {
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
