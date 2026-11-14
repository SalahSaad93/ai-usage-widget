using System;
using System.Collections.Generic;
using System.IO;

namespace AiUsageWidget.Core
{
    // cache.json: the last-known snapshot per provider, written atomically (temp file, then replace).
    public static class SnapshotCache
    {
        public static List<UsageSnapshot> Load(string path)
        {
            var snapshots = new List<UsageSnapshot>();
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                return snapshots;
            }
            catch (UnauthorizedAccessException)
            {
                return snapshots;
            }

            var root = Json.ParseObject(text);
            if (root == null) return snapshots;
            List<object> arr = Json.GetArray(root, "snapshots");
            if (arr == null) return snapshots;
            foreach (object o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                UsageSnapshot s = FromJson(d);
                if (s != null) snapshots.Add(s);
            }
            return snapshots;
        }

        public static void Save(string path, List<UsageSnapshot> snapshots)
        {
            var arr = new List<object>();
            foreach (UsageSnapshot s in snapshots) arr.Add(ToJson(s));
            var root = new Dictionary<string, object>();
            root["version"] = 1;
            root["snapshots"] = arr;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(root));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        static Dictionary<string, object> ToJson(UsageSnapshot s)
        {
            var d = new Dictionary<string, object>();
            d["providerId"] = s.ProviderId;
            d["displayName"] = s.DisplayName;
            d["planName"] = s.PlanName;
            d["fetchedAtUtc"] = EpochSeconds(s.FetchedAtUtc);
            d["status"] = s.Status.ToString();
            d["message"] = s.Message;
            var windows = new List<object>();
            foreach (UsageWindow w in s.Windows)
            {
                var wd = new Dictionary<string, object>();
                wd["kind"] = w.Kind.ToString();
                wd["key"] = w.Key;
                wd["label"] = w.Label;
                wd["primary"] = w.Primary;
                wd["durationSeconds"] = w.DurationSeconds.HasValue ? (object)w.DurationSeconds.Value : null;
                wd["unit"] = w.Unit;
                wd["usedPercent"] = w.UsedPercent;
                wd["used"] = w.Used.HasValue ? (object)w.Used.Value : null;
                wd["limit"] = w.Limit.HasValue ? (object)w.Limit.Value : null;
                wd["resetsAtUtc"] = w.ResetsAtUtc.HasValue ? (object)EpochSeconds(w.ResetsAtUtc.Value) : null;
                windows.Add(wd);
            }
            d["windows"] = windows;
            return d;
        }

        static UsageSnapshot FromJson(Dictionary<string, object> d)
        {
            string id = Json.GetStr(d, "providerId");
            if (string.IsNullOrEmpty(id)) return null;
            var s = new UsageSnapshot();
            s.ProviderId = id;
            s.DisplayName = Json.GetStr(d, "displayName") ?? "";
            s.PlanName = Json.GetStr(d, "planName") ?? "";
            s.Message = Json.GetStr(d, "message") ?? "";
            double? fetched = Json.GetDouble(d, "fetchedAtUtc");
            if (fetched.HasValue) s.FetchedAtUtc = FromEpochSeconds(fetched.Value);
            ProviderStatus status;
            s.Status = Enum.TryParse<ProviderStatus>(Json.GetStr(d, "status"), out status)
                ? status : ProviderStatus.Error;
            List<object> windows = Json.GetArray(d, "windows");
            if (windows != null)
            {
                foreach (object o in windows)
                {
                    var wd = o as Dictionary<string, object>;
                    if (wd == null) continue;
                    WindowKind kind;
                    if (!Enum.TryParse<WindowKind>(Json.GetStr(wd, "kind"), out kind)) continue;
                    double? percent = Json.GetDouble(wd, "usedPercent");
                    if (!percent.HasValue) continue;
                    var w = new UsageWindow();
                    w.Kind = kind;
                    w.Key = Json.GetStr(wd, "key");
                    w.Label = Json.GetStr(wd, "label");
                    w.Primary = !wd.ContainsKey("primary") || Json.GetBool(wd, "primary", true);
                    double? duration = Json.GetDouble(wd, "durationSeconds");
                    if (duration.HasValue) w.DurationSeconds = (int)duration.Value;
                    w.Unit = Json.GetStr(wd, "unit");
                    w.UsedPercent = percent.Value;
                    w.Used = Json.GetDouble(wd, "used");
                    w.Limit = Json.GetDouble(wd, "limit");
                    double? resets = Json.GetDouble(wd, "resetsAtUtc");
                    if (resets.HasValue) w.ResetsAtUtc = FromEpochSeconds(resets.Value);
                    s.Windows.Add(w);
                }
            }
            return s;
        }

        static double EpochSeconds(DateTime utc)
        {
            if (utc == DateTime.MinValue) return 0;
            TimeSpan t = utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return t.TotalSeconds;
        }

        static DateTime FromEpochSeconds(double seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }
    }
}
