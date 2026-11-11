using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // Claude Code usage (spec section 12). The OAuth token is read from disk on each fetch,
    // kept in local variables, never logged, never cached, never written; an expired login is
    // detected from the file alone, with no network call (spec section 7).
    public sealed class ClaudeCodeProvider : IUsageProvider
    {
        public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

        readonly string credentialsFileOverride;

        public ClaudeCodeProvider() : this(null) { }

        // Self-test hook: point the credentials read at a temp folder so the real
        // %USERPROFILE%\.claude\credentials.json is never touched.
        public ClaudeCodeProvider(string credentialsFileOverride)
        {
            this.credentialsFileOverride = credentialsFileOverride;
        }

        public string Id { get { return "claude-code"; } }
        public string DisplayName { get { return "Claude Code"; } }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            string path = credentialsFileOverride ?? DefaultCredentialsPath();
            if (!File.Exists(path)) return NotConfigured();
            string credentialsJson;
            try
            {
                credentialsJson = File.ReadAllText(path);
            }
            catch (IOException)
            {
                return NotConfigured();
            }
            catch (UnauthorizedAccessException)
            {
                return NotConfigured();
            }

            var credentials = Json.ParseObject(credentialsJson);
            var oauth = Json.GetObject(credentials, "claudeAiOauth");
            string token = Json.GetStr(oauth, "accessToken");
            if (string.IsNullOrEmpty(token)) return NotConfigured();

            // These logins last about 8 hours; an expired one is a current fact, so answer
            // AuthRequired straight from the file without touching the network.
            double? expiresAtMs = Json.GetDouble(oauth, "expiresAt");
            if (expiresAtMs.HasValue && expiresAtMs.Value <= EpochMsNow())
                return AuthRequired();

            string planName = PlanNameOf(credentialsJson);
            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + token));
            headers.Add(new KeyValuePair<string, string>("anthropic-beta", "oauth-2025-04-20"));

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

            if (resp.Status == 401 || resp.Status == 403) return AuthRequired();
            if (resp.Status != 200)
                return Err("HTTP " + resp.Status.ToString(CultureInfo.InvariantCulture));
            return Parse(resp.Text, planName);
        }

        static string DefaultCredentialsPath()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, ".claude", ".credentials.json");
        }

        static double EpochMsNow()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalMilliseconds;
        }

        // Pure: no I/O, no clock. RefreshService stamps FetchedAtUtc.
        public static UsageSnapshot Parse(string json, string planName)
        {
            var root = Json.ParseObject(json);
            var windows = new List<UsageWindow>();
            MapWindow(windows, WindowKind.FiveHour, Json.GetObject(root, "five_hour"));
            MapWindow(windows, WindowKind.Weekly, Json.GetObject(root, "seven_day"));
            var snapshot = new UsageSnapshot();
            snapshot.ProviderId = "claude-code";
            snapshot.DisplayName = "Claude Code";
            snapshot.PlanName = planName ?? "";
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

        // A null block is absent; a block without a utilization is too.
        static void MapWindow(List<UsageWindow> into, WindowKind kind,
            Dictionary<string, object> block)
        {
            if (block == null) return;
            double? percent = Json.GetDouble(block, "utilization");
            if (!percent.HasValue) return;
            var window = new UsageWindow();
            window.Kind = kind;
            window.UsedPercent = percent.Value;
            window.ResetsAtUtc = ParseInstant(Json.GetStr(block, "resets_at"));
            into.Add(window);
        }

        // "resets_at" is ISO 8601 with offset; unparseable -> no reset time.
        static DateTime? ParseInstant(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                DateTimeOffset value = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                return value.UtcDateTime;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // Plan name comes from the credentials file, not the usage response (spec section 12).
        public static string PlanNameOf(string credentialsJson)
        {
            var credentials = Json.ParseObject(credentialsJson);
            var oauth = Json.GetObject(credentials, "claudeAiOauth");
            string raw = Json.GetStr(oauth, "subscriptionType");
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw == "max")
            {
                string tier = Json.GetStr(oauth, "rateLimitTier");
                tier = tier ?? "";
                if (tier.Contains("5x")) return "Max 5x";
                if (tier.Contains("20x")) return "Max 20x";
                return "Max";
            }
            if (raw == "pro") return "Pro";
            return Capital(raw);
        }

        static string Capital(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw ?? "";
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }

        UsageSnapshot AuthRequired()
        {
            return Snap(ProviderStatus.AuthRequired, "sign in needed: open Claude Code");
        }

        UsageSnapshot NotConfigured()
        {
            return Snap(ProviderStatus.NotConfigured, "not configured");
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
            if (string.IsNullOrEmpty(message)) return "network error";
            if (message.Length > 60) message = message.Substring(0, 60);
            return "error: " + message;
        }
    }
}
