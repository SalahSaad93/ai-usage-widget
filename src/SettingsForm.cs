using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    // The settings dialog. It edits a working copy of the values and, on OK, applies them
    // to the caller's Settings object (the caller saves the file). The copy-in / copy-out
    // is pure and self-tested; the window itself is never opened by the self-test.
    public sealed class SettingsForm : Form
    {
        public const string OpenRouterSecretName = "openrouter";
        public const string ProjectUrl = "https://github.com/SalahSaad93/ai-usage-widget";
        const string KeyPlaceholder = "leave empty to keep the current key";

        static readonly string[] LayoutValues = { "detailed", "compact" };
        static readonly string[] ThemeValues = { "system", "dark", "light" };
        static readonly string[] UpdateModeValues = { "auto", "notify", "off" };

        // Plain values between ReadFrom and ApplyTo: no WinForms types, so the round trip
        // is testable headlessly.
        public sealed class WorkingValues
        {
            public int RefreshMinutes;
            public string Layout;          // detailed | compact
            public bool AlwaysOnTop;
            public string Theme;           // system | dark | light
            public int Opacity;            // 40..100
            public int WarnPercent;
            public int CriticalPercent;
            public bool AutoDetect;
            public List<ProviderSetting> Providers;
            public bool NotifyEnabled;
            public int NotifyLow;          // 0 turns one off
            public int NotifyHigh;         // 0 turns one off
            public bool NotifyOnReset;
            public string UpdateMode;      // auto | notify | off
        }

        readonly Settings target;
        readonly WorkingValues working;
        readonly List<CheckBox> providerBoxes = new List<CheckBox>();
        readonly List<ConsentForm.ConsentRow> providerRows = new List<ConsentForm.ConsentRow>();
        readonly int wrapWidth;

        // Read at construction or right after; the version labels follow the property.
        string currentVersion;
        public string CurrentVersion
        {
            get { return currentVersion; }
            set
            {
                currentVersion = value;
                string line = "Current version: " + (currentVersion == null ? "(unknown)" : currentVersion);
                if (updatesVersionLabel != null) updatesVersionLabel.Text = line;
                if (aboutVersionLabel != null) aboutVersionLabel.Text = line;
            }
        }

        NumericUpDown refreshBox, warnBox, criticalBox, notifyLowBox, notifyHighBox;
        ComboBox layoutCombo, themeCombo, updateCombo;
        CheckBox topBox, autoDetectBox, notifyBox, resetBox;
        TrackBar opacityBar;
        Label opacityLabel, updatesVersionLabel, aboutVersionLabel;
        TextBox keyBox;
        bool keyPlaceholderShowing;
        bool clearKeyRequested;

        public SettingsForm(Settings settings, IList<IUsageProvider> providers)
        {
            target = settings;
            working = ReadFrom(settings);
            // The dialog lists every provider in the merged tick order (settings order
            // first, then unlisted ones), not the raw stored subset.
            working.Providers = MergedProviderTicks(settings, providers);
            wrapWidth = 460 * DeviceDpi / 96;

            Text = "AI Usage Widget: settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildGeneralTab());
            tabs.TabPages.Add(BuildSubscriptionsTab(providers));
            tabs.TabPages.Add(BuildNotificationsTab());
            tabs.TabPages.Add(BuildUpdatesTab());
            tabs.TabPages.Add(BuildAboutTab());

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(10);
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            root.Controls.Add(tabs);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.AutoSize = true;
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = new Padding(0, 8, 0, 0);
            var ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate
            {
                SaveOpenRouterKey();
                ApplyWorking();
            };
            var cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            LoadValuesIntoControls();
            ClientSize = new Size(560, 620);
        }

        // --- pure copy-in / copy-out -----------------------------------------------------

        public static WorkingValues ReadFrom(Settings s)
        {
            Settings d = s != null ? s : new Settings();
            var v = new WorkingValues();
            v.RefreshMinutes = Settings.Clamp(d.RefreshMinutes, 1, 120);
            v.Layout = d.Layout == "compact" ? "compact" : "detailed";
            v.AlwaysOnTop = d.AlwaysOnTop;
            v.Theme = d.Theme == "dark" || d.Theme == "light" ? d.Theme : "system";
            v.Opacity = Settings.Clamp(d.Opacity, 40, 100);
            v.WarnPercent = Settings.Clamp(d.WarnPercent, 1, 98);
            v.CriticalPercent = Settings.Clamp(d.CriticalPercent, v.WarnPercent + 1, 99);
            v.AutoDetect = d.AutoDetect;
            v.Providers = CopyProviders(d.Providers);

            NotificationSettings n = d.Notifications != null ? d.Notifications : new NotificationSettings();
            v.NotifyEnabled = n.Enabled;
            v.NotifyLow = n.Thresholds != null && n.Thresholds.Count > 0
                ? Settings.Clamp(n.Thresholds[0], 0, 100) : 80;
            v.NotifyHigh = n.Thresholds != null && n.Thresholds.Count > 1
                ? Settings.Clamp(n.Thresholds[1], 0, 100) : 0;
            v.NotifyOnReset = n.OnReset;

            v.UpdateMode = d.Update != null && IsUpdateMode(d.Update.Mode) ? d.Update.Mode : "notify";
            return v;
        }

        // Thresholds are validated here so both the OK click and the round trip obey the
        // same rule: 1..98 for warn, critical strictly above warn and at most 99.
        public static void ApplyTo(WorkingValues v, Settings s)
        {
            if (s == null || v == null) return;
            s.RefreshMinutes = Settings.Clamp(v.RefreshMinutes, 1, 120);
            s.Layout = v.Layout == "compact" ? "compact" : "detailed";
            s.AlwaysOnTop = v.AlwaysOnTop;
            s.Theme = v.Theme == "dark" || v.Theme == "light" ? v.Theme : "system";
            s.Opacity = Settings.Clamp(v.Opacity, 40, 100);
            s.WarnPercent = Settings.Clamp(v.WarnPercent, 1, 98);
            s.CriticalPercent = Settings.Clamp(v.CriticalPercent, s.WarnPercent + 1, 99);
            s.AutoDetect = v.AutoDetect;
            s.Providers = CopyProviders(v.Providers);

            if (s.Notifications == null) s.Notifications = new NotificationSettings();
            s.Notifications.Enabled = v.NotifyEnabled;
            var thresholds = new List<int>();
            if (v.NotifyLow > 0) thresholds.Add(Settings.Clamp(v.NotifyLow, 1, 100));
            if (v.NotifyHigh > 0 && (v.NotifyLow <= 0 || v.NotifyHigh != v.NotifyLow))
                thresholds.Add(Settings.Clamp(v.NotifyHigh, 1, 100));
            thresholds.Sort();
            s.Notifications.Thresholds = thresholds;
            s.Notifications.OnReset = v.NotifyOnReset;

            if (s.Update == null) s.Update = new UpdateSettings();
            s.Update.Mode = IsUpdateMode(v.UpdateMode) ? v.UpdateMode : "notify";
        }

        // Every provider exactly once, in dialog order: the stored entries keep their
        // order and ticks, then unlisted providers follow in registry order — unticked
        // when experimental, ticked otherwise.
        public static List<ProviderSetting> MergedProviderTicks(Settings s,
            IList<IUsageProvider> providers)
        {
            var result = new List<ProviderSetting>();
            if (providers == null) return result;
            var wanted = new List<string>();
            foreach (IUsageProvider p in providers)
                if (p != null && p.Id != null && !wanted.Contains(p.Id)) wanted.Add(p.Id);

            var listed = new Dictionary<string, bool>();
            if (s != null && s.Providers != null)
                foreach (ProviderSetting ps in s.Providers)
                    if (ps != null && ps.Id != null && wanted.Contains(ps.Id)
                        && !listed.ContainsKey(ps.Id))
                        listed[ps.Id] = ps.Enabled;

            var added = new List<string>();
            if (s != null && s.Providers != null)
                foreach (ProviderSetting ps in s.Providers)
                    if (ps != null && ps.Id != null && wanted.Contains(ps.Id)
                        && !added.Contains(ps.Id))
                    {
                        var row = new ProviderSetting();
                        row.Id = ps.Id;
                        row.Enabled = listed[ps.Id];
                        result.Add(row);
                        added.Add(ps.Id);
                    }
            foreach (IUsageProvider p in providers)
            {
                if (p == null || p.Id == null || added.Contains(p.Id)) continue;
                var row = new ProviderSetting();
                row.Id = p.Id;
                row.Enabled = Array.IndexOf(ProviderRegistry.ExperimentalIds, p.Id) < 0;
                result.Add(row);
                added.Add(p.Id);
            }
            return result;
        }

        static List<ProviderSetting> CopyProviders(List<ProviderSetting> list)
        {
            var copy = new List<ProviderSetting>();
            if (list == null) return copy;
            foreach (ProviderSetting ps in list)
                if (ps != null && !string.IsNullOrEmpty(ps.Id))
                    copy.Add(new ProviderSetting { Id = ps.Id, Enabled = ps.Enabled });
            return copy;
        }

        static bool IsUpdateMode(string mode)
        {
            return mode == "auto" || mode == "notify" || mode == "off";
        }

        // --- wiring ----------------------------------------------------------------------

        void LoadValuesIntoControls()
        {
            refreshBox.Value = Settings.Clamp(working.RefreshMinutes, 1, 120);
            layoutCombo.SelectedIndex = IndexOfValue(LayoutValues, working.Layout, 0);
            topBox.Checked = working.AlwaysOnTop;
            themeCombo.SelectedIndex = IndexOfValue(ThemeValues, working.Theme, 0);
            opacityBar.Value = Settings.Clamp(working.Opacity,
                opacityBar.Minimum, opacityBar.Maximum);
            opacityLabel.Text = opacityBar.Value + "%";
            warnBox.Value = Settings.Clamp(working.WarnPercent, 1, 98);
            criticalBox.Value = Settings.Clamp(working.CriticalPercent,
                (int)warnBox.Value + 1, 99);
            autoDetectBox.Checked = working.AutoDetect;
            for (int i = 0; i < providerRows.Count && i < working.Providers.Count; i++)
                providerBoxes[i].Checked = working.Providers[i].Enabled;
            notifyBox.Checked = working.NotifyEnabled;
            notifyLowBox.Value = Settings.Clamp(working.NotifyLow, 0, 100);
            notifyHighBox.Value = Settings.Clamp(working.NotifyHigh, 0, 100);
            resetBox.Checked = working.NotifyOnReset;
            updateCombo.SelectedIndex = IndexOfValue(UpdateModeValues, working.UpdateMode, 1);
        }

        void ApplyWorking()
        {
            working.RefreshMinutes = (int)refreshBox.Value;
            working.Layout = ValueAt(LayoutValues, layoutCombo.SelectedIndex, "detailed");
            working.AlwaysOnTop = topBox.Checked;
            working.Theme = ValueAt(ThemeValues, themeCombo.SelectedIndex, "system");
            working.Opacity = opacityBar.Value;
            working.WarnPercent = (int)warnBox.Value;
            working.CriticalPercent = (int)criticalBox.Value;
            working.AutoDetect = autoDetectBox.Checked;
            var ticks = new List<ProviderSetting>();
            for (int i = 0; i < providerRows.Count; i++)
            {
                var ps = new ProviderSetting();
                ps.Id = providerRows[i].Provider.Id;
                ps.Enabled = providerBoxes[i].Checked;
                ticks.Add(ps);
            }
            working.Providers = ticks;
            working.NotifyEnabled = notifyBox.Checked;
            working.NotifyLow = (int)notifyLowBox.Value;
            working.NotifyHigh = (int)notifyHighBox.Value;
            working.NotifyOnReset = resetBox.Checked;
            working.UpdateMode = ValueAt(UpdateModeValues, updateCombo.SelectedIndex, "notify");
            ApplyTo(working, target);
        }

        // The key is never displayed back: only a freshly typed value is stored, and the
        // stored one is deleted only via the Clear key button.
        void SaveOpenRouterKey()
        {
            if (keyBox == null) return;
            if (clearKeyRequested)
            {
                SecretStore.Clear(OpenRouterSecretName);
                return;
            }
            if (!keyPlaceholderShowing)
            {
                string value = keyBox.Text.Trim();
                if (value.Length > 0) SecretStore.Save(OpenRouterSecretName, value);
            }
        }

        void ShowKeyPlaceholder()
        {
            keyPlaceholderShowing = true;
            keyBox.PasswordChar = '\0';
            keyBox.Text = KeyPlaceholder;
            keyBox.ForeColor = SystemColors.GrayText;
        }

        static int IndexOfValue(string[] values, string value, int fallback)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] == value) return i;
            return fallback;
        }

        static string ValueAt(string[] values, int index, string fallback)
        {
            return index >= 0 && index < values.Length ? values[index] : fallback;
        }

        // --- small layout helpers --------------------------------------------------------

        static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.ColumnCount = 2;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        static void AddField(TableLayoutPanel t, string labelText, Control c)
        {
            var l = new Label();
            l.Text = labelText;
            l.AutoSize = true;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(4, 8, 14, 4);
            t.Controls.Add(l);
            c.Margin = new Padding(0, 4, 0, 4);
            t.Controls.Add(c);
        }

        static void AddSpan(TableLayoutPanel t, Control c)
        {
            c.Margin = new Padding(4, 8, 0, 4);
            t.Controls.Add(c);
            t.SetColumnSpan(c, 2);
        }

        static FlowLayoutPanel Flow(params Control[] children)
        {
            var f = new FlowLayoutPanel();
            f.FlowDirection = FlowDirection.LeftToRight;
            f.AutoSize = true;
            f.WrapContents = false;
            f.Margin = new Padding(0);
            foreach (Control c in children)
            {
                c.Margin = new Padding(0, 2, 8, 2);
                f.Controls.Add(c);
            }
            return f;
        }

        static ComboBox Combo(params string[] items)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (string item in items) c.Items.Add(item);
            return c;
        }

        static NumericUpDown Numeric(int min, int max)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Width = 70;
            return n;
        }

        static TabPage Titled(string title, Control content)
        {
            var page = new TabPage(title);
            page.Padding = new Padding(10);
            content.Dock = DockStyle.Top;
            page.Controls.Add(content);
            return page;
        }

        // --- tabs ------------------------------------------------------------------------

        TabPage BuildGeneralTab()
        {
            var grid = Grid();

            refreshBox = Numeric(1, 120);
            var minutesLabel = new Label();
            minutesLabel.Text = "minutes";
            minutesLabel.AutoSize = true;
            AddField(grid, "Refresh every", Flow(refreshBox, minutesLabel));

            layoutCombo = Combo("Detailed", "Compact");
            AddField(grid, "Layout", layoutCombo);

            topBox = new CheckBox();
            topBox.Text = "Always on top";
            topBox.AutoSize = true;
            AddSpan(grid, topBox);

            themeCombo = Combo("System", "Dark", "Light");
            AddField(grid, "Theme", themeCombo);

            opacityBar = new TrackBar();
            opacityBar.Minimum = 40;
            opacityBar.Maximum = 100;
            opacityBar.TickFrequency = 10;
            opacityBar.Width = 220;
            opacityLabel = new Label();
            opacityLabel.Text = "100%";
            opacityLabel.AutoSize = true;
            opacityBar.ValueChanged += delegate
            {
                opacityLabel.Text = opacityBar.Value + "%";
            };
            AddField(grid, "Opacity", Flow(opacityBar, opacityLabel));

            warnBox = Numeric(1, 98);
            criticalBox = Numeric(2, 99);
            warnBox.ValueChanged += delegate
            {
                if (warnBox.Value >= criticalBox.Value) criticalBox.Value = warnBox.Value + 1;
            };
            criticalBox.ValueChanged += delegate
            {
                if (criticalBox.Value <= warnBox.Value) warnBox.Value = criticalBox.Value - 1;
            };
            AddField(grid, "yellow from", warnBox);
            AddField(grid, "red above", criticalBox);

            return Titled("General", grid);
        }

        TabPage BuildSubscriptionsTab(IList<IUsageProvider> providers)
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            autoDetectBox = new CheckBox();
            autoDetectBox.Text = "Detect new subscriptions automatically";
            autoDetectBox.AutoSize = true;
            autoDetectBox.Margin = new Padding(4, 4, 0, 8);
            root.Controls.Add(autoDetectBox);

            var list = new TableLayoutPanel();
            list.Dock = DockStyle.Top;
            list.AutoSize = true;
            list.ColumnCount = 1;

            var byId = new Dictionary<string, IUsageProvider>();
            if (providers != null)
                foreach (IUsageProvider p in providers)
                    if (p != null && p.Id != null && !byId.ContainsKey(p.Id)) byId[p.Id] = p;

            bool hasOpenRouter = false;
            foreach (ProviderSetting tick in working.Providers)
            {
                IUsageProvider p;
                if (!byId.TryGetValue(tick.Id, out p)) continue;
                if (tick.Id == "openrouter") hasOpenRouter = true;
                var row = new ConsentForm.ConsentRow();
                row.Provider = p;
                row.Checked = tick.Enabled;
                row.Mark = Array.IndexOf(ProviderRegistry.ExperimentalIds, tick.Id) >= 0
                    ? ConsentForm.ExperimentalMark
                    : null;
                providerRows.Add(row);
                providerBoxes.Add(ConsentForm.AddProviderRow(list, row, wrapWidth));
            }

            if (hasOpenRouter)
            {
                var header = new Label();
                header.Text = "OpenRouter API key";
                header.AutoSize = true;
                header.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
                header.Margin = new Padding(0, 14, 0, 4);
                list.Controls.Add(header);

                keyBox = new TextBox();
                keyBox.Width = 260;
                keyBox.Margin = new Padding(24, 2, 8, 2);
                var clearBtn = new Button();
                clearBtn.Text = "Clear key";
                clearBtn.UseVisualStyleBackColor = true;
                clearBtn.Click += delegate
                {
                    clearKeyRequested = true;
                    ShowKeyPlaceholder();
                };
                keyBox.Enter += delegate
                {
                    if (keyPlaceholderShowing)
                    {
                        keyPlaceholderShowing = false;
                        keyBox.PasswordChar = '*';
                        keyBox.Text = "";
                        keyBox.ForeColor = SystemColors.WindowText;
                    }
                };
                keyBox.Leave += delegate
                {
                    if (keyBox.TextLength == 0) ShowKeyPlaceholder();
                };
                keyBox.TextChanged += delegate
                {
                    if (!keyPlaceholderShowing) clearKeyRequested = false;
                };
                list.Controls.Add(Flow(keyBox, clearBtn));
                var hint = ConsentForm.DimLabel(
                    "The key is stored protected on this PC and is never shown again.",
                    wrapWidth);
                list.Controls.Add(hint);
            }

            var scroll = new Panel();
            scroll.AutoScroll = true;
            scroll.Dock = DockStyle.Fill;
            scroll.Controls.Add(list);
            root.Controls.Add(scroll);
            return new TabPage("Subscriptions") { Padding = new Padding(10), Controls = { root } };
        }

        TabPage BuildNotificationsTab()
        {
            var grid = Grid();

            notifyBox = new CheckBox();
            notifyBox.Text = "Show usage notifications";
            notifyBox.AutoSize = true;
            AddSpan(grid, notifyBox);

            notifyLowBox = Numeric(0, 100);
            var lowUnit = new Label();
            lowUnit.Text = "% used";
            lowUnit.AutoSize = true;
            AddField(grid, "first threshold", Flow(notifyLowBox, lowUnit));

            notifyHighBox = Numeric(0, 100);
            var highUnit = new Label();
            highUnit.Text = "% used";
            highUnit.AutoSize = true;
            AddField(grid, "second threshold", Flow(notifyHighBox, highUnit));

            AddSpan(grid, ConsentForm.DimLabel("0 turns a threshold off.", wrapWidth));

            resetBox = new CheckBox();
            resetBox.Text = "Also notify when a limit resets";
            resetBox.AutoSize = true;
            AddSpan(grid, resetBox);

            return Titled("Notifications", grid);
        }

        TabPage BuildUpdatesTab()
        {
            var grid = Grid();

            updateCombo = Combo("Install automatically", "Tell me", "Off");
            AddField(grid, "Install updates", updateCombo);

            updatesVersionLabel = ConsentForm.DimLabel("Current version: (unknown)", wrapWidth);
            updatesVersionLabel.Margin = new Padding(4, 8, 0, 4);
            AddSpan(grid, updatesVersionLabel);

            AddSpan(grid, ConsentForm.DimLabel(
                "Updates are signature-checked before they are installed.", wrapWidth));

            return Titled("Updates", grid);
        }

        TabPage BuildAboutTab()
        {
            var grid = Grid();
            grid.ColumnCount = 1;
            grid.ColumnStyles.Clear();
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var name = new Label();
            name.Text = "AI Usage Widget";
            name.AutoSize = true;
            name.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);
            name.Margin = new Padding(4, 8, 0, 2);
            grid.Controls.Add(name);

            aboutVersionLabel = ConsentForm.DimLabel("Current version: (unknown)", wrapWidth);
            aboutVersionLabel.Margin = new Padding(4, 0, 0, 8);
            grid.Controls.Add(aboutVersionLabel);

            var dataBtn = new Button();
            dataBtn.Text = "Open data folder";
            dataBtn.UseVisualStyleBackColor = true;
            dataBtn.Click += delegate
            {
                try { Process.Start(AppPaths.DataDir); }
                catch (Exception) { }
            };
            var logBtn = new Button();
            logBtn.Text = "Open log";
            logBtn.UseVisualStyleBackColor = true;
            logBtn.Click += delegate
            {
                try { Process.Start(AppPaths.LogPath); }
                catch (Exception) { }
            };
            grid.Controls.Add(Flow(dataBtn, logBtn));

            var link = new LinkLabel();
            link.Text = ProjectUrl;
            link.AutoSize = true;
            link.Margin = new Padding(4, 10, 0, 2);
            link.Links.Add(0, link.Text.Length);
            link.LinkClicked += delegate
            {
                try { Process.Start(ProjectUrl); }
                catch (Exception) { }
            };
            grid.Controls.Add(link);

            var vendor = new Label();
            vendor.Text = "Not affiliated with any AI vendor.";
            vendor.AutoSize = true;
            vendor.ForeColor = SystemColors.GrayText;
            vendor.Margin = new Padding(4, 10, 0, 0);
            grid.Controls.Add(vendor);

            return Titled("About", grid);
        }
    }
}
