using System;
using System.Collections.Generic;
using System.Globalization;

namespace AiUsageWidget.Core
{
    // Threshold and reset notifications. The Notifier remembers the last seen percent and
    // reset time of every window, so Evaluate can be called once per refresh with fresh
    // snapshots and returns exactly the messages that should be shown now.
    public sealed class Notifier
    {
        sealed class Seen
        {
            public double? Percent;
            public DateTime? ResetsAtUtc;
        }

        readonly Dictionary<string, Seen> seen = new Dictionary<string, Seen>();

        public List<string> Evaluate(IList<UsageSnapshot> snapshots, NotificationSettings cfg,
            DateTime nowUtc)
        {
            var messages = new List<string>();
            if (snapshots == null) return messages;
            bool enabled = cfg != null && cfg.Enabled;

            foreach (UsageSnapshot s in snapshots)
            {
                if (s == null || s.Status != ProviderStatus.Ok) continue;
                foreach (UsageWindow w in s.Windows)
                {
                    if (w == null) continue;
                    string key = StateKey(s, w);
                    Seen prev;
                    bool first = !seen.TryGetValue(key, out prev);
                    if (first) prev = new Seen();

                    if (enabled)
                    {
                        string name = string.IsNullOrEmpty(s.DisplayName)
                            ? s.ProviderId : s.DisplayName;
                        string label = string.IsNullOrEmpty(w.Label)
                            ? Painter.DefaultLabel(w.Kind) : w.Label;

                        // Reset: the window's reset instant moved forward while usage fell
                        // by 20 points or more.
                        if (cfg.OnReset && prev.Percent.HasValue && prev.ResetsAtUtc.HasValue
                            && w.ResetsAtUtc.HasValue
                            && w.ResetsAtUtc.Value > prev.ResetsAtUtc.Value
                            && prev.Percent.Value - w.UsedPercent >= 20)
                        {
                            messages.Add(name + " " + label + ": reset");
                        }

                        // Threshold crossing: previous below, current at or above. Several
                        // thresholds crossed at once announce once, for the highest.
                        int? crossed = null;
                        if (prev.Percent.HasValue && cfg.Thresholds != null)
                        {
                            foreach (int t in cfg.Thresholds)
                                if (prev.Percent.Value < t && w.UsedPercent >= t
                                    && (!crossed.HasValue || t > crossed.Value))
                                    crossed = t;
                        }
                        if (crossed.HasValue)
                        {
                            string message = name + " " + label + ": "
                                + Math.Round(w.UsedPercent).ToString(CultureInfo.InvariantCulture)
                                + "% used";
                            if (w.ResetsAtUtc.HasValue)
                                message += ", resets in " + Painter.Duration(
                                    w.ResetsAtUtc.Value - nowUtc);
                            messages.Add(message);
                        }
                    }

                    // The state advances whether or not anything was announced, and while
                    // notifications are disabled; a reset therefore re-arms the thresholds.
                    var next = new Seen();
                    next.Percent = w.UsedPercent;
                    next.ResetsAtUtc = w.ResetsAtUtc;
                    seen[key] = next;
                }
            }
            return messages;
        }

        // A window is identified by providerId/key; a window without a key falls back to
        // its kind so its state is still tracked.
        static string StateKey(UsageSnapshot s, UsageWindow w)
        {
            string windowKey = string.IsNullOrEmpty(w.Key) ? w.Kind.ToString() : w.Key;
            return (s.ProviderId ?? "") + "/" + windowKey;
        }
    }
}
