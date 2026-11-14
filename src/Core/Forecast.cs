using System;
using System.Collections.Generic;

namespace AiUsageWidget.Core
{
    // What Project concludes about one window; null / false / "" all mean "unknown".
    public sealed class ForecastResult
    {
        public double? RatePerHour;      // recent burn rate, percent per hour
        public DateTime? HitsLimitAtUtc; // when 100 % would be reached at that rate
        public bool BeforeReset;         // the limit is reached before the window resets
        public string Text;              // "limit in 1h 20m", or "" when nothing to announce
    }

    // Pace and forecast, computed from a window plus its recorded history. Pure: no clock,
    // no file access, no UI.
    public static class Forecast
    {
        // A rate this slow would need more than a century to reach the limit: not a
        // forecast. The bound also keeps an absurd rate from overflowing DateTime.
        const double MaxHoursToLimit = 100.0 * 366.0 * 24.0;

        // The percent that would be used by now at an even rate, given the window started
        // at ResetsAtUtc - DurationSeconds. Null when either is unknown; clamped to 0..100.
        public static double? PacePercent(UsageWindow w, DateTime nowUtc)
        {
            if (w == null || !w.ResetsAtUtc.HasValue || !w.DurationSeconds.HasValue) return null;
            double duration = w.DurationSeconds.Value;
            if (duration <= 0) return null;
            DateTime start = w.ResetsAtUtc.Value.AddSeconds(-duration);
            double elapsed = (nowUtc - start).TotalSeconds;
            if (elapsed < 0) elapsed = 0;
            double pace = elapsed / duration * 100.0;
            if (pace > 100) pace = 100;
            return pace;
        }

        // Projects the window's burn from its history: a least-squares rate over the last
        // 90 minutes when there are enough samples, otherwise the even pace since the
        // window start, otherwise nothing.
        public static ForecastResult Project(UsageWindow w, IList<HistorySample> samples,
            DateTime nowUtc)
        {
            var result = new ForecastResult();
            result.Text = "";
            if (w == null) return result;

            DateTime? start = null;
            if (w.ResetsAtUtc.HasValue && w.DurationSeconds.HasValue && w.DurationSeconds.Value > 0)
                start = w.ResetsAtUtc.Value.AddSeconds(-w.DurationSeconds.Value);

            // Only the current window generation counts: a sample belongs when it carries
            // the same reset time, or was taken at or after the window start.
            var current = new List<HistorySample>();
            if (samples != null)
                foreach (HistorySample s in samples)
                {
                    if (s == null) continue;
                    bool sameReset = w.ResetsAtUtc.HasValue && s.ResetsAtUtc.HasValue
                        && s.ResetsAtUtc.Value == w.ResetsAtUtc.Value;
                    bool inWindow = sameReset || (start.HasValue && s.TUtc >= start.Value);
                    if (inWindow) current.Add(s);
                }
            current.Sort(delegate(HistorySample a, HistorySample b)
            {
                return a.TUtc.CompareTo(b.TUtc);
            });

            double? rate = null;
            List<HistorySample> tail = Since(current, nowUtc.AddMinutes(-90));
            if (tail.Count >= 3
                && (tail[tail.Count - 1].TUtc - tail[0].TUtc).TotalMinutes >= 10)
                rate = SlopePerHour(tail);

            if (!rate.HasValue)
            {
                double? pace = PacePercent(w, nowUtc);
                if (pace.HasValue && start.HasValue)
                {
                    double hours = (nowUtc - start.Value).TotalHours;
                    if (hours > 0) rate = pace.Value / hours;
                }
            }
            if (!rate.HasValue) return result;
            result.RatePerHour = rate;

            // A limit only "hits" at a real rate while usage is still below 100 %.
            if (rate.Value > 0 && w.UsedPercent < 100)
            {
                double hoursToLimit = (100 - w.UsedPercent) / rate.Value;
                if (hoursToLimit <= MaxHoursToLimit)
                {
                    result.HitsLimitAtUtc = nowUtc.AddHours(hoursToLimit);
                    result.BeforeReset = w.ResetsAtUtc.HasValue
                        && result.HitsLimitAtUtc.Value < w.ResetsAtUtc.Value;
                    if (result.BeforeReset)
                        result.Text = "limit in " + Painter.Duration(
                            result.HitsLimitAtUtc.Value - nowUtc);
                }
            }
            return result;
        }

        static List<HistorySample> Since(List<HistorySample> ordered, DateTime cutoff)
        {
            var tail = new List<HistorySample>();
            foreach (HistorySample s in ordered)
                if (s.TUtc >= cutoff) tail.Add(s);
            return tail;
        }

        // Least-squares slope of percent over hours.
        static double SlopePerHour(List<HistorySample> ordered)
        {
            DateTime t0 = ordered[0].TUtc;
            double n = ordered.Count;
            double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
            foreach (HistorySample s in ordered)
            {
                double x = (s.TUtc - t0).TotalHours;
                sumX += x;
                sumY += s.UsedPercent;
                sumXX += x * x;
                sumXY += x * s.UsedPercent;
            }
            double denom = n * sumXX - sumX * sumX;
            if (denom <= 0) return 0;   // one instant only; callers require a span first
            return (n * sumXY - sumX * sumY) / denom;
        }
    }
}
