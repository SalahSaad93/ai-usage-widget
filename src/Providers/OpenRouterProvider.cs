using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // OpenRouter key usage (official, documented API). The key is read on each fetch from
    // OPENROUTER_API_KEY or the widget's own secret store, kept in local variables, never
    // logged, never cached, never written.
    public sealed class OpenRouterProvider : IUsageProvider
    {
        public const string UsageUrl = "https://openrouter.ai/api/v1/key";
        public const string EnvVarName = "OPENROUTER_API_KEY";
        public const string SecretName = "openrouter";

        public string Id { get { return "openrouter"; } }
        public string DisplayName { get { return "OpenRouter"; } }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            string key = KeyFromEnvironment() ?? KeyFromStore();
            if (key == null) return NotConfigured();

            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + key));

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

            if (resp.Status == 401 || resp.Status == 403) return AuthRequired();
            if (resp.Status != 200)
                return Err(Errors.StatusText(resp.Status));
            return Parse(resp.Text, DateTime.UtcNow);
        }

        // Detected when either key source exists; the value is only read for the header.
        public bool IsDetected()
        {
            return KeyFromEnvironment() != null || KeyFromStore() != null;
        }

        static string KeyFromEnvironment()
        {
            string value = Environment.GetEnvironmentVariable(EnvVarName);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        static string KeyFromStore()
        {
            return SecretStore.Load(SecretName);
        }

        // Pure: no I/O, no clock (the reset time is computed from the injected now).
        // RefreshService stamps FetchedAtUtc.
        public static UsageSnapshot Parse(string json, DateTime nowUtc)
        {
            var snapshot = new UsageSnapshot();
            snapshot.ProviderId = "openrouter";
            snapshot.DisplayName = "OpenRouter";

            var data = Json.GetObject(Json.ParseObject(json), "data");
            if (data == null)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.Message = "unexpected response";
                return snapshot;
            }

            // "Free" when the key is on the free tier, else the plan is unknown.
            snapshot.PlanName = Json.GetBool(data, "is_free_tier", false) ? "Free" : "";

            double? limit = Json.GetDouble(data, "limit");
            double? remaining = Json.GetDouble(data, "limit_remaining");
            string reset = Json.GetStr(data, "limit_reset");

            // limit and limit_remaining are null together for a key without a credit limit.
            if (limit.HasValue && remaining.HasValue)
            {
                double used = limit.Value - remaining.Value;
                var window = new UsageWindow();
                window.Kind = KindForReset(reset);
                window.Key = "limit";
                window.Label = "key limit";
                window.DurationSeconds = DurationForReset(reset);
                window.Used = used;
                window.Limit = limit.Value;
                window.Unit = "USD";
                window.UsedPercent = limit.Value > 0 ? used * 100.0 / limit.Value : 0;
                window.ResetsAtUtc = NextResetUtc(nowUtc, reset);
                snapshot.Windows.Add(window);
                snapshot.Status = ProviderStatus.Ok;
                return snapshot;
            }

            double? monthly = Json.GetDouble(data, "usage_monthly");
            double usedMonthly = monthly.HasValue ? monthly.Value : 0;
            snapshot.Status = ProviderStatus.Ok;
            snapshot.Message = "no limit set: $" +
                usedMonthly.ToString("F2", CultureInfo.InvariantCulture) + " used this month";
            return snapshot;
        }

        static WindowKind KindForReset(string reset)
        {
            if (reset == "daily") return WindowKind.Daily;
            if (reset == "weekly") return WindowKind.Weekly;
            if (reset == "monthly") return WindowKind.Monthly;
            return WindowKind.Other;
        }

        static int? DurationForReset(string reset)
        {
            if (reset == "daily") return 86400;
            if (reset == "weekly") return 604800;
            if (reset == "monthly") return 2592000;
            return null;
        }

        // The API gives no wall-clock reset time, only a cadence: daily keys reset at the
        // next UTC midnight, weekly keys at the next Monday 00:00 UTC, monthly keys at the
        // first of the next month 00:00 UTC. An unknown cadence has no computable reset.
        public static DateTime? NextResetUtc(DateTime nowUtc, string reset)
        {
            if (reset == "daily")
                return StartOfDay(nowUtc).AddDays(1);
            if (reset == "weekly")
            {
                int days = ((int)DayOfWeek.Monday - (int)nowUtc.DayOfWeek + 7) % 7;
                if (days == 0) days = 7;   // Monday itself rolls a full week forward
                return StartOfDay(nowUtc).AddDays(days);
            }
            if (reset == "monthly")
            {
                return new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMonths(1);
            }
            return null;
        }

        static DateTime StartOfDay(DateTime utc)
        {
            return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
        }

        UsageSnapshot AuthRequired()
        {
            return Snap(ProviderStatus.AuthRequired, "check OpenRouter key");
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
