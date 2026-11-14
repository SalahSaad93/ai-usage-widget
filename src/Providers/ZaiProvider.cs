using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // Z.ai GLM coding-plan quota (spec section 12). The API key is read from opencode's auth
    // file on each fetch, kept in local variables, never logged, never cached, never written.
    public sealed class ZaiProvider : IUsageProvider
    {
        public const string UsageUrl = "https://api.z.ai/api/monitor/usage/quota/limit";

        readonly string authFileOverride;
        readonly string zcodeConfigOverride;

        public ZaiProvider() : this(null, null) { }

        // Self-test hooks: point the auth and ZCode-config reads at temp folders so no real
        // user file is ever touched.
        public ZaiProvider(string authFileOverride, string zcodeConfigOverride)
        {
            this.authFileOverride = authFileOverride;
            this.zcodeConfigOverride = zcodeConfigOverride;
        }

        public string Id { get { return "zai"; } }
        public string DisplayName { get { return "Z.ai GLM"; } }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            KeySource key = LocateKey(true);
            if (key == null) return NotConfigured();

            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + key.Value));

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
                return Err(Short(ex));
            }

            if (resp.Status == 401 || resp.Status == 403)
                return AuthRequired();
            if (resp.Status != 200)
                return Err(Errors.StatusText(resp.Status));
            return Parse(resp.Text);
        }

        // Pure path computation (no environment reads), reachable from the self-test.
        public static string DefaultAuthPath(string profileFolder)
        {
            return Path.Combine(profileFolder, ".local", "share", "opencode", "auth.json");
        }

        static string DefaultAuthPath()
        {
            return DefaultAuthPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        // Pure path computation (no environment reads), reachable from the self-test.
        public static string DefaultZcodeConfigPath(string profileFolder)
        {
            return Path.Combine(profileFolder, ".zcode", "cli", "config.json");
        }

        static string DefaultZcodeConfigPath()
        {
            return DefaultZcodeConfigPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        sealed class KeySource
        {
            public string Value;   // the key itself, only when wantValue; never logged or stored
        }

        // The Z.ai key sources, first match wins (spec section 13): zai-coding-plan.key in
        // opencode's auth.json, then the ZAI_API_KEY environment variable, then
        // provider.zai.options.apiKey in the ZCode config. One method serves both
        // IsDetected (wantValue false: presence only, the key value is never kept) and
        // FetchAsync (wantValue true: the key comes back for the Bearer header).
        KeySource LocateKey(bool wantValue)
        {
            string authJson = Detection.ReadTextIfExists(authFileOverride ?? DefaultAuthPath());
            if (authJson != null)
            {
                var plan = Json.GetObject(Json.ParseObject(authJson), "zai-coding-plan");
                string key = Json.GetStr(plan, "key");
                if (!string.IsNullOrEmpty(key))
                {
                    var found = new KeySource();
                    if (wantValue) found.Value = key;
                    return found;
                }
            }

            string envKey = Environment.GetEnvironmentVariable("ZAI_API_KEY");
            if (!string.IsNullOrEmpty(envKey))
            {
                var found = new KeySource();
                if (wantValue) found.Value = envKey;
                return found;
            }

            string configJson = Detection.ReadTextIfExists(
                zcodeConfigOverride ?? DefaultZcodeConfigPath());
            if (configJson != null)
            {
                var provider = Json.GetObject(Json.ParseObject(configJson), "provider");
                var zai = Json.GetObject(provider, "zai");
                var options = Json.GetObject(zai, "options");
                string key = Json.GetStr(options, "apiKey");
                if (!string.IsNullOrEmpty(key))
                {
                    var found = new KeySource();
                    if (wantValue) found.Value = key;
                    return found;
                }
            }
            return null;
        }

        // Detected when any Z.ai key source exists (spec section 13).
        public bool IsDetected()
        {
            return LocateKey(false) != null;
        }

        // Pure: no I/O, no clock. RefreshService stamps FetchedAtUtc.
        public static UsageSnapshot Parse(string json)
        {
            var root = Json.ParseObject(json);
            var snapshot = new UsageSnapshot();
            snapshot.ProviderId = "zai";
            snapshot.DisplayName = "Z.ai GLM";

            // A 200 body can still refuse the key.
            if (!Json.GetBool(root, "success", true) && Json.GetInt(root, "code", 0) == 401)
            {
                snapshot.Status = ProviderStatus.AuthRequired;
                snapshot.Message = "sign in needed: check Z.ai key";
                return snapshot;
            }

            var data = Json.GetObject(root, "data");
            var limits = Json.GetArray(data, "limits");
            var windows = new List<UsageWindow>();
            if (limits != null)
                foreach (object o in limits)
                    MapLimit(windows, o as Dictionary<string, object>);

            snapshot.PlanName = PlanNameOf(Json.GetStr(data, "level"));
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

        // TOKENS_LIMIT unit 3 -> five-hour, unit 6 -> weekly; TIME_LIMIT -> monthly (the
        // web-tool quota, the one window with absolute numbers). Anything else is ignored.
        static void MapLimit(List<UsageWindow> into, Dictionary<string, object> limit)
        {
            if (limit == null) return;
            string type = Json.GetStr(limit, "type");
            int unit = Json.GetInt(limit, "unit", -1);
            WindowKind? kind = null;
            if (type == "TOKENS_LIMIT" && unit == 3) kind = WindowKind.FiveHour;
            else if (type == "TOKENS_LIMIT" && unit == 6) kind = WindowKind.Weekly;
            else if (type == "TIME_LIMIT") kind = WindowKind.Monthly;
            if (!kind.HasValue) return;
            double? percent = Json.GetDouble(limit, "percentage");
            if (!percent.HasValue) return;

            var window = new UsageWindow();
            window.Kind = kind.Value;
            window.UsedPercent = percent.Value;
            if (kind.Value == WindowKind.Monthly)
            {
                window.Used = Json.GetDouble(limit, "currentValue");
                window.Limit = Json.GetDouble(limit, "usage");
            }
            window.ResetsAtUtc = FromEpochMs(Json.GetDouble(limit, "nextResetTime"));
            into.Add(window);
        }

        public static string PlanNameOf(string level)
        {
            if (string.IsNullOrEmpty(level)) return "";
            if (level == "lite") return "Coding Lite";
            if (level == "pro") return "Coding Pro";
            if (level == "max") return "Coding Max";
            return level;
        }

        static DateTime? FromEpochMs(double? ms)
        {
            if (!ms.HasValue) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms.Value);
        }

        UsageSnapshot AuthRequired()
        {
            return Snap(ProviderStatus.AuthRequired, "sign in needed: check Z.ai key");
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

        static string Short(Exception ex)
        {
            return "error: " + Errors.Describe(ex);
        }
    }
}
