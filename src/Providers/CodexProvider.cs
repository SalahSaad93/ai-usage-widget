using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // Codex (ChatGPT plan) usage. Login token is read from disk on each fetch, kept in local
    // variables, never logged, never cached, never written; the widget never refreshes or
    // rewrites the CLI's auth file (spec section 7).
    public sealed class CodexProvider : IUsageProvider
    {
        public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

        static readonly Dictionary<string, string> PlanNames = new Dictionary<string, string>
        {
            { "plus", "Plus" },
            { "pro", "Pro" },
            { "prolite", "Pro Lite" },
            { "team", "Team" }
        };

        readonly string authFileOverride;

        public CodexProvider() : this(null) { }

        // Self-test hook: point the auth read at a temp folder so the real
        // %USERPROFILE%\.codex\auth.json is never touched.
        public CodexProvider(string authFileOverride)
        {
            this.authFileOverride = authFileOverride;
        }

        public string Id { get { return "codex"; } }
        public string DisplayName { get { return "Codex"; } }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            string authPath = authFileOverride ?? DefaultAuthPath();
            if (!File.Exists(authPath)) return NotConfigured();
            string authJson;
            try
            {
                authJson = File.ReadAllText(authPath);
            }
            catch (IOException)
            {
                return NotConfigured();
            }
            catch (UnauthorizedAccessException)
            {
                return NotConfigured();
            }

            var auth = Json.ParseObject(authJson);
            var tokens = Json.GetObject(auth, "tokens");
            string token = Json.GetStr(tokens, "access_token");
            if (string.IsNullOrEmpty(token)) return NotConfigured();
            string accountId = Json.GetStr(tokens, "account_id");

            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + token));
            if (!string.IsNullOrEmpty(accountId))
                headers.Add(new KeyValuePair<string, string>("ChatGPT-Account-Id", accountId));

            HttpResponse resp;
            try
            {
                resp = await Http.GetTextAsync(UsageUrl, headers.ToArray(), ct);
            }
            catch (OperationCanceledException)
            {
                throw;   // timeout is the refresh service's concern
            }
            catch (Exception ex)
            {
                return Err(Short(ex.Message));
            }

            if (resp.Status == 401 || resp.Status == 403)
                return Snap(ProviderStatus.AuthRequired, "sign in needed: run codex login");
            if (resp.Status != 200)
                return Err("HTTP " + resp.Status.ToString(CultureInfo.InvariantCulture));
            return Parse(resp.Text);
        }

        // Pure path computation (no environment reads), reachable from the self-test.
        public static string DefaultAuthPath(string profileFolder, string codexHomeOverride)
        {
            if (!string.IsNullOrEmpty(codexHomeOverride)) return Path.Combine(codexHomeOverride, "auth.json");
            return Path.Combine(profileFolder, ".codex", "auth.json");
        }

        static string DefaultAuthPath()
        {
            return DefaultAuthPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("CODEX_HOME"));
        }

        // Pure: no I/O, no clock. RefreshService stamps FetchedAtUtc.
        public static UsageSnapshot Parse(string json)
        {
            var root = Json.ParseObject(json);
            var rateLimit = Json.GetObject(root, "rate_limit");
            var windows = new List<UsageWindow>();
            if (rateLimit != null)
            {
                MapWindow(windows, Json.GetObject(rateLimit, "primary_window"));
                MapWindow(windows, Json.GetObject(rateLimit, "secondary_window"));
            }
            var snapshot = new UsageSnapshot();
            snapshot.ProviderId = "codex";
            snapshot.DisplayName = "Codex";
            snapshot.PlanName = PlanNameOf(Json.GetStr(root, "plan_type"));
            snapshot.Windows = windows;
            if (windows.Count == 0)
            {
                // Parses but no usable windows: the undocumented endpoint changed shape.
                snapshot.Status = ProviderStatus.Error;
                snapshot.Message = "unexpected response";
            }
            else
            {
                snapshot.Status = ProviderStatus.Ok;
            }
            return snapshot;
        }

        // Match by window length, never by primary/secondary position (spec section 8).
        static void MapWindow(List<UsageWindow> into, Dictionary<string, object> w)
        {
            if (w == null) return;
            double? percent = Json.GetDouble(w, "used_percent");
            if (!percent.HasValue) return;
            WindowKind? kind = KindForSeconds(Json.GetDouble(w, "limit_window_seconds"));
            if (!kind.HasValue) return;
            var window = new UsageWindow();
            window.Kind = kind.Value;
            window.UsedPercent = percent.Value;
            double? resetAt = Json.GetDouble(w, "reset_at");
            if (resetAt.HasValue) window.ResetsAtUtc = FromEpochSeconds(resetAt.Value);
            into.Add(window);
        }

        public static WindowKind? KindForSeconds(double? seconds)
        {
            if (!seconds.HasValue) return null;
            double s = seconds.Value;
            if (s == 18000) return WindowKind.FiveHour;
            if (s == 604800) return WindowKind.Weekly;
            if (s >= 2592000) return WindowKind.Monthly;
            return null;
        }

        static DateTime FromEpochSeconds(double seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }

        static string PlanNameOf(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string mapped;
            if (!PlanNames.TryGetValue(raw, out mapped)) mapped = raw;
            return mapped;
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

        UsageSnapshot NotConfigured()
        {
            return Snap(ProviderStatus.NotConfigured, "not configured");
        }

        UsageSnapshot Err(string message)
        {
            return Snap(ProviderStatus.Error, message);
        }

        static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "network error";
            if (message.Length > 60) message = message.Substring(0, 60);
            return "error: " + message;
        }
    }
}
