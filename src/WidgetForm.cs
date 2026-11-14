using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    public sealed class WidgetForm : Form
    {
        const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string RunValueName = "AiUsageWidget";

        readonly Settings settings;
        readonly RefreshService refresh;
        readonly Updater updater;
        readonly History history;                  // usage samples for pace and forecast
        readonly Notifier notifier = new Notifier();
        Painter painter;   // rebuilt on WM_DPICHANGED at the new scale
        readonly NotifyIcon tray = new NotifyIcon();
        Icon embeddedTrayIcon;     // the app icon; restored when there is no usage data
        Icon renderedTrayIcon;     // the live-drawn icon (owned); null means the embedded one is in use
        int renderedTrayPercent = -1;   // rounded percent the rendered icon was built for
        int renderedTrayColor;          // and its colour, ToArgb
        readonly Timer refreshTimer = new Timer();
        readonly Timer minuteTimer = new Timer();
        readonly Timer updateTimer = new Timer();
        readonly Timer noteTimer = new Timer();      // how long the "just refreshed" note stays up
        readonly Timer resumeTimer = new Timer();    // delayed refresh after resume / network return
        readonly IUsageProvider[] allProviders;   // ProviderRegistry.All allocates on every access
        List<IUsageProvider> activeProviders;   // recomputed at startup and at every refresh
        List<string> activeIds = new List<string>();   // ordered, as SelectShown returns them
        readonly Dictionary<string, IUsageProvider> allById = new Dictionary<string, IUsageProvider>();
        readonly Dictionary<string, bool> detectionCache = new Dictionary<string, bool>();
        List<string> suggestions = new List<string>();   // detected tools with no provider
        readonly string versionText;

        int mode;          // 0 = detailed, 1 = compact
        int scroll;
        int detailedWidth;   // last Detailed-mode width the user chose; default 340 scaled
        bool exitSignalled;
        bool justRefreshed;        // header note after a throttled manual refresh
        bool refreshWasBusy;       // to spot the Busy true->false edge in OnRefreshChanged
        bool inDpiChange;          // WM_DPICHANGED can re-enter via its own bounds change
        bool resumeRefreshPending; // coalesce resume/network events into one refresh
        bool settingsOpen;         // the settings dialog is up: never open a second one
        float dpiScale;            // kept so the painter can be rebuilt for theme changes
        bool lightTheme;           // the resolved "system" / dark / light palette in use
        bool pointerOver;          // pointer on the widget: hover, tooltips, full opacity
        HitInfo hover;             // element under the pointer; drives hover fills + tooltip
        readonly ToolTip toolTip = new ToolTip();   // the widget has no child controls to attach one to

        // Windows 11 rounds the corners and draws its own edge; the manifest declares the
        // Windows 10+ supportedOS GUID, so the build number is the real one.
        static readonly bool RoundedCorners = Environment.OSVersion.Version.Build >= 22000;

        public WidgetForm(string versionText)
        {
            this.versionText = versionText;
            settings = Settings.Load(AppPaths.SettingsPath);
            history = new History(AppPaths.HistoryPath, UtcNow);
            history.Load();
            allProviders = ProviderRegistry.All;
            foreach (IUsageProvider p in allProviders) allById[p.Id] = p;
            RefreshDetection();
            activeProviders = Settings.SelectShown(allProviders, settings.Providers,
                settings.AutoDetect, IsProviderDetected);
            foreach (IUsageProvider p in activeProviders) activeIds.Add(p.Id);
            refresh = new RefreshService(activeProviders.ToArray(), UtcNow, AppPaths.CachePath);
            updater = new Updater(settings.Update, AppPaths.RunningExePath, new Version(versionText));
            using (Graphics g = CreateGraphics())
                dpiScale = g.DpiX / 96f;
            lightTheme = ResolveLightTheme();
            ReplacePainter();
            detailedWidth = (int)painter.S(340);

            mode = settings.Layout == "compact" ? 1 : 0;

            Text = "AI Usage";
            FormBorderStyle = FormBorderStyle.None;
            TopMost = settings.AlwaysOnTop;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            ResizeRedraw = true;
            KeyPreview = true;
            MinimumSize = new Size((int)painter.S(300), (int)painter.S(120));

            RestoreSavedBounds();
            if (mode == 0) detailedWidth = ClientSize.Width;   // the saved width is the last chosen one
            DedupKnownProviders();
            InitTray();
            suggestions = ComputeSuggestions();
            AnnounceNewSuggestions();
            AnnounceNewProviders();
            BuildMenu();
            InitTimers();
            UpdateOpacity();

            // Resume, network return and theme flips arrive on other threads or out of
            // band, so the handlers marshal onto the UI thread.
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            refresh.Changed = OnRefreshChanged;
            updater.StateChanged = OnUpdaterChanged;
            updater.NotifyUser = Balloon;
            updater.ExitRequested = ExitApp;

            refresh.LoadCache();   // paint last-good immediately; first refresh lands in OnShown
        }

        static DateTime UtcNow()
        {
            return DateTime.UtcNow;
        }

        void OnRefreshChanged()
        {
            if (refreshWasBusy && !refresh.Busy) OnRefreshCompleted();   // once per completed refresh
            refreshWasBusy = refresh.Busy;
            FitHeightToContent();
            Invalidate();
        }

        // The Busy true->false edge of a refresh: samples land in history, notifications
        // are evaluated, and the tray icon and tooltip catch up with the new data.
        void OnRefreshCompleted()
        {
            RecordHistory();
            ShowNotifications();
            UpdateTrayVisuals();
            TrimMemory();
        }

        void RecordHistory()
        {
            try
            {
                history.Record(refresh.Snapshots, UtcNow());
                history.Prune(settings.HistoryDays);
                history.Save();
            }
            catch (Exception ex)
            {
                Log.Error("history update failed", ex);   // Record/Prune/Save log I/O issues; never throw past here
            }
        }

        void ShowNotifications()
        {
            try
            {
                List<string> messages = notifier.Evaluate(refresh.Snapshots, settings.Notifications,
                    UtcNow());
                if (messages.Count > 0)
                    BalloonWarning(NotifierBalloonText(messages));
            }
            catch (Exception ex)
            {
                Log.Error("notification check failed", ex);
            }
        }

        // Several messages become one balloon: at most 4 lines, then "+N more".
        public static string NotifierBalloonText(List<string> messages)
        {
            if (messages == null || messages.Count == 0) return "";
            int shown = Math.Min(4, messages.Count);
            var lines = new List<string>();
            for (int i = 0; i < shown; i++) lines.Add(messages[i]);
            string text = string.Join("\n", lines.ToArray());
            if (messages.Count > 4)
                text += "\n+" + (messages.Count - 4) + " more";
            return text;
        }

        List<RowVm> BuildRowsNow()
        {
            return Painter.BuildRows(refresh.Snapshots, DateTime.UtcNow, activeIds, history);
        }

        // The window is exactly as tall as its rows (as in the prototype), capped to the screen.
        // A window sitting in the lower half of the screen keeps its bottom edge in place, and
        // in compact mode the width tracks the widest title with the right edge in place.
        void FitHeightToContent()
        {
            List<RowVm> rows = BuildRowsNow();
            if (rows.Count == 0) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int wanted = (int)Math.Ceiling(painter.HeaderHeight + painter.ContentHeight(mode, rows) + painter.FooterHeight);
            int height = Math.Min(wanted, wa.Height - 32);
            int width = mode == 0
                ? ClientSize.Width
                : Math.Min((int)Math.Ceiling(painter.CompactWidthFor(rows)), wa.Width - 32);
            if (height == ClientSize.Height && width == ClientSize.Width) return;
            bool anchorBottom = Top + Height / 2 > wa.Top + wa.Height / 2;
            int bottom = Bottom;
            int right = Right;
            ClientSize = new Size(width, height);
            if (anchorBottom) Top = Math.Max(wa.Top, bottom - Height);
            Left = Math.Max(wa.Left, right - Width);
        }

        // --- auto-detection (spec section 13) -------------------------------------------

        // Detection scans files and PATH, so it never runs on paint, menu rebuilds or
        // suggestion lookups: start-up and RecomputeShown refresh this cache once per
        // refresh and everything else reads it.
        void RefreshDetection()
        {
            detectionCache.Clear();
            foreach (IUsageProvider p in allProviders) detectionCache[p.Id] = p.IsDetected();
        }

        bool IsProviderDetected(string id)
        {
            bool detected;
            return detectionCache.TryGetValue(id, out detected) && detected;
        }

        // Recomputes the shown set from the registry, the settings entries and detection;
        // hidden providers stop being fetched. Runs at startup and at the start of every
        // refresh.
        void RecomputeShown()
        {
            RefreshDetection();
            activeProviders = Settings.SelectShown(allProviders, settings.Providers,
                settings.AutoDetect, IsProviderDetected);
            activeIds = new List<string>();
            foreach (IUsageProvider p in activeProviders) activeIds.Add(p.Id);
            refresh.SetProviders(activeProviders.ToArray());
            suggestions = ComputeSuggestions();
            AnnounceNewSuggestions();
            AnnounceNewProviders();
            FitHeightToContent();
            Invalidate();
        }

        List<string> ComputeSuggestions()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var detected = new List<string>();
            foreach (KnownTool tool in KnownTools.All())
                if (KnownTools.IsDetected(tool, profile, local, PathValue()))
                    detected.Add(tool.Id);
            // The antigravity provider covers the Antigravity suggestion once the agy CLI is
            // detected (spec section 14).
            var covered = new List<string>();
            if (IsProviderDetected("antigravity")) covered.Add("antigravity");
            return KnownTools.SuggestedIds(detected, settings.DismissedSuggestions, covered);
        }

        string PathValue()
        {
            return Detection.CurrentPath();   // fresh from the registry: finds CLIs installed after launch
        }

        string ToolName(string toolId)
        {
            return KnownTools.DisplayNameOf(toolId);
        }

        string ProviderName(string id)
        {
            IUsageProvider p;
            return allById.TryGetValue(id, out p) ? p.DisplayName : id;
        }

        // One balloon for all providers that become shown: only for ones that turn up while
        // the widget runs, or that are new since the last saved state — never on the very
        // first run (no settings file yet), where the ids are just seeded (spec section 13).
        void AnnounceNewProviders()
        {
            var fresh = new List<string>();
            foreach (string id in activeIds)
                if (!settings.KnownProviders.Contains(id)) fresh.Add(id);
            if (fresh.Count == 0) return;
            if (settings.HasFile)
                Balloon("Detected " + NameList(fresh, ProviderName) + ": added to the widget");
            settings.KnownProviders.AddRange(fresh);   // fresh ids only: the whole list would duplicate
            SaveSettings();
        }

        // One balloon for all known tools new since the last saved state, ever:
        // announcedSuggestions remembers what was said.
        void AnnounceNewSuggestions()
        {
            var fresh = new List<string>();
            foreach (string id in suggestions)
                if (!settings.AnnouncedSuggestions.Contains(id)) fresh.Add(id);
            if (fresh.Count == 0) return;
            Balloon(NameList(fresh, ToolName) + " found on this PC: no usage support yet");
            settings.AnnouncedSuggestions.AddRange(fresh);
            SaveSettings();
        }

        static string NameList(List<string> ids, Func<string, string> nameOf)
        {
            var names = new List<string>();
            foreach (string id in ids) names.Add(nameOf(id));
            return string.Join(", ", names.ToArray());
        }

        // Older settings files can carry duplicate knownProviders ids (the announcement used
        // to add the whole active list on every run); drop them once so the file heals.
        void DedupKnownProviders()
        {
            var seen = new HashSet<string>();
            var unique = new List<string>();
            foreach (string id in settings.KnownProviders)
                if (seen.Add(id)) unique.Add(id);
            if (unique.Count == settings.KnownProviders.Count) return;
            settings.KnownProviders = unique;
            SaveSettings();
        }

        // The toggle rule, pure: the new state is the opposite of whether the provider is
        // shown right now, so hiding an auto-detected provider (shown, unlisted) writes one
        // disabled entry on the first click instead of two.
        public static void ApplyProviderToggle(List<ProviderSetting> entries, string id,
            bool currentlyShown)
        {
            foreach (ProviderSetting ps in entries)
            {
                if (ps.Id != id) continue;
                ps.Enabled = !currentlyShown;
                return;
            }
            var added = new ProviderSetting();
            added.Id = id;
            added.Enabled = !currentlyShown;
            entries.Add(added);
        }

        // A click in the Subscriptions submenu writes an explicit entry: the rule above.
        void ToggleProvider(string id)
        {
            ApplyProviderToggle(settings.Providers, id, activeIds.Contains(id));
            SaveSettings();
            RecomputeShown();
            BuildMenu();
        }

        void DismissSuggestion(string toolId)
        {
            if (!settings.DismissedSuggestions.Contains(toolId))
                settings.DismissedSuggestions.Add(toolId);
            SaveSettings();
            suggestions = ComputeSuggestions();
            BuildMenu();
            Invalidate();
        }

        void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception)
            {
                // a bad URL or a missing browser must never take the widget down
            }
        }

        string FooterNote()
        {
            int n = suggestions.Count;
            return n == 0 ? null : "+" + n.ToString(CultureInfo.InvariantCulture) + " not supported yet";
        }

        void Balloon(string text)
        {
            tray.ShowBalloonTip(4000, "AI Usage Widget", text, ToolTipIcon.Info);
        }

        // Threshold and reset notifications use the warning icon; shown from the tray, so
        // they arrive even while the widget itself is hidden.
        void BalloonWarning(string text)
        {
            tray.ShowBalloonTip(4000, "AI Usage Widget", text, ToolTipIcon.Warning);
        }

        void RestoreSavedBounds()
        {
            Rectangle r = settings.Bounds;
            if (r.Width < 80 || r.Height < 80) r = new Rectangle(r.X, r.Y, 340, 560);
            bool onScreen = false;
            foreach (Screen screen in Screen.AllScreens)
                if (settings.HasSavedBounds && !Rectangle.Intersect(r, screen.Bounds).IsEmpty)
                {
                    onScreen = true;
                    break;
                }
            if (!onScreen)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int w = Math.Min(r.Width, wa.Width - 32);
                int h = Math.Min(r.Height, wa.Height - 32);
                r = new Rectangle(wa.Right - w - 16, wa.Bottom - h - 16, w, h);
            }
            Bounds = r;
        }

        void InitTimers()
        {
            refreshTimer.Interval = settings.RefreshMinutes * 60000;
            refreshTimer.Tick += OnRefreshTimer;
            minuteTimer.Interval = 60000;   // re-render "updated ... ago", stale tags, reset times
            minuteTimer.Tick += delegate { Invalidate(); };
            noteTimer.Interval = 3000;   // "just refreshed" stays up about this long
            noteTimer.Tick += delegate
            {
                noteTimer.Stop();
                justRefreshed = false;
                Invalidate();
            };
            resumeTimer.Interval = 5000;   // one refresh about 5 s after resume / network return
            resumeTimer.Tick += delegate
            {
                resumeTimer.Stop();
                resumeRefreshPending = false;
                RunRefresh(false);
            };
            updateTimer.Interval = 24 * 60 * 60000;
            updateTimer.Tick += OnUpdateTimer;
            refreshTimer.Start();
            minuteTimer.Start();
            updateTimer.Start();
        }

        void OnRefreshTimer(object sender, EventArgs e)
        {
            RunRefresh(false);
        }

        void OnUpdateTimer(object sender, EventArgs e)
        {
            RunCheck();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) ScheduleDelayedRefresh();
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // With "system" selected the Windows app theme can flip while the widget runs.
            if (settings.Theme != "system") return;
            try
            {
                BeginInvoke((MethodInvoker)delegate { ApplyTheme(); });
            }
            catch (InvalidOperationException)
            {
                // no handle yet (or already gone): nothing to re-theme
            }
        }

        void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable) ScheduleDelayedRefresh();
        }

        // The system events arrive on other threads; marshal onto the UI thread and never
        // touch a form that is going away. While one delayed refresh is pending, further
        // events change nothing.
        void ScheduleDelayedRefresh()
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || resumeRefreshPending) return;
                    resumeRefreshPending = true;
                    resumeTimer.Start();
                });
            }
            catch (InvalidOperationException)
            {
                // no handle yet (or already gone): nothing to schedule on
            }
        }

        // --- palette, painter and opacity -----------------------------------------------

        const string PersonalizeKeyPath =
            "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";

        bool ResolveLightTheme()
        {
            string t = settings.Theme;
            return t == "light" || (t != "dark" && AppsUseLightTheme());
        }

        // HKCU\...\Themes\Personalize AppsUseLightTheme: 1 = light, missing = dark.
        static bool AppsUseLightTheme()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath))
            {
                object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                return value is int && (int)value == 1;
            }
        }

        void ApplyTheme()
        {
            bool light = ResolveLightTheme();
            if (light == lightTheme) return;
            lightTheme = light;
            ReplacePainter();
            Invalidate();
        }

        // Builds the painter at the current scale, resolved palette and colour thresholds;
        // BackColor follows the palette so the window base matches what is drawn on it.
        void ReplacePainter()
        {
            Painter old = painter;
            painter = new Painter(dpiScale, lightTheme ? Theme.Light : Theme.Dark,
                settings.WarnPercent, settings.CriticalPercent);
            painter.DrawBorder = !RoundedCorners;
            if (old != null) old.Dispose();
            BackColor = painter.Background;
        }

        // settings.Opacity (40..100) while idle; fully opaque while the pointer is over it.
        void UpdateOpacity()
        {
            int percent = pointerOver ? 100 : Settings.Clamp(settings.Opacity, 40, 100);
            double target = percent / 100.0;
            if (Opacity != target) Opacity = target;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        // Hands pages the widget is not using back to Windows. The HTTP stack leaves the
        // working set well above what an idle widget needs; run after each refresh.
        static void TrimMemory()
        {
            GC.Collect();
            using (System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess())
                SetProcessWorkingSetSize(current.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }

        async void RunRefresh(bool manual)
        {
            // First-run consent comes first: no provider is contacted before it is given.
            // This is the one choke point every refresh path goes through.
            if (!settings.ConsentDone) return;
            try
            {
                RecomputeShown();   // detection runs at the start of every refresh
                if (manual && (refresh.Busy
                    || (DateTime.UtcNow - refresh.LastRunStartUtc).TotalSeconds < 30))
                {
                    ShowJustRefreshed();   // RunOnceAsync would return silently: say so instead
                    return;
                }
                await refresh.RunOnceAsync(manual);
            }
            catch (Exception ex)
            {
                Log.Error("refresh failed", ex);
                // a refresh must never take the widget down
            }
        }

        void ShowJustRefreshed()
        {
            justRefreshed = true;
            noteTimer.Stop();
            noteTimer.Start();
            Invalidate();
        }

        async void RunCheck()
        {
            try
            {
                await updater.CheckAsync();
            }
            catch (Exception ex)
            {
                Log.Error("update check failed", ex);
            }
        }

        void InitTray()
        {
            using (System.IO.Stream icon = typeof(WidgetForm).Assembly.GetManifestResourceStream("AiUsageWidget.app.ico"))
                embeddedTrayIcon = icon == null
                    ? SystemIcons.Application
                    : new Icon(icon, SystemInformation.SmallIconSize);
            tray.Icon = embeddedTrayIcon;
            tray.Text = TrayText();
            tray.Visible = true;
            tray.Click += delegate
            {
                // One left click toggles the widget; double-click below keeps showing it.
                if (Visible) HideToTray();
                else ShowAndActivate();
            };
            tray.DoubleClick += delegate { ShowAndActivate(); };
        }

        // The tray icon and tooltip catch up with the rows: the icon shows the worst
        // primary window in its threshold colour, the tooltip lists the worst windows
        // first. Rebuilt only when the rounded percent or colour actually changed, and
        // the previous icon is disposed (this runs on every refresh).
        void UpdateTrayVisuals()
        {
            List<RowVm> rows = BuildRowsNow();
            string tooltip = TrayIconRenderer.TooltipText(rows, TrayText());
            if (tooltip != tray.Text) tray.Text = tooltip;
            double? worst = TrayIconRenderer.WorstPercent(rows);
            if (!worst.HasValue)
            {
                RestoreEmbeddedTrayIcon();
                return;
            }
            Color color = painter.ColorAt(worst.Value);
            int percent = (int)Math.Round(worst.Value);
            if (renderedTrayIcon != null && percent == renderedTrayPercent
                && color.ToArgb() == renderedTrayColor) return;
            Icon fresh = TrayIconRenderer.Render(worst, color, SystemInformation.SmallIconSize.Width);
            if (fresh == null) return;
            Icon old = renderedTrayIcon;
            tray.Icon = fresh;
            renderedTrayIcon = fresh;
            renderedTrayPercent = percent;
            renderedTrayColor = color.ToArgb();
            if (old != null) old.Dispose();
        }

        void RestoreEmbeddedTrayIcon()
        {
            if (renderedTrayIcon == null) return;
            Icon old = renderedTrayIcon;
            renderedTrayIcon = null;
            renderedTrayPercent = -1;
            tray.Icon = embeddedTrayIcon;
            old.Dispose();
        }

        string TrayText()
        {
            string t = "AI Usage Widget v" + versionText;
            if (!string.IsNullOrEmpty(updater.StatusText)) t += " — " + updater.StatusText;
            if (t.Length > 63) t = t.Substring(0, 63);
            return t;
        }

        void OnUpdaterChanged()
        {
            UpdateTrayVisuals();   // the version text is only the tooltip's no-data fallback
            BuildMenu();
            Invalidate();
        }

        void BuildMenu()
        {
            ContextMenuStrip old = tray.ContextMenuStrip;
            var menu = new ContextMenuStrip();

            menu.Items.Add("Refresh now", null, delegate { RunRefresh(true); });
            menu.Items.Add("Settings…", null, delegate { OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());

            var layoutItem = new ToolStripMenuItem("Layout");
            var detailed = new ToolStripMenuItem("Detailed");
            detailed.Checked = mode == 0;
            detailed.Click += delegate { SetLayout("detailed"); };
            var compact = new ToolStripMenuItem("Compact");
            compact.Checked = mode == 1;
            compact.Click += delegate { SetLayout("compact"); };
            layoutItem.DropDownItems.Add(detailed);
            layoutItem.DropDownItems.Add(compact);
            menu.Items.Add(layoutItem);

            // Subscriptions: every registered provider with its shown/hidden state (spec 13).
            // Detection comes from the cache RefreshDetection keeps fresh.
            var subsItem = new ToolStripMenuItem("Subscriptions");
            foreach (IUsageProvider p in allProviders)
            {
                bool shown = activeIds.Contains(p.Id);
                string suffix = !shown ? " (hidden)"
                    : IsProviderDetected(p.Id) ? " (detected)" : " (not detected)";
                var item = new ToolStripMenuItem(p.DisplayName + suffix);
                item.Checked = shown;
                string id = p.Id;
                item.Click += delegate { ToggleProvider(id); };
                subsItem.DropDownItems.Add(item);
            }
            subsItem.DropDownItems.Add(new ToolStripSeparator());
            var autoItem = new ToolStripMenuItem("Auto-detect");
            autoItem.Checked = settings.AutoDetect;
            autoItem.Click += delegate
            {
                settings.AutoDetect = !settings.AutoDetect;
                SaveSettings();
                RecomputeShown();
                BuildMenu();
            };
            subsItem.DropDownItems.Add(autoItem);
            menu.Items.Add(subsItem);

            // Detected tools with no usage provider yet (spec section 13); `suggestions`
            // comes from RecomputeShown / the constructor — no detection scans here.
            var sugItem = new ToolStripMenuItem("Detected, no usage support yet");
            foreach (string toolId in suggestions)
            {
                var entry = new ToolStripMenuItem(ToolName(toolId));
                string url = KnownTools.IssueUrl(settings.Update.Repo, ToolName(toolId));
                var requestItem = new ToolStripMenuItem("Request support…");
                requestItem.Enabled = !string.IsNullOrEmpty(url);
                requestItem.Click += delegate { OpenUrl(url); };
                entry.DropDownItems.Add(requestItem);
                var dismissItem = new ToolStripMenuItem("Dismiss");
                string dismissedId = toolId;
                dismissItem.Click += delegate { DismissSuggestion(dismissedId); };
                entry.DropDownItems.Add(dismissItem);
                // Spec section 14: the Antigravity desktop app without the agy CLI gets a
                // help line pointing at the CLI.
                string helpText = KnownTools.HelpText(toolId);
                if (helpText != null && !IsProviderDetected(toolId))
                {
                    var helpItem = new ToolStripMenuItem(helpText);
                    helpItem.Enabled = false;
                    entry.DropDownItems.Add(helpItem);
                }
                sugItem.DropDownItems.Add(entry);
            }
            if (suggestions.Count == 0) sugItem.Enabled = false;
            menu.Items.Add(sugItem);

            var topItem = new ToolStripMenuItem("Always on top");
            topItem.Checked = TopMost;
            topItem.Click += delegate
            {
                TopMost = !TopMost;
                settings.AlwaysOnTop = TopMost;
                SaveSettings();
                BuildMenu();
            };
            menu.Items.Add(topItem);

            var runItem = new ToolStripMenuItem("Start with Windows");
            runItem.Checked = IsRegisteredToRun();
            runItem.Click += delegate { ToggleRunAtStartup(); };
            menu.Items.Add(runItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Check for updates", null, delegate { RunCheck(); });
            if (updater.PendingInstall)
                menu.Items.Add("Install update v" + updater.PendingVersion, null,
                    delegate { updater.InstallPending(); });
            if (settings.Update.RequiresToken)
                menu.Items.Add("Set update token…", null, delegate { ShowTokenDialog(); });

            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem versionItem = menu.Items.Add("AI Usage Widget v" + versionText);
            versionItem.Enabled = false;
            menu.Items.Add("Exit", null, delegate { ExitApp(); });

            tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        void ShowAndActivate()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // The tray icon and the widget itself (right-click, or the "⋯" header button) show
        // this one menu instance; BuildMenu replaces it wholesale on every state change.
        void ShowWidgetMenu(Point screen)
        {
            ContextMenuStrip menu = tray.ContextMenuStrip;
            if (menu == null) return;
            menu.Show(screen);
        }

        void HideToTray()
        {
            ExitHover();
            if (!settings.TrayHintShown)
            {
                Balloon("AI Usage Widget is still running. Double-click the tray icon to show it.");
                settings.TrayHintShown = true;
                SaveSettings();
            }
            Hide();
        }

        void ExitApp()
        {
            exitSignalled = true;
            SaveSettings();
            tray.Visible = false;
            Application.Exit();
        }

        void SetLayout(string layout)
        {
            int newMode = layout == "compact" ? 1 : 0;
            if (mode == 0 && newMode == 1) detailedWidth = ClientSize.Width;   // remember the chosen width
            settings.Layout = layout;
            mode = newMode;
            scroll = 0;
            int right = Right;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int width = mode == 0
                ? detailedWidth   // the last width the user chose in Detailed, not a forced default
                : Math.Min((int)Math.Ceiling(painter.CompactWidthFor(BuildRowsNow())), wa.Width - 32);
            ClientSize = new Size(width, ClientSize.Height);
            Left = Math.Max(wa.Left, right - Width);   // keep the right edge in place
            FitHeightToContent();
            SaveSettings();
            BuildMenu();
            Invalidate();
        }

        static bool IsRegisteredToRun()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                return key != null && key.GetValue(RunValueName) != null;
        }

        void ToggleRunAtStartup()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) return;
                if (IsRegisteredToRun()) key.DeleteValue(RunValueName, false);
                else key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
            }
            BuildMenu();
        }

        void ShowTokenDialog()
        {
            using (TokenDialog dlg = new TokenDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string token = dlg.TokenText;
                if (string.IsNullOrEmpty(token)) TokenStore.Clear();
                else TokenStore.Save(token);
                RunCheck();
            }
        }

        // The settings window: at most one instance, modally over the widget. The dialog
        // writes its values into `settings` on OK; ApplyLiveSettings mirrors them onto the
        // running widget without a restart.
        void OpenSettings()
        {
            if (settingsOpen) return;
            settingsOpen = true;
            try
            {
                using (SettingsForm dlg = new SettingsForm(settings, allProviders))
                {
                    dlg.CurrentVersion = versionText;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                }
                ApplyLiveSettings();
            }
            finally
            {
                settingsOpen = false;
            }
        }

        void ApplyLiveSettings()
        {
            // SaveSettings copies bounds, topmost and layout from the live window, so the
            // window fields move before the save.
            TopMost = settings.AlwaysOnTop;
            SaveSettings();
            bool compact = settings.Layout == "compact";
            if ((mode == 1) != compact) SetLayout(settings.Layout);
            refreshTimer.Interval = settings.RefreshMinutes * 60000;
            lightTheme = ResolveLightTheme();   // theme and thresholds both live in the painter
            ReplacePainter();
            UpdateOpacity();
            RecomputeShown();
            BuildMenu();
            Invalidate();
            RunRefresh(false);
        }

        void SaveSettings()
        {
            settings.Bounds = Bounds;
            settings.AlwaysOnTop = TopMost;
            settings.Layout = mode == 0 ? "detailed" : "compact";
            try
            {
                settings.Save(AppPaths.SettingsPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        string HeaderNote()
        {
            if (justRefreshed) return "just refreshed";
            if (refresh.Busy) return "updating…";
            DateTime? latest = null;
            List<UsageSnapshot> snapshots = refresh.Snapshots;
            if (snapshots != null)
            {
                foreach (UsageSnapshot s in snapshots)
                {
                    if (s.FetchedAtUtc == DateTime.MinValue) continue;
                    if (!latest.HasValue || s.FetchedAtUtc > latest.Value) latest = s.FetchedAtUtc;
                }
            }
            if (!latest.HasValue) return "";
            return UpdatedText(latest.Value);
        }

        // The header note and the row-title tooltip say the age the same way.
        static string UpdatedText(DateTime fetchedUtc)
        {
            TimeSpan age = DateTime.UtcNow - fetchedUtc;
            if (age.TotalSeconds < 60) return "updated just now";
            if (age.TotalMinutes < 60) return "updated " + (int)age.TotalMinutes + " min ago";
            return "updated " + Painter.Duration(age) + " ago";
        }

        // --- hover, tooltips and the hand cursor -----------------------------------------

        const int TooltipDurationMs = 7000;

        void EnterHover(Point p)
        {
            if (!pointerOver)
            {
                pointerOver = true;
                UpdateOpacity();
            }
            SetHover(painter.HitTest(p, ClientSize.Width, ClientSize.Height, mode, scroll,
                BuildRowsNow()));
        }

        void ExitHover()
        {
            if (!pointerOver) return;
            pointerOver = false;
            SetHover(new HitInfo());
            UpdateOpacity();
        }

        // Repaints and re-shows the tooltip only when the element under the pointer changes.
        void SetHover(HitInfo h)
        {
            if (h.Equals(hover)) return;
            hover = h;
            Cursor = IsHandTarget(h) ? Cursors.Hand : Cursors.Default;
            ShowTooltip(h);
            Invalidate();
        }

        static bool IsHandTarget(HitInfo h)
        {
            return h.Kind == HitKind.MenuButton || h.Kind == HitKind.RefreshButton
                || h.Kind == HitKind.CloseButton || h.Kind == HitKind.DetailedChip
                || h.Kind == HitKind.CompactChip;
        }

        void ShowTooltip(HitInfo h)
        {
            toolTip.Hide(this);
            string text = TooltipFor(h);
            if (text == null) return;
            Point p = PointToClient(Cursor.Position);
            toolTip.Show(text, this, p.X + 16, p.Y + 12, TooltipDurationMs);
        }

        string TooltipFor(HitInfo h)
        {
            if (h.Kind == HitKind.MenuButton) return "Menu";
            if (h.Kind == HitKind.RefreshButton) return "Refresh now";
            if (h.Kind == HitKind.CloseButton) return "Hide to tray";
            if (h.Kind != HitKind.RowTitle && h.Kind != HitKind.UsageWindow) return null;
            List<RowVm> rows = BuildRowsNow();
            if (h.Row < 0 || h.Row >= rows.Count) return null;
            RowVm row = rows[h.Row];
            if (h.Kind == HitKind.RowTitle) return RowTooltip(row);
            if (h.Window < 0 || h.Window >= row.Windows.Count) return null;
            return WindowTooltip(row.Windows[h.Window]);
        }

        // Provider name, plan, the age of the data, and the full message the row itself
        // only shows shortened.
        static string RowTooltip(RowVm row)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(row.Name)) lines.Add(row.Name);
            if (!string.IsNullOrEmpty(row.Plan)) lines.Add(row.Plan);
            if (row.FetchedAtUtc != DateTime.MinValue) lines.Add(UpdatedText(row.FetchedAtUtc));
            if (!string.IsNullOrEmpty(row.Msg)) lines.Add(row.Msg);
            return lines.Count == 0 ? null : string.Join("\n", lines.ToArray());
        }

        static string WindowTooltip(WindowVm wnd)
        {
            var lines = new List<string>();
            lines.Add(wnd.Label);
            lines.Add(wnd.UsedPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%");
            if (!string.IsNullOrEmpty(wnd.Detail))
                lines.Add(wnd.Detail + (string.IsNullOrEmpty(wnd.Unit) ? "" : " " + wnd.Unit));
            if (wnd.ResetsAtUtc.HasValue)
                lines.Add("resets " + wnd.ResetsAtUtc.Value.ToLocalTime().ToString(
                    "ddd dd MMM HH:mm", CultureInfo.CurrentCulture));
            if (wnd.PacePercent.HasValue)
                lines.Add("even pace would be "
                    + Math.Round(wnd.PacePercent.Value).ToString("0", CultureInfo.InvariantCulture) + " %");
            if (!string.IsNullOrEmpty(wnd.ForecastText))
                lines.Add(wnd.ForecastText);
            return string.Join("\n", lines.ToArray());
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            List<RowVm> rows = BuildRowsNow();
            painter.Draw(e.Graphics, ClientSize.Width, ClientSize.Height, mode, scroll, rows,
                HeaderNote(), FooterNote(), hover);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                base.OnMouseDown(e);   // right-click reaches the menu via WM_CONTEXTMENU
                return;
            }
            float w = ClientSize.Width;
            if (painter.MenuRect(w).Contains(e.Location))
            {
                ShowWidgetMenu(PointToScreen(e.Location));
                return;
            }
            if (painter.RefreshRect(w).Contains(e.Location))
            {
                RunRefresh(true);
                return;
            }
            if (painter.CloseRect(w).Contains(e.Location))
            {
                HideToTray();
                return;
            }
            if (painter.DetailedRect(w, ClientSize.Height).Contains(e.Location))
            {
                SetLayout("detailed");
                return;
            }
            if (painter.CompactRect(w, ClientSize.Height).Contains(e.Location))
            {
                SetLayout("compact");
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            EnterHover(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            ExitHover();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            List<RowVm> rows = BuildRowsNow();
            float max = painter.ContentHeight(mode, rows)
                - (ClientSize.Height - painter.HeaderHeight - painter.FooterHeight);
            scroll -= e.Delta / 4;
            scroll = (int)Math.Max(0, Math.Min(scroll, max));
            Invalidate();
            EnterHover(new Point(e.X, e.Y));   // the content moved under a still pointer
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) HideToTray();
            base.OnKeyDown(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!EnsureConsent())
            {
                ExitApp();   // Exit on the consent window: quit without fetching anything
                return;
            }
            InitialWork();
        }

        // The first-run consent window. An existing user (a settings file but no consent
        // flag) just gets the flag; a brand-new user chooses the shown providers before
        // the first refresh, which RunRefresh's gate holds back meanwhile.
        bool EnsureConsent()
        {
            if (settings.ConsentDone) return true;
            if (settings.HasFile)
            {
                settings.ConsentDone = true;
                SaveSettings();
                return true;
            }
            using (ConsentForm dlg = new ConsentForm(allProviders, IsProviderDetected))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                settings.Providers = dlg.Result;
                settings.ConsentDone = true;
                SaveSettings();
                RecomputeShown();
                BuildMenu();
                return true;
            }
        }

        async void InitialWork()
        {
            await Task.Delay(300);   // let the first paint land
            RunRefresh(false);
            await Task.Delay(1500);
            RunCheck();   // startup update check, after first paint
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (mode == 0) detailedWidth = ClientSize.Width;   // keep the remembered width current
            SaveSettings();   // fires after a user move or resize
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exitSignalled && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            SaveSettings();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Static system events: always unsubscribe.
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            refreshTimer.Stop();
            minuteTimer.Stop();
            updateTimer.Stop();
            noteTimer.Stop();
            resumeTimer.Stop();
            refreshTimer.Dispose();
            minuteTimer.Dispose();
            updateTimer.Dispose();
            noteTimer.Dispose();
            resumeTimer.Dispose();
            if (renderedTrayIcon != null) renderedTrayIcon.Dispose();
            tray.Dispose();
            toolTip.Dispose();
            base.OnFormClosed(e);
            painter.Dispose();
        }

        // WS_EX_TOOLWINDOW keeps the widget out of Alt+Tab; CS_DROPSHADOW gives it the
        // system drop shadow on every Windows version.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;              // WS_EX_TOOLWINDOW
                cp.ClassStyle |= 0x00020000;     // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryRoundCorners();
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Ask DWM for rounded corners on Windows 11; it draws the window edge there, which
        // is why the painter drops its own border (ReplacePainter). Failures are ignored.
        void TryRoundCorners()
        {
            if (!RoundedCorners) return;
            int round = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE (33), DWMWCP_ROUND
            try
            {
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch (Exception)
            {
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct TrackMouseEventRecord
        {
            public int Size;
            public int Flags;
            public IntPtr Track;
            public int HoverTime;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool TrackMouseEvent(ref TrackMouseEventRecord record);

        // WinForms tracks client-area leaves on its own; the non-client leave (the drag
        // areas) only arrives when asked for, once per call.
        void ArmNonClientLeaveTracking()
        {
            var record = new TrackMouseEventRecord();
            record.Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(TrackMouseEventRecord));
            record.Flags = 0x02 | 0x10;   // TME_LEAVE | TME_NONCLIENT
            record.Track = Handle;
            TrackMouseEvent(ref record);
        }

        // Appearing never steals focus; ShowAndActivate still brings it to the front.
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        // Drag anywhere to move, no maximise on double-click. The height is always automatic
        // (FitHeightToContent), so no edge offers vertical resizing: in Detailed mode the
        // left and right edges resize the width, in Compact mode no edge resizes.
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_DPICHANGED = 0x02E0;
            const int WM_NCLBUTTONDBLCLK = 0x00A3;
            const int WM_NCMOUSEMOVE = 0x00A0;
            const int WM_NCMOUSELEAVE = 0x02A2;
            const int WM_NCRBUTTONUP = 0x00A5;
            const int WM_CONTEXTMENU = 0x007B;
            if (m.Msg == WM_NCLBUTTONDBLCLK) return;
            if (m.Msg == Program.ShowMessageId)
            {
                ShowAndActivate();   // a second launch asks the running widget to show itself
                return;
            }
            if (m.Msg == WM_DPICHANGED)
            {
                HandleDpiChanged(m);
                return;
            }
            if (m.Msg == WM_NCMOUSEMOVE)
            {
                // Almost the whole window is HTCAPTION (non-client), so hover and the
                // tooltips ride on the non-client mouse messages.
                ArmNonClientLeaveTracking();
                long lp = m.LParam.ToInt64();
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                EnterHover(p);
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == WM_NCMOUSELEAVE)
            {
                ExitHover();
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == WM_NCRBUTTONUP)
            {
                // Answered here so DefWindowProc never turns it into WM_CONTEXTMENU too
                // (the menu would open twice).
                long lp = m.LParam.ToInt64();
                ShowWidgetMenu(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                return;
            }
            if (m.Msg == WM_CONTEXTMENU)
            {
                // Right-clicks on the client-area buttons land here; lParam -1 means the
                // keyboard asked for the menu.
                long lp = m.LParam.ToInt64();
                Point screen = lp == -1
                    ? PointToScreen(new Point(Width / 2, (int)painter.HeaderHeight))
                    : new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                ShowWidgetMenu(screen);
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int grip = (int)painter.S(6), w = ClientSize.Width, h = ClientSize.Height;
                bool l = p.X < grip, r = p.X >= w - grip, t = p.Y < grip, b = p.Y >= h - grip;
                int code = 2;                   // HTCAPTION: drag anywhere
                if (mode == 0 && !t && !b)
                {
                    if (l) code = 10;           // HTLEFT: width only, Detailed only
                    else if (r) code = 11;      // HTRIGHT
                }
                if (code == 2 && (painter.MenuRect(w).Contains(p) || painter.RefreshRect(w).Contains(p)
                    || painter.CloseRect(w).Contains(p) || painter.DetailedRect(w, h).Contains(p)
                    || painter.CompactRect(w, h).Contains(p)))
                    code = 1;                   // HTCLIENT: header buttons and footer chips handle clicks
                m.Result = (IntPtr)code;
                return;
            }
            base.WndProc(ref m);
        }

        // Per-monitor DPI: rebuild the painter at the new scale and take the suggested
        // bounds. Applying them can raise WM_DPICHANGED again, so re-entrancy is guarded.
        void HandleDpiChanged(Message m)
        {
            if (inDpiChange) return;
            inDpiChange = true;
            try
            {
                int newDpi = (int)(m.WParam.ToInt64() & 0xFFFF);   // low word: the new DPI
                int left = System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 0);
                int top = System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 4);
                int right = System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 8);
                int bottom = System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 12);
                dpiScale = newDpi / 96f;
                ReplacePainter();
                MinimumSize = new Size((int)painter.S(300), (int)painter.S(120));
                Bounds = Rectangle.FromLTRB(left, top, right, bottom);
                if (mode == 0) detailedWidth = ClientSize.Width;
                FitHeightToContent();
                Invalidate();
            }
            finally
            {
                inDpiChange = false;
            }
        }

        sealed class TokenDialog : Form
        {
            readonly TextBox input = new TextBox();

            public TokenDialog()
            {
                Text = "Set update token";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                TopMost = true;
                ClientSize = new Size(380, 110);

                var label = new Label();
                label.Text = "GitHub fine-grained token (read-only Contents on one repo):";
                label.SetBounds(12, 12, 356, 20);
                input.SetBounds(12, 38, 356, 23);

                var ok = new Button();
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(198, 72, 75, 26);
                var cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(281, 72, 75, 26);

                Controls.Add(label);
                Controls.Add(input);
                Controls.Add(ok);
                Controls.Add(cancel);
                AcceptButton = ok;
                CancelButton = cancel;
            }

            public string TokenText
            {
                get
                {
                    string t = input.Text;
                    return t == null ? null : t.Trim();
                }
            }
        }
    }
}
