using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // Antigravity usage via the official agy CLI (spec section 14). Hard rule: this provider
    // never opens, reads or references any Antigravity or Gemini credential or state file and
    // never calls Google endpoints itself - it only runs `agy`. The runner and PATH are
    // injectable so the self-test feeds fixture text and never starts a process.
    public sealed class AntigravityProvider : IUsageProvider
    {
        const int TimeoutMs = 20000;

        readonly Func<CancellationToken, Task<CliResult>> runnerOverride;
        readonly string pathOverride;

        public AntigravityProvider() : this(null, null) { }

        // Self-test hooks: the fake runner keeps processes out of the self-test; the PATH
        // override feeds detection without touching the real environment.
        public AntigravityProvider(Func<CancellationToken, Task<CliResult>> runnerOverride,
            string pathOverride)
        {
            this.runnerOverride = runnerOverride;
            this.pathOverride = pathOverride;
        }

        public string Id { get { return "antigravity"; } }
        public string DisplayName { get { return "Antigravity"; } }

        // agy on PATH (spec section 14). The Antigravity desktop app alone does not count, so
        // there is no .antigravity folder probe here; the KnownTools suggestion covers that.
        public bool IsDetected()
        {
            string pathValue = pathOverride ?? Environment.GetEnvironmentVariable("PATH");
            return Detection.OnPath(pathValue, "agy.exe")
                || Detection.OnPath(pathValue, "agy.cmd")
                || Detection.OnPath(pathValue, "agy.bat");
        }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            CliResult result;
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

            if (result.TimedOut) return Err("timeout after 20 s");

            // Exit code 9009 or cmd's "is not recognized" means agy is not installed.
            if (result.ExitCode == 9009 ||
                Mentions(result.StdOut, "is not recognized") ||
                Mentions(result.StdErr, "is not recognized"))
                return Snap(ProviderStatus.NotConfigured, "not configured");

            return Parse(result.StdOut, result.ExitCode, result.StdErr, DateTime.UtcNow);
        }

        // cmd.exe /c agy -p /usage --output-format json through the shared hidden-process
        // runner (spec section 14: same design as the Muse provider, one implementation).
        static Task<CliResult> RunCli(CancellationToken ct)
        {
            return Task.Run<CliResult>(delegate
            {
                return CliProcess.Run("agy -p /usage --output-format json", TimeoutMs);
            }, ct);
        }

        // Pure: no I/O, no process, no clock beyond the caller's "now" (used only for
        // reset_in_seconds). Spec section 14 state order: any non-zero exit or output that
        // mentions login / sign in / auth is an auth problem, whatever the exit code.
        public static UsageSnapshot Parse(string json, int exitCode, string stderr,
            DateTime nowUtc)
        {
            var snapshot = new UsageSnapshot();
            snapshot.ProviderId = "antigravity";
            snapshot.DisplayName = "Antigravity";

            if (exitCode != 0 || Mentions(json, "login") || Mentions(json, "sign in") ||
                Mentions(json, "auth") || Mentions(stderr, "login") ||
                Mentions(stderr, "sign in") || Mentions(stderr, "auth"))
            {
                snapshot.Status = ProviderStatus.AuthRequired;
                snapshot.Message = "sign in needed: run agy login";
                return snapshot;
            }

            var root = Json.ParseObject(json);
            if (root == null)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.Message = "unexpected output";
                return snapshot;
            }

            snapshot.PlanName = Capital(Json.GetStr(root, "tier"));
            if (snapshot.PlanName.Length == 0)
                snapshot.PlanName = Capital(Json.GetStr(root, "plan"));

            var best = new Dictionary<WindowKind, UsageWindow>();
            Walk(root, "", nowUtc, best);

            var windows = new List<UsageWindow>();
            if (best.ContainsKey(WindowKind.FiveHour)) windows.Add(best[WindowKind.FiveHour]);
            if (best.ContainsKey(WindowKind.Weekly)) windows.Add(best[WindowKind.Weekly]);
            if (best.ContainsKey(WindowKind.Monthly)) windows.Add(best[WindowKind.Monthly]);

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

        // The JSON shape has never been observed (spec section 14), so the walk is tolerant:
        // every object with a remaining fraction is a bucket; its kind comes from the keys on
        // the path plus its own id/name fields; per kind the highest UsedPercent wins.
        static void Walk(Dictionary<string, object> node, string pathText, DateTime nowUtc,
            Dictionary<WindowKind, UsageWindow> best)
        {
            double? fraction = Json.GetDouble(node, "remaining_fraction");
            if (!fraction.HasValue) fraction = Json.GetDouble(node, "remainingFraction");
            if (fraction.HasValue)
            {
                WindowKind? kind = KindOf(pathText + " " + OwnText(node));
                if (kind.HasValue)
                {
                    var window = new UsageWindow();
                    window.Kind = kind.Value;
                    double used = (1.0 - fraction.Value) * 100.0;
                    if (used < 0) used = 0;
                    if (used > 100) used = 100;
                    // Round off binary-fraction noise: (1 - 0.45) * 100 is 55.000…004 in
                    // doubles, and rows compare whole percents.
                    window.UsedPercent = Math.Round(used, 2);
                    window.ResetsAtUtc = ResetOf(node, nowUtc);
                    UsageWindow existing;
                    if (!best.TryGetValue(kind.Value, out existing) ||
                        window.UsedPercent > existing.UsedPercent)
                        best[kind.Value] = window;
                }
            }

            foreach (KeyValuePair<string, object> kv in node)
            {
                var child = kv.Value as Dictionary<string, object>;
                if (child != null) Walk(child, pathText + " " + kv.Key, nowUtc, best);
                // The serializer materializes JSON arrays as ArrayList (non-generic
                // IEnumerable), so walk any IEnumerable and keep only nested objects; a
                // dictionary or string value enumerates to nothing here.
                var items = kv.Value as IEnumerable;
                if (items != null)
                {
                    foreach (object item in items)
                    {
                        var itemNode = item as Dictionary<string, object>;
                        if (itemNode != null) Walk(itemNode, pathText + " " + kv.Key, nowUtc, best);
                    }
                }
            }
        }

        // Five-hour markers first ("5h" / "5-hour" / "5_hour" / "five" / "hour"), then week,
        // then month; anything else is ignored (spec section 14).
        static WindowKind? KindOf(string text)
        {
            if (text == null) return null;
            string t = text.ToLowerInvariant();
            if (t.Contains("5h") || t.Contains("hour") || t.Contains("five"))
                return WindowKind.FiveHour;
            if (t.Contains("week")) return WindowKind.Weekly;
            if (t.Contains("month")) return WindowKind.Monthly;
            return null;
        }

        // reset_time / resetTime (ISO 8601) first; failing that now + reset_in_seconds /
        // resetInSeconds; neither means no reset time on the row.
        static DateTime? ResetOf(Dictionary<string, object> node, DateTime nowUtc)
        {
            string raw = Json.GetStr(node, "reset_time");
            if (string.IsNullOrEmpty(raw)) raw = Json.GetStr(node, "resetTime");
            if (!string.IsNullOrEmpty(raw))
            {
                DateTime parsed;
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out parsed))
                    return parsed;
            }
            double? seconds = Json.GetDouble(node, "reset_in_seconds");
            if (!seconds.HasValue) seconds = Json.GetDouble(node, "resetInSeconds");
            if (seconds.HasValue) return nowUtc + TimeSpan.FromSeconds(seconds.Value);
            return null;
        }

        // The bucket's own id/name fields, so the kind can come from a displayName alone.
        static string OwnText(Dictionary<string, object> node)
        {
            string text = Json.GetStr(node, "id");
            if (text == null) text = "";
            string name = Json.GetStr(node, "name");
            if (name != null) text += " " + name;
            string display = Json.GetStr(node, "displayName");
            if (display != null) text += " " + display;
            return text;
        }

        static bool Mentions(string text, string word)
        {
            return text != null && text.ToLowerInvariant().Contains(word);
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
            if (string.IsNullOrEmpty(message)) return "agy cli failed";
            return Cap60(message);
        }
    }
}
