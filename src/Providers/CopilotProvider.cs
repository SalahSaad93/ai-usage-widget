using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // GitHub Copilot quota (unofficial endpoint, so experimental and off by default). The
    // oauth token is read from the editor's credential file on each fetch, kept in local
    // variables, never logged, never cached, never written; the widget never refreshes it.
    public sealed class CopilotProvider : IUsageProvider
    {
        public const string UsageUrl = "https://api.github.com/copilot_internal/user";

        readonly string[] credentialFileOverrides;

        public CopilotProvider() : this(null) { }

        // Self-test hook: point the credential read at temp files instead of the real
        // %LOCALAPPDATA% / %USERPROFILE% locations.
        public CopilotProvider(string[] credentialFileOverrides)
        {
            this.credentialFileOverrides = credentialFileOverrides;
        }

        public string Id { get { return "github-copilot"; } }
        public string DisplayName { get { return "GitHub Copilot"; } }

        // Detected when any of the credential files exists (existence-only, spec 13).
        public bool IsDetected()
        {
            foreach (string path in CredentialFiles())
                if (Detection.FileExists(path)) return true;
            return false;
        }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            string token = FindToken();
            if (string.IsNullOrEmpty(token)) return NotConfigured();

            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>("Authorization", "token " + token));
            headers.Add(new KeyValuePair<string, string>("Accept", "application/json"));
            headers.Add(new KeyValuePair<string, string>("Editor-Version", "vscode/1.96.2"));
            headers.Add(new KeyValuePair<string, string>("Editor-Plugin-Version",
                "copilot-chat/0.26.7"));
            headers.Add(new KeyValuePair<string, string>("X-Github-Api-Version", "2025-04-01"));

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
                return Err("error: " + Errors.Describe(ex));
            }

            if (resp.Status == 401 || resp.Status == 403 || resp.Status == 404)
                return AuthRequired();
            if (resp.Status != 200)
                return Err(Errors.StatusText(resp.Status));
            return Parse(resp.Text);
        }

        // Pure path computation (no environment reads), reachable from the self-test: the
        // first existing of the four credential files is the source.
        public static string[] DefaultCredentialFiles(string localAppDataFolder,
            string profileFolder)
        {
            return new string[]
            {
                Path.Combine(localAppDataFolder, "github-copilot", "apps.json"),
                Path.Combine(localAppDataFolder, "github-copilot", "hosts.json"),
                Path.Combine(profileFolder, ".config", "github-copilot", "apps.json"),
                Path.Combine(profileFolder, ".config", "github-copilot", "hosts.json")
            };
        }

        string[] CredentialFiles()
        {
            return credentialFileOverrides ?? DefaultCredentialFiles(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        string FindToken()
        {
            return FindOAuthToken(CredentialFiles());
        }

        // The first oauth_token under a "github.com…" key, from the first readable file.
        // The token is returned to the caller for one header and never kept.
        public static string FindOAuthToken(string[] files)
        {
            if (files == null) return null;
            foreach (string path in files)
            {
                string json = Detection.ReadTextIfExists(path);
                if (json == null) continue;
                string token = FirstOAuthToken(json);
                if (token != null) return token;
            }
            return null;
        }

        // The same extraction, pure on already-read contents (hosts.json / apps.json shape:
        // keys start with "github.com", the value object carries oauth_token).
        public static string FirstOAuthToken(string json)
        {
            var root = Json.ParseObject(json);
            if (root == null) return null;
            foreach (KeyValuePair<string, object> entry in root)
            {
                if (!entry.Key.StartsWith("github.com", StringComparison.Ordinal)) continue;
                var fields = entry.Value as Dictionary<string, object>;
                if (fields == null) continue;
                string token = Json.GetStr(fields, "oauth_token");
                if (!string.IsNullOrEmpty(token)) return token;
            }
            return null;
        }

        // Pure: no I/O, no clock. RefreshService stamps FetchedAtUtc.
        public static UsageSnapshot Parse(string json)
        {
            var snapshot = new UsageSnapshot();
            snapshot.ProviderId = "github-copilot";
            snapshot.DisplayName = "GitHub Copilot";

            var root = Json.ParseObject(json);
            if (root == null)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.Message = "unexpected response";
                return snapshot;
            }

            snapshot.PlanName = Capital(Json.GetStr(root, "copilot_plan"));
            DateTime? reset = ParseResetDate(Json.GetStr(root, "quota_reset_date"));

            var blocks = Json.GetObject(root, "quota_snapshots");
            MapBlock(snapshot.Windows, Json.GetObject(blocks, "premium_interactions"),
                "premium_interactions", "premium requests", true, reset);
            MapBlock(snapshot.Windows, Json.GetObject(blocks, "chat"), "chat", "chat", false,
                reset);

            if (snapshot.Windows.Count == 0)
            {
                if (!string.IsNullOrEmpty(snapshot.PlanName))
                {
                    snapshot.Status = ProviderStatus.Ok;
                    snapshot.Message = "no quota reported";
                }
                else
                {
                    snapshot.Status = ProviderStatus.Error;
                    snapshot.Message = "unexpected response";
                }
                return snapshot;
            }
            snapshot.Status = ProviderStatus.Ok;
            return snapshot;
        }

        // A block that is absent, unlimited, or missing its percent yields no window; the
        // absolute pair is only reported when both fields exist. The endpoint has not been
        // captured live, so unknown fields are ignored.
        static void MapBlock(List<UsageWindow> into, Dictionary<string, object> block,
            string key, string label, bool primary, DateTime? reset)
        {
            if (block == null) return;
            if (Json.GetBool(block, "unlimited", false)) return;
            double? percentRemaining = Json.GetDouble(block, "percent_remaining");
            if (!percentRemaining.HasValue) return;

            var window = new UsageWindow();
            window.Kind = WindowKind.Monthly;
            window.Key = key;
            window.Label = label;
            window.Primary = primary;
            window.Unit = "requests";
            window.UsedPercent = 100.0 - percentRemaining.Value;
            double? entitlement = Json.GetDouble(block, "entitlement");
            double? remaining = Json.GetDouble(block, "remaining");
            if (entitlement.HasValue && remaining.HasValue)
            {
                window.Limit = entitlement.Value;
                window.Used = entitlement.Value - remaining.Value;
            }
            window.ResetsAtUtc = reset;
            into.Add(window);
        }

        // ISO date ("2026-12-01") or date-time; the bare date reads as midnight UTC.
        static DateTime? ParseResetDate(string text)
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

        static string Capital(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }

        UsageSnapshot AuthRequired()
        {
            return Snap(ProviderStatus.AuthRequired, "sign in needed: open your editor");
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
    }
}
