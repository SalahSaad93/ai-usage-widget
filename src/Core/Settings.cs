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
            Mode = "auto";
            Repo = "SalahSaad93/ai-usage-widget";
            RequiresToken = true;
        }
    }

    public sealed class Settings
    {
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
                // The pre-0.2.2 default was exactly [{"id":"codex","enabled":true}] — never a
                // user choice; treat it as empty so the registry order keeps applying.
                if (list.Count == 1 && list[0].Id == "codex" && list[0].Enabled) list.Clear();
                if (list.Count > 0) Providers = list;
            }

            AutoDetect = Json.GetBool(d, "autoDetect", true);
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
                Update.RequiresToken = Json.GetBool(updateDict, "requiresToken", true);
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
                if (!string.IsNullOrEmpty(s)) list.Add(s);
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
            outDict["dismissedSuggestions"] = new List<string>(DismissedSuggestions);
            outDict["announcedSuggestions"] = new List<string>(AnnouncedSuggestions);
            outDict["knownProviders"] = new List<string>(KnownProviders);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Json.Serialize(outDict));
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
