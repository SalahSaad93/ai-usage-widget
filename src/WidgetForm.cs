using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
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
        readonly Painter painter;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Timer refreshTimer = new Timer();
        readonly Timer minuteTimer = new Timer();
        readonly Timer updateTimer = new Timer();
        List<IUsageProvider> activeProviders;   // recomputed at startup and at every refresh
        List<string> activeIds = new List<string>();   // ordered, as SelectShown returns them
        readonly Dictionary<string, IUsageProvider> allById = new Dictionary<string, IUsageProvider>();
        List<string> suggestions = new List<string>();   // detected tools with no provider
        readonly string versionText;

        int mode;          // 0 = detailed, 1 = compact
        int scroll;
        bool exitSignalled;

        public WidgetForm(string versionText)
        {
            this.versionText = versionText;
            settings = Settings.Load(AppPaths.SettingsPath);
            foreach (IUsageProvider p in ProviderRegistry.All) allById[p.Id] = p;
            activeProviders = Settings.SelectShown(ProviderRegistry.All, settings.Providers,
                settings.AutoDetect, IsProviderDetected);
            foreach (IUsageProvider p in activeProviders) activeIds.Add(p.Id);
            refresh = new RefreshService(activeProviders.ToArray(), UtcNow, AppPaths.CachePath);
            updater = new Updater(settings.Update, AppPaths.RunningExePath, new Version(versionText));
            using (Graphics g = CreateGraphics())
                painter = new Painter(g.DpiX / 96f);

            mode = settings.Layout == "compact" ? 1 : 0;

            Text = "AI Usage";
            FormBorderStyle = FormBorderStyle.None;
            TopMost = settings.AlwaysOnTop;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Painter.Background;
            DoubleBuffered = true;
            ResizeRedraw = true;
            KeyPreview = true;
            MinimumSize = new Size((int)painter.S(300), (int)painter.S(120));

            RestoreSavedBounds();
            InitTray();
            suggestions = ComputeSuggestions();
            AnnounceNewSuggestions();
            AnnounceNewProviders();
            BuildMenu();
            InitTimers();

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
            FitHeightToContent();
            Invalidate();
        }

        List<RowVm> BuildRowsNow()
        {
            return Painter.BuildRows(refresh.Snapshots, DateTime.UtcNow, activeIds);
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

        bool IsProviderDetected(string id)
        {
            IUsageProvider p;
            return allById.TryGetValue(id, out p) && p.IsDetected();
        }

        // Recomputes the shown set from the registry, the settings entries and detection;
        // hidden providers stop being fetched. Runs at startup and at the start of every
        // refresh.
        void RecomputeShown()
        {
            activeProviders = Settings.SelectShown(ProviderRegistry.All, settings.Providers,
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
            return Environment.GetEnvironmentVariable("PATH");
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

        // One balloon per provider that becomes shown: only for one that turns up while the
        // widget runs, or that is new since the last saved state — never on the very first
        // run (no settings file yet), where the ids are just seeded (spec section 13).
        void AnnounceNewProviders()
        {
            var fresh = new List<string>();
            foreach (string id in activeIds)
                if (!settings.KnownProviders.Contains(id)) fresh.Add(id);
            if (fresh.Count == 0) return;
            if (settings.HasFile)
                foreach (string id in fresh)
                    Balloon("Detected " + ProviderName(id) + ": added to the widget");
            settings.KnownProviders.AddRange(activeIds);
            SaveSettings();
        }

        // One balloon per known tool, ever: announcedSuggestions remembers what was said.
        void AnnounceNewSuggestions()
        {
            var fresh = new List<string>();
            foreach (string id in suggestions)
                if (!settings.AnnouncedSuggestions.Contains(id)) fresh.Add(id);
            if (fresh.Count == 0) return;
            foreach (string id in fresh)
                Balloon(ToolName(id) + " found on this PC: no usage support yet");
            settings.AnnouncedSuggestions.AddRange(fresh);
            SaveSettings();
        }

        // A click in the Subscriptions submenu writes an explicit entry: flipped when one
        // exists, added enabled when the provider was only auto-shown.
        void ToggleProvider(string id)
        {
            ProviderSetting entry = null;
            foreach (ProviderSetting ps in settings.Providers)
                if (ps.Id == id) { entry = ps; break; }
            if (entry != null) entry.Enabled = !entry.Enabled;
            else
            {
                var added = new ProviderSetting();
                added.Id = id;
                added.Enabled = true;
                settings.Providers.Add(added);
            }
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
            minuteTimer.Tick += delegate { Invalidate(); TrimMemory(); };
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

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        // Hands pages the widget is not using back to Windows. The HTTP stack leaves the
        // working set well above what an idle widget needs after each refresh.
        static void TrimMemory()
        {
            GC.Collect();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }

        async void RunRefresh(bool manual)
        {
            try
            {
                RecomputeShown();   // detection runs at the start of every refresh
                await refresh.RunOnceAsync(manual);
            }
            catch (Exception)
            {
                // a refresh must never take the widget down
            }
        }

        async void RunCheck()
        {
            try
            {
                await updater.CheckAsync();
            }
            catch (Exception)
            {
            }
        }

        void InitTray()
        {
            using (System.IO.Stream icon = typeof(WidgetForm).Assembly.GetManifestResourceStream("AiUsageWidget.app.ico"))
                tray.Icon = icon == null ? SystemIcons.Application : new Icon(icon, SystemInformation.SmallIconSize);
            tray.Text = TrayText();
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowAndActivate(); };
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
            tray.Text = TrayText();
            BuildMenu();
            Invalidate();
        }

        void BuildMenu()
        {
            ContextMenuStrip old = tray.ContextMenuStrip;
            var menu = new ContextMenuStrip();

            menu.Items.Add("Refresh now", null, delegate { RunRefresh(true); });
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
            var subsItem = new ToolStripMenuItem("Subscriptions");
            foreach (IUsageProvider p in ProviderRegistry.All)
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

            // Detected tools with no usage provider yet (spec section 13).
            suggestions = ComputeSuggestions();
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

        void HideToTray()
        {
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
            settings.Layout = layout;
            mode = layout == "compact" ? 1 : 0;
            scroll = 0;
            int right = Right;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int width = mode == 0
                ? (int)painter.S(340)
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
            TimeSpan age = DateTime.UtcNow - latest.Value;
            if (age.TotalSeconds < 60) return "updated just now";
            if (age.TotalMinutes < 60) return "updated " + (int)age.TotalMinutes + " min ago";
            return "updated " + Painter.Duration(age) + " ago";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            List<RowVm> rows = BuildRowsNow();
            painter.Draw(e.Graphics, ClientSize.Width, ClientSize.Height, mode, scroll, rows,
                HeaderNote(), FooterNote());
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            float w = ClientSize.Width;
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

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            List<RowVm> rows = BuildRowsNow();
            float max = painter.ContentHeight(mode, rows)
                - (ClientSize.Height - painter.HeaderHeight - painter.FooterHeight);
            scroll -= e.Delta / 4;
            scroll = (int)Math.Max(0, Math.Min(scroll, max));
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) HideToTray();
            base.OnKeyDown(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            InitialWork();
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
            refreshTimer.Stop();
            minuteTimer.Stop();
            updateTimer.Stop();
            refreshTimer.Dispose();
            minuteTimer.Dispose();
            updateTimer.Dispose();
            tray.Dispose();
            base.OnFormClosed(e);
            painter.Dispose();
        }

        // Resizable from all edges, drag anywhere to move, no maximise on double-click.
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_NCLBUTTONDBLCLK = 0x00A3;
            if (m.Msg == WM_NCLBUTTONDBLCLK) return;
            if (m.Msg == WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int grip = (int)painter.S(6), w = ClientSize.Width, h = ClientSize.Height;
                bool l = p.X < grip, r = p.X >= w - grip, t = p.Y < grip, b = p.Y >= h - grip;
                int code;
                if (t && l) code = 13;          // HTTOPLEFT
                else if (t && r) code = 14;     // HTTOPRIGHT
                else if (b && l) code = 16;     // HTBOTTOMLEFT
                else if (b && r) code = 17;     // HTBOTTOMRIGHT
                else if (l) code = 10;          // HTLEFT
                else if (r) code = 11;          // HTRIGHT
                else if (t) code = 12;          // HTTOP
                else if (b) code = 15;          // HTBOTTOM
                else
                {
                    code = 2;                   // HTCAPTION: drag anywhere
                    if (painter.RefreshRect(w).Contains(p) || painter.CloseRect(w).Contains(p))
                        code = 1;               // HTCLIENT: header buttons handle clicks
                }
                m.Result = (IntPtr)code;
                return;
            }
            base.WndProc(ref m);
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
