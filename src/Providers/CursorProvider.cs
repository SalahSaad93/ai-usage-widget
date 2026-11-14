using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // Cursor usage (unofficial endpoint, so experimental and off by default). The access
    // token is read from the state.vscdb ItemTable on each fetch, kept in local variables,
    // never logged, never cached, never written; the refresh token in the same table is
    // never touched — an expired access token is reported as AuthRequired instead.
    public sealed class CursorProvider : IUsageProvider
    {
        public const string UsageUrl =
            "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage";
        public const string TokenKey = "cursorAuth/accessToken";
        public const string PlanKey = "cursorAuth/stripeMembershipType";
        const string TableName = "ItemTable";

        readonly string dbFileOverride;
        readonly Func<string, string> readValue;

        public CursorProvider() : this(null, null) { }

        // Self-test hook: inject the token reader so no real database is ever opened.
        public CursorProvider(Func<string, string> readValueOverride) : this(null, readValueOverride) { }

        public CursorProvider(string dbFileOverride, Func<string, string> readValueOverride)
        {
            this.dbFileOverride = dbFileOverride;
            this.readValue = readValueOverride;
        }

        public string Id { get { return "cursor"; } }
        public string DisplayName { get { return "Cursor"; } }

        // Detected when the database file exists (existence-only, spec 13).
        public bool IsDetected()
        {
            return Detection.FileExists(DbPath());
        }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            string dbPath = DbPath();
            if (!Detection.FileExists(dbPath)) return NotConfigured();

            string token = ReadStoredValue(TokenKey);
            if (string.IsNullOrEmpty(token)) return NotConfigured();

            // An expired login is a current fact: answer AuthRequired straight from the
            // database without touching the network.
            if (IsTokenExpired(token, DateTime.UtcNow)) return AuthRequired();

            string planHint = ReadStoredValue(PlanKey);

            HttpResponse resp;
            try
            {
                resp = await PostUsage(token, ct);
            }
            catch (OperationCanceledException)
            {
                throw;   // timeout is the refresh service's concern
            }
            catch (Exception ex)
            {
                return Err("error: " + Errors.Describe(ex));
            }

            if (resp.Status == 401 || resp.Status == 403) return AuthRequired();
            if (resp.Status != 200)
                return Err(Errors.StatusText(resp.Status));
            return Parse(resp.Text, planHint);
        }

        // Core.Http only has GET helpers; this provider builds its own POST on the shared
        // client. The request body is the empty JSON object the Connect protocol expects.
        static async Task<HttpResponse> PostUsage(string token, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, UsageUrl))
            {
                req.Content = new StringContent("{}", Encoding.UTF8);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                using (HttpResponseMessage resp = await Http.Client.SendAsync(req, ct))
                {
                    var result = new HttpResponse();
                    result.Status = (int)resp.StatusCode;
                    if (resp.Content != null)
                        result.Text = await resp.Content.ReadAsStringAsync();
                    return result;
                }
            }
        }

        // Pure path computation (no environment reads), reachable from the self-test.
        public static string DefaultDbPath(string appDataFolder)
        {
            return Path.Combine(appDataFolder, "Cursor", "User", "globalStorage", "state.vscdb");
        }

        string DbPath()
        {
            return dbFileOverride ??
                DefaultDbPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        }

        string ReadStoredValue(string key)
        {
            if (readValue != null) return readValue(key);
            return Sqlite.ReadValue(DbPath(), TableName, key);
        }

        // Pure: no I/O, no clock (expiry is judged against the injected now).
        // RefreshService stamps FetchedAtUtc.
        public static UsageSnapshot Parse(string json, string membershipType)
        {
            var root = Json.ParseObject(json);
            var snapshot = new UsageSnapshot();
            snapshot.ProviderId = "cursor";
            snapshot.DisplayName = "Cursor";
            snapshot.PlanName = Capital(membershipType);
            if (root == null)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.Message = "unexpected response";
                return snapshot;
            }

            // The endpoint has not been captured live; every field is read tolerantly.
            // Dates may be ISO 8601 text or epoch milliseconds, quoted or not.
            DateTime? cycleStart = ParseCycleDate(Json.Get(root, "billingCycleStart"));
            DateTime? cycleEnd = ParseCycleDate(Json.Get(root, "billingCycleEnd"));

            var planUsage = Json.GetObject(root, "planUsage");
            double? percent = Json.GetDouble(planUsage, "totalPercentUsed");
            if (!percent.HasValue) percent = Json.GetDouble(root, "totalPercentUsed");
            if (!percent.HasValue)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.Message = "unexpected response";
                return snapshot;
            }

            var window = new UsageWindow();
            window.Kind = WindowKind.Monthly;
            window.Key = "cycle";
            window.Label = "billing cycle";
            window.UsedPercent = percent.Value;
            double? used = Json.GetDouble(planUsage, "totalSpend");
            double? limit = Json.GetDouble(planUsage, "limit");
            if (used.HasValue || limit.HasValue)
            {
                window.Used = used;
                window.Limit = limit;
                window.Unit = "cents";
            }
            if (cycleStart.HasValue && cycleEnd.HasValue)
            {
                double seconds = (cycleEnd.Value - cycleStart.Value).TotalSeconds;
                if (seconds > 0) window.DurationSeconds = (int)Math.Round(seconds);
            }
            window.ResetsAtUtc = cycleEnd;
            snapshot.Windows.Add(window);
            snapshot.Status = ProviderStatus.Ok;
            return snapshot;
        }

        // ISO 8601 text or epoch milliseconds, as a JSON number or a numeric string.
        static DateTime? ParseCycleDate(object value)
        {
            if (value == null) return null;
            string text = value as string;
            if (text == null)
            {
                double? ms = ToDouble(value);
                return ms.HasValue ? FromEpochMs(ms.Value) : (DateTime?)null;
            }
            if (text.Length == 0) return null;
            DateTime? iso = ParseIso(text);
            if (iso.HasValue) return iso;
            double? msText = ToDouble(text);
            return msText.HasValue ? FromEpochMs(msText.Value) : (DateTime?)null;
        }

        static DateTime? ParseIso(string text)
        {
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

        static double? ToDouble(object value)
        {
            if (value == null || value is bool) return null;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (InvalidCastException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        static DateTime FromEpochMs(double ms)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
        }

        // The access token is a JWT; its payload carries exp (epoch seconds). No signature
        // check — the token is only judged fresh enough to show usage. Anything unreadable
        // counts as not expired so the endpoint itself gets the final say.
        public static bool IsTokenExpired(string token, DateTime nowUtc)
        {
            double? exp = TokenExpiresAt(token);
            if (!exp.HasValue) return false;
            return exp.Value <= (nowUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalSeconds;
        }

        static double? TokenExpiresAt(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            string[] parts = token.Split('.');
            if (parts.Length < 2) return null;
            string payloadText = DecodeBase64Url(parts[1]);
            if (payloadText == null) return null;
            var payload = Json.ParseObject(payloadText);
            if (payload == null) return null;
            return Json.GetDouble(payload, "exp");
        }

        static string DecodeBase64Url(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string base64 = text.Replace('-', '+').Replace('_', '/');
            int mod = base64.Length % 4;
            if (mod == 2) base64 += "==";
            else if (mod == 3) base64 += "=";
            else if (mod == 1) return null;
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
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
            return Snap(ProviderStatus.AuthRequired, "sign in needed: open Cursor");
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
