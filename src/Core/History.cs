using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AiUsageWidget.Core
{
    // One usage point of one window: percent used at a time, with the window's reset
    // instant as it was then (Forecast uses the reset to tell window generations apart).
    public sealed class HistorySample
    {
        public DateTime TUtc;          // sample time, UTC
        public double UsedPercent;     // 0..100, rounded to 0.01 on record
        public DateTime? ResetsAtUtc;  // the window's reset instant, when the vendor gave one
    }

    // history.json: per-window usage samples keyed "providerId/windowKey". Samples are
    // compact JSON arrays [epochSeconds, usedPercent, resetEpochSeconds|null] rather than
    // objects, so a month of samples stays far below 1 MB. The file is written atomically
    // (temp file, then replace, like SnapshotCache). Load and Save never throw for I/O
    // problems; they log through Log.Error instead.
    public sealed class History
    {
        readonly string path;
        readonly Func<DateTime> nowUtc;
        readonly Dictionary<string, List<HistorySample>> series;

        public History(string path, Func<DateTime> nowUtc)
        {
            this.path = path;
            this.nowUtc = nowUtc;
            series = new Dictionary<string, List<HistorySample>>(StringComparer.Ordinal);
        }

        // Convenience overloads that read the clock injected in the constructor.
        public void Record(IList<UsageSnapshot> snapshots)
        {
            Record(snapshots, ClockNow());
        }

        public void Prune(int keepDays)
        {
            Prune(ClockNow(), keepDays);
        }

        public void Record(IList<UsageSnapshot> snapshots, DateTime nowUtc)
        {
            if (snapshots == null) return;
            foreach (UsageSnapshot s in snapshots)
            {
                if (s == null || s.Status != ProviderStatus.Ok) continue;
                foreach (UsageWindow w in s.Windows)
                {
                    if (w == null || string.IsNullOrEmpty(w.Key)) continue;
                    Append(KeyOf(s.ProviderId, w.Key), nowUtc, w.UsedPercent, w.ResetsAtUtc);
                }
            }
        }

        public List<HistorySample> Samples(string providerId, string windowKey)
        {
            List<HistorySample> list;
            if (!series.TryGetValue(KeyOf(providerId, windowKey), out list) || list.Count == 0)
                return new List<HistorySample>();
            return new List<HistorySample>(list);   // insertion order: oldest first
        }

        public void Prune(DateTime nowUtc, int keepDays)
        {
            DateTime cutoff = nowUtc.AddDays(-keepDays);
            var empty = new List<string>();
            foreach (KeyValuePair<string, List<HistorySample>> kv in series)
            {
                kv.Value.RemoveAll(delegate(HistorySample s) { return s.TUtc < cutoff; });
                if (kv.Value.Count == 0) empty.Add(kv.Key);
            }
            foreach (string key in empty) series.Remove(key);
        }

        public void Load()
        {
            series.Clear();
            if (string.IsNullOrEmpty(path)) return;
            string text;
            try
            {
                if (!File.Exists(path)) return;   // first run: no history yet
                text = File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                Log.Error("history load failed", ex);
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("history load failed", ex);
                return;
            }

            Dictionary<string, object> root = Json.ParseObject(text);
            if (root == null) return;   // malformed: start empty
            Dictionary<string, object> loaded = Json.GetObject(root, "series");
            if (loaded == null) return;
            foreach (KeyValuePair<string, object> kv in loaded)
            {
                List<object> points = AsArray(kv.Value);
                if (points == null) continue;
                var list = new List<HistorySample>();
                foreach (object point in points)
                {
                    List<object> fields = AsArray(point);
                    if (fields == null || fields.Count < 2) continue;
                    double? t = ToDouble(fields[0]);
                    double? percent = ToDouble(fields[1]);
                    if (!t.HasValue || !percent.HasValue) continue;
                    var sample = new HistorySample();
                    sample.TUtc = FromEpochSeconds(t.Value);
                    sample.UsedPercent = percent.Value;
                    if (fields.Count >= 3 && fields[2] != null)
                    {
                        double? reset = ToDouble(fields[2]);
                        if (reset.HasValue) sample.ResetsAtUtc = FromEpochSeconds(reset.Value);
                    }
                    list.Add(sample);
                }
                if (list.Count > 0) series[kv.Key] = list;
            }
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var outSeries = new Dictionary<string, object>();
                foreach (KeyValuePair<string, List<HistorySample>> kv in series)
                {
                    if (kv.Value == null || kv.Value.Count == 0) continue;
                    var points = new List<object>();
                    foreach (HistorySample s in kv.Value)
                    {
                        var fields = new List<object>();
                        fields.Add(EpochSeconds(s.TUtc));
                        fields.Add(s.UsedPercent);
                        fields.Add(s.ResetsAtUtc.HasValue
                            ? (object)EpochSeconds(s.ResetsAtUtc.Value) : null);
                        points.Add(fields);
                    }
                    outSeries[kv.Key] = points;
                }
                var root = new Dictionary<string, object>();
                root["version"] = 1;
                root["series"] = outSeries;

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(root));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (IOException ex)
            {
                Log.Error("history save failed", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("history save failed", ex);
            }
        }

        // Appends unless the last sample already says the same thing within 30 minutes:
        // a new sample needs a changed percent, a changed reset time, or an aging last one.
        void Append(string key, DateTime at, double usedPercent, DateTime? resetsAtUtc)
        {
            List<HistorySample> list;
            if (!series.TryGetValue(key, out list))
            {
                list = new List<HistorySample>();
                series[key] = list;
            }
            double percent = Math.Round(usedPercent, 2);
            if (list.Count > 0)
            {
                HistorySample last = list[list.Count - 1];
                bool same = last.UsedPercent == percent
                    && Nullable.Equals(last.ResetsAtUtc, resetsAtUtc);
                bool aged = (at - last.TUtc).TotalMinutes > 30;
                if (same && !aged) return;
            }
            var sample = new HistorySample();
            sample.TUtc = at;
            sample.UsedPercent = percent;
            sample.ResetsAtUtc = resetsAtUtc;
            list.Add(sample);
        }

        static string KeyOf(string providerId, string windowKey)
        {
            return (providerId ?? "") + "/" + windowKey;
        }

        DateTime ClockNow()
        {
            return nowUtc != null ? nowUtc() : DateTime.UtcNow;
        }

        static List<object> AsArray(object value)
        {
            var arr = value as object[];
            if (arr != null) return new List<object>(arr);
            var seq = value as System.Collections.IEnumerable;
            if (seq == null) return null;
            var list = new List<object>();
            foreach (object item in seq) list.Add(item);
            return list;
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

        static long EpochSeconds(DateTime utc)
        {
            TimeSpan t = utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)t.TotalSeconds;
        }

        static DateTime FromEpochSeconds(double seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }
    }
}
