using System;
using System.Collections.Generic;

namespace AiUsageWidget.Core
{
    public enum WindowKind
    {
        FiveHour,
        Daily,
        Weekly,
        Monthly,
        Other
    }

    public enum ProviderStatus
    {
        Ok,
        Stale,
        AuthRequired,
        NotConfigured,
        Error
    }

    public sealed class UsageWindow
    {
        public string Key;
        public string Label;
        public bool Primary = true;
        public int? DurationSeconds;
        public string Unit;
        public WindowKind Kind;
        public double UsedPercent;        // 0..100, always set
        public double? Used;              // absolute, only when the vendor reports it
        public double? Limit;
        public DateTime? ResetsAtUtc;

        public static WindowKind KindForDuration(double? seconds)
        {
            if (!seconds.HasValue) return WindowKind.Other;
            double value = seconds.Value;
            if (Math.Abs(value - 18000) <= 1800) return WindowKind.FiveHour;
            if (Math.Abs(value - 86400) <= 8640) return WindowKind.Daily;
            if (Math.Abs(value - 604800) <= 60480) return WindowKind.Weekly;
            if (value >= 2419200) return WindowKind.Monthly;
            return WindowKind.Other;
        }
    }

    public sealed class UsageSnapshot
    {
        public string ProviderId;         // "codex"
        public string DisplayName;        // "Codex"
        public string PlanName;           // "Pro Lite"; empty when unknown
        public List<UsageWindow> Windows; // only the windows this plan has; never null
        public DateTime FetchedAtUtc;
        public ProviderStatus Status;
        public string Message;            // short, user-facing; never contains a token

        public UsageSnapshot()
        {
            PlanName = "";
            Message = "";
            Windows = new List<UsageWindow>();
            FetchedAtUtc = DateTime.MinValue;
        }
    }
}
