using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    public sealed class MuseCliResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
    }

    // Muse usage via the official CLI (spec section 12). The CLI manages its own login, so
    // this provider never reads or writes a Muse token; it runs `muse usage --json` and
    // parses the output. The runner is injectable so the self-test feeds fixture text and
    // never starts a process.
    public sealed class MuseProvider : IUsageProvider
    {
        const int TimeoutMs = 15000;

        readonly Func<CancellationToken, Task<MuseCliResult>> runnerOverride;

        public MuseProvider() : this(null) { }

        // Self-test hook: a fake runner returning fixture text; no process is started.
        public MuseProvider(Func<CancellationToken, Task<MuseCliResult>> runnerOverride)
        {
            this.runnerOverride = runnerOverride;
        }

        public string Id { get { return "muse"; } }
        public string DisplayName { get { return "Muse"; } }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            MuseCliResult result;
            try
            {
                result = await (runnerOverride != null ? runnerOverride(ct) : RunCli(ct));
            }
            catch (OperationCanceledException)
            {
                throw;   // timeout is the refresh service's concern
            }
            catch (Exception ex)
            {
                return Err(Short(ex.Message));
            }
            if (result == null) return Err("no output");

            // Exit code 9009 or cmd's "is not recognized" means muse is not installed.
            if (result.ExitCode == 9009 ||
                (result.StdErr != null && result.StdErr.Contains("is not recognized")))
                return Snap(ProviderStatus.NotConfigured, "not configured");

            return Parse(result.StdOut, result.ExitCode, result.StdErr);
        }

        // cmd.exe /c muse usage --json: no window, stdout and stderr captured, the process
        // tree killed after 15 s.
        static Task<MuseCliResult> RunCli(CancellationToken ct)
        {
            return Task.Run<MuseCliResult>(delegate
            {
                var psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c muse usage --json";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return new MuseCliResult { ExitCode = -1, StdErr = "no process" };
                    Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = proc.StandardError.ReadToEndAsync();
                    bool exited = proc.WaitForExit(TimeoutMs);
                    if (!exited)
                    {
                        KillTree(proc);
                        return new MuseCliResult { ExitCode = -1, StdErr = "timeout after 15 s" };
                    }
                    return new MuseCliResult
                    {
                        ExitCode = proc.ExitCode,
                        StdOut = stdout.Result ?? "",
                        StdErr = stderr.Result ?? ""
                    };
                }
            }, ct);
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

        // Pure: no I/O, no clock, no process. RefreshService stamps FetchedAtUtc.
        public static UsageSnapshot Parse(string json, int exitCode, string stderr)
        {
            var snapshot = new UsageSnapshot();
            snapshot.ProviderId = "muse";
            snapshot.DisplayName = "Muse";

            // A non-zero exit is only an auth problem when the CLI says so.
            if (exitCode != 0)
            {
                if ((stderr != null && stderr.Contains("login")) ||
                    (json != null && json.Contains("login")))
                {
                    snapshot.Status = ProviderStatus.AuthRequired;
                    snapshot.Message = "sign in needed: run muse login";
                }
                else
                {
                    snapshot.Status = ProviderStatus.Error;
                    string reason = string.IsNullOrEmpty(stderr) ? null : stderr.Trim();
                    snapshot.Message = "error: " + (string.IsNullOrEmpty(reason)
                        ? "exit code " + exitCode.ToString(CultureInfo.InvariantCulture)
                        : Cap60(reason));
                }
                return snapshot;
            }

            var root = Json.ParseObject(json);
            if (root == null)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.Message = "unexpected output";
                return snapshot;
            }

            var allowance = Json.GetObject(root, "allowance");
            if (allowance == null)
            {
                string reason = Json.GetStr(root, "reason");
                if (reason == "server_sent_no_usage")
                {
                    snapshot.Status = ProviderStatus.Ok;
                    snapshot.Message = "no usage yet in this window";
                }
                else
                {
                    snapshot.Status = ProviderStatus.AuthRequired;
                    snapshot.Message = "sign in needed: run muse login";
                }
                return snapshot;
            }

            var windows = new List<UsageWindow>();
            double? fallbackReset = Json.GetDouble(allowance, "resetsAtMs");
            MapWindow(windows, WindowKind.FiveHour, Json.GetObject(allowance, "window"),
                "windowDurationMins", 300, fallbackReset);
            MapWindow(windows, WindowKind.Weekly, Json.GetObject(allowance, "weekly"),
                null, 0, fallbackReset);

            snapshot.PlanName = Capital(Json.GetStr(allowance, "tier"));
            snapshot.Windows = windows;
            if (windows.Count == 0)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.Message = "unexpected response";
            }
            else
            {
                snapshot.Status = ProviderStatus.Ok;
            }
            return snapshot;
        }

        // The five-hour block is gated on the duration in minutes (300 = 5 h), other
        // durations are ignored; the weekly block is mapped by name alone. durationKey is
        // null for blocks without a duration gate.
        static void MapWindow(List<UsageWindow> into, WindowKind kind,
            Dictionary<string, object> block, string durationKey, int durationValue,
            double? fallbackResetMs)
        {
            if (block == null) return;
            if (durationKey != null && Json.GetInt(block, durationKey, -1) != durationValue) return;
            double? percent = Json.GetDouble(block, "usedPercent");
            if (!percent.HasValue) return;

            var window = new UsageWindow();
            window.Kind = kind;
            window.UsedPercent = percent.Value;
            double? reset = Json.GetDouble(block, "resetsAtMs");
            if (!reset.HasValue) reset = fallbackResetMs;
            if (reset.HasValue)
                window.ResetsAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMilliseconds(reset.Value);
            into.Add(window);
        }

        static string Capital(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }

        static string Cap60(string text)
        {
            if (text.Length > 60) text = text.Substring(0, 60);
            return text;
        }

        UsageSnapshot Err(string message)
        {
            return Snap(ProviderStatus.Error, message);
        }

        UsageSnapshot Snap(ProviderStatus status, string message)
        {
            var s = new UsageSnapshot();
            s.ProviderId = Id;
            s.DisplayName = DisplayName;
            s.Status = status;
            s.Message = message;
            return s;
        }

        static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "muse cli failed";
            return Cap60(message);
        }
    }
}
