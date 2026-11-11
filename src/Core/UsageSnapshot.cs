using System;
using System.Collections.Generic;

namespace AiUsageWidget.Core
{
    public enum WindowKind
    {
        FiveHour,
        Weekly,
        Monthly
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
        public WindowKind Kind;
        public double UsedPercent;        // 0..100, always set
        public double? Used;              // absolute, only when the vendor reports it
        public double? Limit;
        public DateTime? ResetsAtUtc;
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
