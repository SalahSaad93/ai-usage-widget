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
        public List<ProviderSetting> Providers;
        public UpdateSettings Update;

        // The last parsed file; unknown keys are merged back on save.
        Dictionary<string, object> raw;

        public Settings()
        {
            Providers = new List<ProviderSetting>();
            Providers.Add(new ProviderSetting { Id = "codex", Enabled = true });
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

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Json.Serialize(outDict));
        }

        // settings.json controls order and enablement; registered providers missing from the
        // list are appended enabled.
        public List<IUsageProvider> MergeProviders(IUsageProvider[] registered)
        {
            var byId = new Dictionary<string, IUsageProvider>();
            foreach (IUsageProvider p in registered) byId[p.Id] = p;
            var list = new List<IUsageProvider>();
            var used = new HashSet<string>();
            foreach (ProviderSetting ps in Providers)
            {
                IUsageProvider p;
                if (!byId.TryGetValue(ps.Id, out p)) continue;
                used.Add(ps.Id);
                if (ps.Enabled) list.Add(p);
            }
            foreach (IUsageProvider p in registered)
                if (!used.Contains(p.Id)) list.Add(p);
            return list;
        }

        public static int Clamp(int value, int lo, int hi)
        {
            return value < lo ? lo : (value > hi ? hi : value);
        }
    }
}
