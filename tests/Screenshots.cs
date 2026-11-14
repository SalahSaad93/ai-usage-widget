using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    // Renders every window with made-up data into PNG files, so the look can be reviewed
    // (and documented) without touching the real widget, real logins or the network.
    public static class Screenshots
    {
        public static int RenderAll(string dir)
        {
            // Settings and secrets used by the dialogs go to a throwaway folder.
            Environment.SetEnvironmentVariable("AI_USAGE_WIDGET_DATA", Path.Combine(
                Path.GetTempPath(), "ai-usage-widget-shots-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(dir);
            Application.EnableVisualStyles();

            DateTime now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            List<UsageSnapshot> snapshots = SampleSnapshots(now);
            var ids = new List<string>();
            foreach (UsageSnapshot s in snapshots) ids.Add(s.ProviderId);

            RenderWidget(Path.Combine(dir, "widget-detailed-dark.png"), snapshots, ids, now, 0, Theme.Dark);
            RenderWidget(Path.Combine(dir, "widget-detailed-light.png"), snapshots, ids, now, 0, Theme.Light);
            RenderWidget(Path.Combine(dir, "widget-compact-dark.png"), snapshots, ids, now, 1, Theme.Dark);
            RenderWidget(Path.Combine(dir, "widget-compact-light.png"), snapshots, ids, now, 1, Theme.Light);

            var providers = new List<IUsageProvider>(ProviderRegistry.All);
            using (var consent = new ConsentForm(providers, delegate(string id)
            {
                return id == "claude-code" || id == "codex" || id == "zai" || id == "cursor";
            }))
                RenderForm(consent, Path.Combine(dir, "consent.png"));

            using (var settingsForm = new SettingsForm(new Settings(), providers))
            {
                settingsForm.CurrentVersion = "0.4.0";
                TabControl tabs = FindTabs(settingsForm);
                int count = tabs == null ? 1 : tabs.TabPages.Count;
                for (int i = 0; i < count; i++)
                {
                    string name = tabs == null ? "all" : tabs.TabPages[i].Text.ToLowerInvariant();
                    int tab = i;
                    RenderForm(settingsForm, Path.Combine(dir, "settings-" + name + ".png"),
                        delegate { if (tabs != null) tabs.SelectedIndex = tab; });
                }
            }
            return 0;
        }

        static void RenderWidget(string path, List<UsageSnapshot> snapshots, List<string> ids,
            DateTime now, int mode, Theme theme)
        {
            using (var painter = new Painter(1f, theme, 50, 70))
            {
                List<RowVm> rows = Painter.BuildRows(snapshots, now, ids);
                int width = mode == 0 ? 340 : (int)Math.Ceiling(painter.CompactWidthFor(rows));
                int height = (int)Math.Ceiling(painter.HeaderHeight
                    + painter.ContentHeight(mode, rows) + painter.FooterHeight);
                using (var bmp = new Bitmap(width, height))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(theme.Background);
                    var none = new HitInfo();
                    none.Kind = HitKind.None;
                    none.Row = -1;
                    none.Window = -1;
                    painter.Draw(g, width, height, mode, 0, rows, "updated just now",
                        "+1 not supported yet", none);
                    bmp.Save(path, ImageFormat.Png);
                }
            }
        }

        // Shows the dialog off-screen just long enough to lay out and paint, then copies it.
        static void RenderForm(Form form, string path)
        {
            RenderForm(form, path, null);
        }

        static void RenderForm(Form form, string path, Action prepare)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.ShowInTaskbar = false;
            form.Show();
            if (prepare != null) prepare();
            Application.DoEvents();
            using (var bmp = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            form.Hide();
        }

        static TabControl FindTabs(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var tabs = c as TabControl;
                if (tabs != null) return tabs;
                TabControl nested = FindTabs(c);
                if (nested != null) return nested;
            }
            return null;
        }

        static List<UsageSnapshot> SampleSnapshots(DateTime now)
        {
            var list = new List<UsageSnapshot>();

            UsageSnapshot claude = Snap("claude-code", "Claude Code", "Max 5x", now);
            claude.Windows.Add(Win(WindowKind.FiveHour, "5h", null, true, 18000, 34, now.AddHours(3.2)));
            claude.Windows.Add(Win(WindowKind.Weekly, "7d", null, true, 604800, 62, now.AddDays(2.1)));
            claude.Windows.Add(Win(WindowKind.Weekly, "7d-opus", "week · Opus", false, 604800, 81, now.AddDays(2.1)));
            list.Add(claude);

            UsageSnapshot codex = Snap("codex", "Codex", "Plus", now);
            codex.Windows.Add(Win(WindowKind.FiveHour, "18000", null, true, 18000, 12, now.AddHours(1.5)));
            codex.Windows.Add(Win(WindowKind.Weekly, "604800", null, true, 604800, 47, now.AddDays(4.6)));
            list.Add(codex);

            UsageSnapshot zai = Snap("zai", "Z.ai GLM", "Coding Pro", now);
            zai.Windows.Add(Win(WindowKind.FiveHour, "5h", null, true, 18000, 8, now.AddHours(4.4)));
            zai.Windows.Add(Win(WindowKind.Weekly, "7d", null, true, 604800, 92, now.AddDays(1.2)));
            UsageWindow tools = Win(WindowKind.Monthly, "30d-tools", "web tools", true, 2592000, 7, null);
            tools.Used = 7;
            tools.Limit = 100;
            tools.Unit = "requests";
            zai.Windows.Add(tools);
            list.Add(zai);

            UsageSnapshot router = Snap("openrouter", "OpenRouter", "", now);
            UsageWindow limit = Win(WindowKind.Monthly, "limit", "key limit", true, 2592000, 25, now.AddDays(16));
            limit.Used = 12.5;
            limit.Limit = 50;
            limit.Unit = "USD";
            router.Windows.Add(limit);
            list.Add(router);

            UsageSnapshot cursor = Snap("cursor", "Cursor", "Pro", now.AddMinutes(-42));
            cursor.Status = ProviderStatus.Stale;
            cursor.Message = "sign in needed: open Cursor";
            cursor.Windows.Add(Win(WindowKind.Monthly, "cycle", "billing cycle", true, 2592000, 58, now.AddDays(9)));
            list.Add(cursor);

            UsageSnapshot muse = Snap("muse", "Muse", "", now);
            muse.Status = ProviderStatus.AuthRequired;
            muse.Message = "sign in needed: run muse login";
            list.Add(muse);
            return list;
        }

        static UsageSnapshot Snap(string id, string name, string plan, DateTime fetched)
        {
            var s = new UsageSnapshot();
            s.ProviderId = id;
            s.DisplayName = name;
            s.PlanName = plan;
            s.Status = ProviderStatus.Ok;
            s.FetchedAtUtc = fetched;
            return s;
        }

        static UsageWindow Win(WindowKind kind, string key, string label, bool primary,
            int duration, double percent, DateTime? resets)
        {
            var w = new UsageWindow();
            w.Kind = kind;
            w.Key = key;
            w.Label = label;
            w.Primary = primary;
            w.DurationSeconds = duration;
            w.UsedPercent = percent;
            w.ResetsAtUtc = resets;
            return w;
        }
    }
}
