using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace AiUsageWidget.Core
{
    // All on-disk locations. The self-test sets AI_USAGE_WIDGET_DATA to a temp folder so the
    // real profile is never touched.
    public static class AppPaths
    {
        public static string DataDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AI_USAGE_WIDGET_DATA");
                if (!string.IsNullOrEmpty(env)) return env;
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(root, "AiUsageWidget");
            }
        }

        public static string SettingsPath { get { return Path.Combine(DataDir, "settings.json"); } }

        public static string CachePath { get { return Path.Combine(DataDir, "cache.json"); } }

        public static string LogPath { get { return Path.Combine(DataDir, "log.txt"); } }

        public static string HistoryPath { get { return Path.Combine(DataDir, "history.json"); } }

        // One DPAPI-protected file per named secret (see SecretStore).
        public static string SecretPath(string name)
        {
            return Path.Combine(DataDir, "secret-" + name + ".bin");
        }

        public static string UpdateDir { get { return Path.Combine(DataDir, "update"); } }

        public static string TokenPath { get { return Path.Combine(DataDir, "update-token.bin"); } }

        // The .old file lives next to the running exe, not in the data dir.
        public static string RunningExePath
        {
            get { return new Uri(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).LocalPath; }
        }

        public static string OldExePath
        {
            get
            {
                string exe = RunningExePath;
                string name = Path.GetFileNameWithoutExtension(exe) + ".old" + Path.GetExtension(exe);
                return Path.Combine(Path.GetDirectoryName(exe), name);
            }
        }
    }

    public sealed class ProviderSetting
    {
        public string Id;
        public bool Enabled;
    }

    public sealed class UpdateSettings
    {
        public string Mode;          // auto | notify | off
        public string Repo;          // "owner/name"; empty disables the updater
        public bool RequiresToken;

        public UpdateSettings()
        {
            Mode = "notify";   // install only after the user clicks; existing files keep their mode
            Repo = "SalahSaad93/ai-usage-widget";
            // Private-repo updates still work when requiresToken is true.
            RequiresToken = false;
        }
    }

    public sealed class NotificationSettings
    {
        public bool Enabled = true;
        public List<int> Thresholds = new List<int> { 80, 95 };   // percent used; crossing one notifies
        public bool OnReset;
    }

    public sealed class Settings
    {
        public string Theme = "system";        // system | dark | light
        public int Opacity = 100;              // 40..100, percent
        public int WarnPercent = 50;           // bars turn yellow from here
        public int CriticalPercent = 70;       // and red above this
        public NotificationSettings Notifications = new NotificationSettings();
        public int HistoryDays = 30;           // how long usage samples are kept
        public bool TrayHintShown;             // the one-time "still running in the tray" balloon
        public bool ConsentDone;               // the first-run "choose what to show" window was answered
        public int RefreshMinutes = 5;
        public string Layout = "detailed";
        public bool AlwaysOnTop = true;
        public Rectangle Bounds = new Rectangle(0, 0, 340, 560);
        public bool HasSavedBounds;   // false on first run: the window goes bottom-right
        public bool AutoDetect = true;
        public List<ProviderSetting> Providers;
        public List<string> DismissedSuggestions = new List<string>();
        public List<string> AnnouncedSuggestions = new List<string>();
        public List<string> KnownProviders = new List<string>();
        public UpdateSettings Update;

        // False when no settings file existed yet (the very first run) — used to keep the
        // detection balloons quiet on that run (spec section 13).
        public bool HasFile { get; private set; }

        // The last parsed file; unknown keys are merged back on save.
        Dictionary<string, object> raw;

        public Settings()
        {
            // Empty by default: MergeProviders then returns every provider, enabled, in
            // registry order. Only an explicit providers list in settings.json reorders.
            Providers = new List<ProviderSetting>();
            Update = new UpdateSettings();
        }

        public static Settings Load(string path)
        {
            var settings = new Settings();
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                return settings;   // missing or unreadable -> defaults
            }
            catch (UnauthorizedAccessException)
            {
                return settings;
            }
            Dictionary<string, object> d = Json.ParseObject(text);
            if (d == null) return settings;   // malformed -> defaults
            settings.raw = d;
            settings.HasFile = true;
            settings.FromJson(d);
            return settings;
        }

        void FromJson(Dictionary<string, object> d)
        {
            RefreshMinutes = Clamp(Json.GetInt(d, "refreshMinutes", 5), 1, 120);
            string layout = Json.GetStr(d, "layout");
            if (layout == "detailed" || layout == "compact") Layout = layout;
            AlwaysOnTop = Json.GetBool(d, "alwaysOnTop", true);

            var boundsDict = Json.GetObject(d, "bounds");
            if (boundsDict != null)
            {
                HasSavedBounds = true;
                Bounds = new Rectangle(
                    Json.GetInt(boundsDict, "x", 0),
                    Json.GetInt(boundsDict, "y", 0),
                    Json.GetInt(boundsDict, "width", 340),
                    Json.GetInt(boundsDict, "height", 560));
            }

            List<object> providersJson = Json.GetArray(d, "providers");
            if (providersJson != null)
            {
                var list = new List<ProviderSetting>();
                foreach (object o in providersJson)
                {
                    var pd = o as Dictionary<string, object>;
                    if (pd == null) continue;
                    string id = Json.GetStr(pd, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var ps = new ProviderSetting();
                    ps.Id = id;
                    ps.Enabled = Json.GetBool(pd, "enabled", true);
                    list.Add(ps);
                }
                if (list.Count > 0) Providers = list;
            }

            AutoDetect = Json.GetBool(d, "autoDetect", true);

            string theme = Json.GetStr(d, "theme");
            if (theme == "system" || theme == "dark" || theme == "light") Theme = theme;
            Opacity = Clamp(Json.GetInt(d, "opacity", 100), 40, 100);
            WarnPercent = Clamp(Json.GetInt(d, "warnPercent", 50), 1, 98);
            CriticalPercent = Clamp(Json.GetInt(d, "criticalPercent", 70), WarnPercent + 1, 99);
            HistoryDays = Clamp(Json.GetInt(d, "historyDays", 30), 1, 365);
            TrayHintShown = Json.GetBool(d, "trayHintShown", false);
            ConsentDone = Json.GetBool(d, "consentDone", false);
            var notifyIn = Json.GetObject(d, "notifications");
            if (notifyIn != null)
            {
                Notifications.Enabled = Json.GetBool(notifyIn, "enabled", true);
                Notifications.OnReset = Json.GetBool(notifyIn, "onReset", false);
                List<object> thresholds = Json.GetArray(notifyIn, "thresholds");
                if (thresholds != null)
                {
                    var parsed = new List<int>();
                    foreach (object o in thresholds)
                    {
                        int value;
                        try
                        {
                            value = Convert.ToInt32(o, System.Globalization.CultureInfo.InvariantCulture);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (value >= 1 && value <= 100 && !parsed.Contains(value)) parsed.Add(value);
                    }
                    parsed.Sort();
                    Notifications.Thresholds = parsed;
                }
            }
            List<string> dismissed = GetStringList(d, "dismissedSuggestions");
            if (dismissed != null) DismissedSuggestions = dismissed;
            List<string> announced = GetStringList(d, "announcedSuggestions");
            if (announced != null) AnnouncedSuggestions = announced;
            List<string> known = GetStringList(d, "knownProviders");
            if (known != null) KnownProviders = known;

            var updateDict = Json.GetObject(d, "update");
            if (updateDict != null)
            {
                string mode = Json.GetStr(updateDict, "mode");
                if (mode == "auto" || mode == "notify" || mode == "off") Update.Mode = mode;
                string repo = Json.GetStr(updateDict, "repo");
                if (repo != null) Update.Repo = repo;
                Update.RequiresToken = Json.GetBool(updateDict, "requiresToken", false);
            }
        }

        static List<string> GetStringList(Dictionary<string, object> d, string key)
        {
            List<object> raw = Json.GetArray(d, key);
            if (raw == null) return null;
            var list = new List<string>();
            foreach (object o in raw)
            {
                string s = o as string;
                if (string.IsNullOrEmpty(s)) continue;
                if (list.Contains(s)) continue;   // de-duplicated, order kept
                list.Add(s);
            }
            return list;
        }

        public void Save(string path)
        {
            var outDict = raw != null ? new Dictionary<string, object>(raw) : new Dictionary<string, object>();
            outDict["refreshMinutes"] = Clamp(RefreshMinutes, 1, 120);
            outDict["layout"] = Layout;
            outDict["alwaysOnTop"] = AlwaysOnTop;

            var boundsDict = Json.GetObject(raw, "bounds");
            if (boundsDict == null) boundsDict = new Dictionary<string, object>();
            boundsDict["x"] = Bounds.X;
            boundsDict["y"] = Bounds.Y;
            boundsDict["width"] = Bounds.Width;
            boundsDict["height"] = Bounds.Height;
            outDict["bounds"] = boundsDict;

            var providersJson = new List<object>();
            foreach (ProviderSetting p in Providers)
            {
                var pd = new Dictionary<string, object>();
                pd["id"] = p.Id;
                pd["enabled"] = p.Enabled;
                providersJson.Add(pd);
            }
            outDict["providers"] = providersJson;

            var updateDict = Json.GetObject(raw, "update");
            if (updateDict == null) updateDict = new Dictionary<string, object>();
            updateDict["mode"] = Update.Mode;
            updateDict["repo"] = Update.Repo;
            updateDict["requiresToken"] = Update.RequiresToken;
            outDict["update"] = updateDict;

            outDict["autoDetect"] = AutoDetect;
            outDict["theme"] = Theme;
            outDict["opacity"] = Clamp(Opacity, 40, 100);
            outDict["warnPercent"] = WarnPercent;
            outDict["criticalPercent"] = CriticalPercent;
            outDict["historyDays"] = HistoryDays;
            outDict["trayHintShown"] = TrayHintShown;
            outDict["consentDone"] = ConsentDone;
            var notifyOut = Json.GetObject(raw, "notifications");
            if (notifyOut == null) notifyOut = new Dictionary<string, object>();
            notifyOut["enabled"] = Notifications.Enabled;
            notifyOut["onReset"] = Notifications.OnReset;
            notifyOut["thresholds"] = new List<int>(Notifications.Thresholds);
            outDict["notifications"] = notifyOut;
            outDict["dismissedSuggestions"] = new List<string>(DismissedSuggestions);
            outDict["announcedSuggestions"] = new List<string>(AnnouncedSuggestions);
            outDict["knownProviders"] = new List<string>(KnownProviders);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            // Atomic (temp file, then replace) and indented, since hand-editing is how most
            // settings are changed.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.SerializeIndented(outDict));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // settings.json controls order and enablement; registered providers missing from the
        // list are appended enabled.
        public List<IUsageProvider> MergeProviders(IUsageProvider[] registered)
        {
            return SelectShown(registered, Providers, false, null);
        }

        // Section 13's shown/hidden rule, pure: registry order in, ordered shown list out.
        // Listed-and-enabled shows always (detected or not); listed-disabled hides; unlisted
        // shows when autoDetect is off (the pre-detection behaviour) or when detected.
        // Unlisted experimental providers (ProviderRegistry.ExperimentalIds) never show,
        // whatever autoDetect and detection say.
        public static List<IUsageProvider> SelectShown(IUsageProvider[] registered,
            List<ProviderSetting> entries, bool autoDetect, Func<string, bool> isDetected)
        {
            var list = new List<IUsageProvider>();
            var byId = new Dictionary<string, IUsageProvider>();
            foreach (IUsageProvider p in registered) byId[p.Id] = p;
            var listed = new HashSet<string>();
            if (entries != null)
                foreach (ProviderSetting ps in entries)
                {
                    IUsageProvider p;
                    if (!byId.TryGetValue(ps.Id, out p)) continue;
                    listed.Add(ps.Id);
                    if (ps.Enabled) list.Add(p);
                }
            foreach (IUsageProvider p in registered)
            {
                if (listed.Contains(p.Id)) continue;
                if (Array.IndexOf(ProviderRegistry.ExperimentalIds, p.Id) >= 0) continue;
                if (autoDetect && isDetected != null && !isDetected(p.Id)) continue;
                list.Add(p);
            }
            return list;
        }

        public static int Clamp(int value, int lo, int hi)
        {
            return value < lo ? lo : (value > hi ? hi : value);
        }
    }
}
