using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    public enum HitKind
    {
        None,
        MenuButton,
        RefreshButton,
        CloseButton,
        DetailedChip,
        CompactChip,
        RowTitle,
        UsageWindow
    }

    // What a point on the widget lands on; the tooltip and hover code read it. Row and
    // Window are -1 when the hit is not on a row, and Window is -1 unless the hit is a
    // usage line.
    public struct HitInfo
    {
        public HitKind Kind;
        public int Row;
        public int Window;

        public bool Equals(HitInfo other)
        {
            return Kind == other.Kind && Row == other.Row && Window == other.Window;
        }
    }

    // What the painter draws; UsageSnapshot stays free of UI concerns.
    public sealed class WindowVm
    {
        public WindowKind Kind;
        public string Label;
        public bool Primary;
        public string Detail;
        public string Unit;
        public double UsedPercent;
        public string Resets;   // "2h 10m", or null when the vendor gave no reset time
        public DateTime? ResetsAtUtc;   // the absolute reset time, for the tooltip
        public double? PacePercent;   // the even-pace percent at nowUtc, when the shape is known
        public string ForecastText;   // "limit in 1h 20m"; null without history, "" when nothing
        public bool ForecastBeforeReset;   // the projected limit lands before the window resets
    }

    public sealed class RowVm
    {
        public string Name;
        public string Plan;
        public ProviderStatus Status;
        public string Msg;      // auth / not-configured / error text, or the stale tag
        public DateTime FetchedAtUtc;   // last good fetch, for the title tooltip's age
        public List<WindowVm> Windows;

        public RowVm()
        {
            Windows = new List<WindowVm>();
        }
    }

    // All drawing. Reproduces prototype/PrototypeWinForms.cs (the approved look), minus the
    // PROTOTYPE footer and layout chips; layout is chosen from the menu.
    public sealed class Painter : IDisposable
    {
        static readonly WindowKind[] KindOrder = { WindowKind.FiveHour, WindowKind.Daily, WindowKind.Weekly, WindowKind.Monthly, WindowKind.Other };

        // Drawn in the body when no provider is shown (spec section 13).
        public const string EmptyStateText = "No AI subscriptions detected on this PC";

        readonly Theme theme;
        readonly int warnPercent;
        readonly int criticalPercent;
        readonly float s;
        readonly Font fName, fPlan, fSmall, fPct;
        readonly StringFormat near, far;

        // Dark at 50/70: what the widget has always drawn; the self-test uses this shape.
        public Painter(float dpiScale)
            : this(dpiScale, Theme.Dark, 50, 70)
        {
        }

        public Painter(float dpiScale, Theme palette, int warn, int critical)
        {
            s = dpiScale;
            theme = palette;
            warnPercent = warn;
            criticalPercent = critical;
            fName = new Font("Segoe UI Semibold", S(13), GraphicsUnit.Pixel);
            fPlan = new Font("Segoe UI", S(11), GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", S(10.5f), GraphicsUnit.Pixel);
            fPct = new Font("Segoe UI", S(11.5f), GraphicsUnit.Pixel);
            near = new StringFormat(StringFormat.GenericTypographic);
            near.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            near.LineAlignment = StringAlignment.Center;
            near.Trimming = StringTrimming.EllipsisCharacter;
            far = new StringFormat(StringFormat.GenericTypographic);
            far.FormatFlags |= StringFormatFlags.NoWrap;
            far.LineAlignment = StringAlignment.Center;
            far.Alignment = StringAlignment.Far;
        }

        // Windows 11 rounds the corners and draws its own edge; Windows 10 keeps the 1 px frame.
        public bool DrawBorder = true;

        public Color Background { get { return theme.Background; } }

        public float S(float v)
        {
            return v * s;
        }

        // One colour rule for everything at the built-in 50/70 thresholds; kept for the
        // self-test. The widget draws with ColorAt and its settings thresholds.
        public static Color ColorFor(double percent)
        {
            return percent < 50 ? Theme.Dark.Green
                : (percent <= 70 ? Theme.Dark.Yellow : Theme.Dark.Red);
        }

        // Below warn green, warn..critical inclusive yellow, above critical red.
        public Color ColorAt(double percent)
        {
            return percent < warnPercent ? theme.Green
                : (percent <= criticalPercent ? theme.Yellow : theme.Red);
        }

        static Color Fade(Color c, bool dim)
        {
            return dim ? Color.FromArgb(115, c) : c;
        }

        // Hover fill: a step toward the text colour — lighter on the dark theme, a shade
        // darker on the light one, so the fill reads on both.
        Color HoverFill(Color fill)
        {
            return Blend(fill, theme.Text, 0.10f);
        }

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                a.R + (int)((b.R - a.R) * t),
                a.G + (int)((b.G - a.G) * t),
                a.B + (int)((b.B - a.B) * t));
        }

        static string PercentText(double v)
        {
            return Math.Round(v).ToString(CultureInfo.InvariantCulture) + "%";
        }

        // "2h 10m" / "3d 4h" / "19d"
        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            int days = (int)t.TotalDays;
            if (days >= 1)
                return t.Hours > 0 ? days + "d " + t.Hours + "h" : days + "d";
            int hours = (int)t.TotalHours;
            if (hours >= 1)
                return t.Minutes > 0 ? hours + "h " + t.Minutes + "m" : hours + "h";
            int minutes = (int)t.TotalMinutes;
            if (minutes < 1) minutes = 1;
            return minutes + "m";
        }

        // With a History the windows also carry their pace and burn forecast; null (the
        // existing callers and self-tests) keeps today's rows.
        public static List<RowVm> BuildRows(List<UsageSnapshot> snapshots, DateTime nowUtc,
            IList<string> orderedIds, History history = null)
        {
            var rows = new List<RowVm>();
            if (snapshots == null || orderedIds == null) return rows;
            // Rows follow the active provider order, not the order snapshots happen to sit in
            // the cache; a provider that is not active is not drawn.
            foreach (string id in orderedIds)
            {
                UsageSnapshot sn = null;
                foreach (UsageSnapshot s in snapshots)
                    if (s.ProviderId == id) { sn = s; break; }
                if (sn == null) continue;
                var row = new RowVm();
                row.Name = string.IsNullOrEmpty(sn.DisplayName) ? sn.ProviderId : sn.DisplayName;
                row.Plan = sn.PlanName;
                row.Status = sn.Status;
                row.FetchedAtUtc = sn.FetchedAtUtc;
                if (sn.Status == ProviderStatus.Stale)
                {
                    row.Msg = "stale " + Duration(nowUtc - sn.FetchedAtUtc);
                    if (!string.IsNullOrEmpty(sn.Message))
                        row.Msg += " · " + sn.Message;
                }
                else if (sn.Status != ProviderStatus.Ok)
                    row.Msg = string.IsNullOrEmpty(sn.Message) ? sn.Status.ToString() : sn.Message;
                else if (sn.Windows.Count == 0)
                    row.Msg = string.IsNullOrEmpty(sn.Message) ? "no usage reported" : sn.Message;
                foreach (UsageWindow w in sn.Windows)
                {
                    var vm = new WindowVm();
                    vm.Kind = w.Kind;
                    vm.Label = string.IsNullOrEmpty(w.Label) ? DefaultLabel(w.Kind) : w.Label;
                    vm.Primary = w.Primary;
                    vm.UsedPercent = w.UsedPercent;
                    vm.Unit = w.Unit;
                    if (w.Used.HasValue && w.Limit.HasValue)
                        vm.Detail = Number(w.Used.Value) + " / " + Number(w.Limit.Value);
                    if (w.ResetsAtUtc.HasValue)
                    {
                        vm.ResetsAtUtc = w.ResetsAtUtc;
                        TimeSpan until = w.ResetsAtUtc.Value - nowUtc;
                        vm.Resets = until.TotalSeconds <= 0 ? "now" : Duration(until);
                    }
                    vm.PacePercent = Forecast.PacePercent(w, nowUtc);
                    if (history != null)
                    {
                        ForecastResult fc = Forecast.Project(w,
                            history.Samples(sn.ProviderId, w.Key), nowUtc);
                        vm.ForecastText = fc.Text;
                        vm.ForecastBeforeReset = fc.BeforeReset;
                    }
                    row.Windows.Add(vm);
                }
                // five-hour above weekly above monthly, regardless of vendor field order
                row.Windows.Sort(delegate(WindowVm a, WindowVm b)
                {
                    int primary = b.Primary.CompareTo(a.Primary);
                    if (primary != 0) return primary;
                    return KindIndex(a.Kind).CompareTo(KindIndex(b.Kind));
                });
                rows.Add(row);
            }
            return rows;
        }

        public float HeaderHeight
        {
            get { return S(30); }
        }

        public float ContentHeight(int mode, List<RowVm> rows)
        {
            float h = mode == 1 ? S(20) : S(2);
            foreach (RowVm r in rows) h += RowHeight(mode, r);
            return h + S(2);
        }

        public float RowHeight(int mode, RowVm r)
        {
            if (mode == 1) return S(24);
            int lines;
            if (r.Windows.Count > 0)
                lines = r.Windows.Count;
            else
                lines = 1;   // message rows: auth / not-configured / error / Ok-with-message
            return S(8 + 18 + lines * 19 + 7 + 6);   // padding + title + lines + padding + gap
        }

        public float FooterHeight
        {
            get { return S(30); }
        }

        // The scroll offset drawing and hit-testing agree on: never below the content.
        public int ClampedScroll(float winWidth, float winHeight, int mode, int scroll,
            List<RowVm> rows)
        {
            float bodyHeight = winHeight - HeaderHeight - FooterHeight;
            float contentH = ContentHeight(mode, rows);
            return (int)Math.Max(0, Math.Min(scroll, contentH - bodyHeight));
        }

        // Layout switch in the footer, as in the approved prototype.
        public RectangleF DetailedRect(float w, float h)
        {
            return new RectangleF(w - S(132), h - FooterHeight + S(4), S(60), S(20));
        }

        public RectangleF CompactRect(float w, float h)
        {
            return new RectangleF(w - S(68), h - FooterHeight + S(4), S(60), S(20));
        }

        // "+N not supported yet" sits left of the layout chips; omitted when the window is
        // too narrow for it to clear the chips.
        void DrawFooterNote(Graphics g, float w, float h, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            float left = S(10);
            float right = DetailedRect(w, h).Left - S(8);
            if (right - left < S(20)) return;
            if (g.MeasureString(text, fSmall, 1000, near).Width > right - left) return;
            Txt(g, text, fSmall, theme.DimText,
                new RectangleF(left, h - FooterHeight, right - left, FooterHeight), false);
        }

        // Width the compact layout needs to show every title whole.
        public float CompactWidthFor(List<RowVm> rows)
        {
            float titleNeeded = 0;
            float messageNeeded = 0;
            using (var probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                if (rows != null)
                    foreach (RowVm r in rows)
                    {
                        float w = g.MeasureString(r.Name ?? "", fName, 1000, near).Width;
                        if (!string.IsNullOrEmpty(r.Plan))
                            w += S(6) + g.MeasureString(r.Plan, fPlan, 1000, near).Width;
                        if (w > titleNeeded) titleNeeded = w;
                        float m = g.MeasureString(r.Msg ?? "", fSmall, 1000, near).Width;
                        if (m > messageNeeded) messageNeeded = m;
                    }
            }
            int columns = CompactColumns(rows).Count;
            if (columns == 0)
            {
                // No usage columns: the title gets 45 % and the message 55 % of the width,
                // so the width grows until both shares fit their widest text.
                float byTitle = (titleNeeded + S(20)) * 100f / 45f;
                float byMessage = (messageNeeded + S(20)) * 100f / 55f;
                return Math.Max(S(420), Math.Max(byTitle, byMessage));
            }
            float needed = titleNeeded + S(20 + 82 * columns + 2);   // margins + one 82 px column per kind
            return Math.Max(S(420), needed);
        }

        // --- header / footer geometry (shared by drawing, hit-testing and clicks) -------

        public RectangleF MenuRect(float w)
        {
            return new RectangleF(w - S(80), S(5), S(22), S(20));
        }

        public RectangleF RefreshRect(float w)
        {
            return new RectangleF(w - S(54), S(5), S(22), S(20));
        }

        public RectangleF CloseRect(float w)
        {
            return new RectangleF(w - S(28), S(5), S(22), S(20));
        }

        // --- detailed-mode geometry (shared by drawing and hit-testing) ------------------

        public float DetailedBodyPad()
        {
            return S(2);
        }

        public RectangleF DetailedCardRect(RowVm r, float y, float width)
        {
            return new RectangleF(S(8), y, width - S(16), RowHeight(0, r) - S(6));
        }

        public RectangleF DetailedTitleBox(RectangleF card)
        {
            return new RectangleF(card.X + S(10), card.Y + S(8), card.Width - S(20), S(18));
        }

        // One usage line inside a card; message rows draw their text in line 0's box.
        public RectangleF DetailedLineBox(RectangleF card, int line)
        {
            return new RectangleF(card.X + S(10), card.Y + S(8) + S(18) + S(19) * line,
                card.Width - S(20), S(19));
        }

        // --- compact-mode geometry (shared by drawing and hit-testing) -------------------

        public float CompactLabelBand()
        {
            return S(20);
        }

        public float CompactRowPitch()
        {
            return S(24);
        }

        public float CompactColsLeft(float width, List<RowVm> rows)
        {
            return width - S(10) - S(82) * CompactColumns(rows).Count;
        }

        public float CompactColumnX(float cols, int column)
        {
            return cols + S(82) * column + S(8);
        }

        public RectangleF CompactTitleBox(float cols, float y)
        {
            return new RectangleF(S(10), y, cols - S(10), S(24));
        }

        public RectangleF CompactCellBox(float cols, int column, float y)
        {
            return new RectangleF(CompactColumnX(cols, column), y, S(82) - S(8), S(24));
        }

        // With no usage columns at all (no shown row has a primary window) the compact row
        // splits: the title keeps the left 45 % and the row's message takes the right 55 %.
        public RectangleF CompactNoColumnTitle(float width, float y)
        {
            float split = width * 0.45f;
            return new RectangleF(S(10), y, Math.Max(0f, split - S(10)), S(24));
        }

        public RectangleF CompactNoColumnMessage(float width, float y)
        {
            float split = width * 0.45f;
            return new RectangleF(split, y, Math.Max(0f, width - S(10) - split), S(24));
        }

        public void Draw(Graphics g, float winWidth, float winHeight, int mode, int scroll,
            List<RowVm> rows, string headerNote, string footerNote, HitInfo hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float header = HeaderHeight;
            var body = new RectangleF(0, header, winWidth, winHeight - header - FooterHeight);
            int clamped = ClampedScroll(winWidth, winHeight, mode, scroll, rows);
            g.SetClip(body);
            if (mode == 0) DrawDetailed(g, header - clamped, winWidth, rows);
            else DrawCompact(g, header - clamped, winWidth, rows);
            g.ResetClip();

            if (rows == null || rows.Count == 0)
                Txt(g, EmptyStateText, fSmall, theme.DimText,
                    new RectangleF(S(10), header + S(10), winWidth - S(20), S(19)), false);

            DrawHeader(g, winWidth, headerNote, hover);
            DrawFooterNote(g, winWidth, winHeight, footerNote);
            Chip(g, "Detailed", DetailedRect(winWidth, winHeight), mode == 0,
                hover.Kind == HitKind.DetailedChip);
            Chip(g, "Compact", CompactRect(winWidth, winHeight), mode == 1,
                hover.Kind == HitKind.CompactChip);
            if (DrawBorder)
                using (var pen = new Pen(theme.Line)) g.DrawRectangle(pen, 0, 0, winWidth - 1, winHeight - 1);
        }

        void DrawHeader(Graphics g, float w, string headerNote, HitInfo hover)
        {
            float header = HeaderHeight;
            Txt(g, "AI Usage", fName, theme.Text, new RectangleF(S(10), 0, S(70), header), false);
            float tx = S(10) + g.MeasureString("AI Usage", fName, 1000, near).Width + S(8);
            Txt(g, headerNote, fSmall, theme.DimText, new RectangleF(tx, S(1), w - tx - S(88), header), false);
            Chip(g, "⋯", MenuRect(w), false, hover.Kind == HitKind.MenuButton);
            Chip(g, "⟳", RefreshRect(w), false, hover.Kind == HitKind.RefreshButton);
            Chip(g, "✕", CloseRect(w), false, hover.Kind == HitKind.CloseButton);
        }

        void DrawDetailed(Graphics g, float y, float width, List<RowVm> rows)
        {
            y += DetailedBodyPad();
            foreach (RowVm r in rows)
            {
                RectangleF card = DetailedCardRect(r, y, width);
                FillRound(g, theme.Card, card, S(6));
                RectangleF title = DetailedTitleBox(card);
                Title(g, r, title);
                bool dim = r.Status == ProviderStatus.Stale;
                if (r.Windows.Count > 0)
                {
                    float labelWidth = DetailedLabelWidth(g, r.Windows);
                    for (int j = 0; j < r.Windows.Count; j++)
                    {
                        WindowVm wnd = r.Windows[j];
                        RectangleF ln = DetailedLineBox(card, j);
                        Txt(g, wnd.Label, fSmall, Fade(theme.DimText, dim),
                            new RectangleF(ln.X, ln.Y, labelWidth, ln.Height), false);
                        float bx = ln.X + labelWidth, bw = ln.Right - S(84) - S(40) - bx;
                        if (bw > S(10))
                            Bar(g, wnd.UsedPercent, new RectangleF(bx, ln.Y + S(5.5f), bw, S(8)), dim,
                                wnd.PacePercent);
                        Txt(g, PercentText(wnd.UsedPercent), fPct, Fade(ColorAt(wnd.UsedPercent), dim),
                            new RectangleF(ln.Right - S(84) - S(40), ln.Y, S(40), ln.Height), true);
                        if (wnd.ForecastBeforeReset && !string.IsNullOrEmpty(wnd.ForecastText))
                            Txt(g, wnd.ForecastText, fSmall, Fade(theme.Yellow, dim),
                                new RectangleF(ln.Right - S(84), ln.Y, S(84), ln.Height), true);
                        else if (wnd.Resets != null)
                            Txt(g, "resets " + wnd.Resets, fSmall, Fade(theme.DimText, dim),
                                new RectangleF(ln.Right - S(84), ln.Y, S(84), ln.Height), true);
                        else if (wnd.Detail != null)
                            Txt(g, wnd.Detail, fSmall, Fade(theme.DimText, dim),
                                new RectangleF(ln.Right - S(84), ln.Y, S(84), ln.Height), true);
                    }
                }
                else
                {
                    Color c = r.Status == ProviderStatus.AuthRequired ? theme.Yellow : theme.DimText;
                    RectangleF ln = DetailedLineBox(card, 0);
                    Txt(g, r.Msg, fSmall, c, new RectangleF(ln.X, ln.Y, ln.Width, ln.Height), false);
                }
                y += RowHeight(0, r);
            }
        }

        void DrawCompact(Graphics g, float y, float width, List<RowVm> rows)
        {
            List<WindowKind> kinds = CompactColumns(rows);
            float cols = CompactColsLeft(width, rows);
            bool noColumns = kinds.Count == 0;
            for (int i = 0; i < kinds.Count; i++)
                Txt(g, DefaultLabel(kinds[i]), fSmall, theme.DimText,
                    new RectangleF(CompactColumnX(cols, i), y, S(82), CompactLabelBand()), false);
            y += CompactLabelBand();
            foreach (RowVm r in rows)
            {
                Title(g, r, noColumns ? CompactNoColumnTitle(width, y) : CompactTitleBox(cols, y));
                bool dim = r.Status == ProviderStatus.Stale;
                if (r.Windows.Count > 0)
                {
                    for (int i = 0; i < kinds.Count; i++)
                    {
                        RectangleF cell = CompactCellBox(cols, i, y);
                        WindowVm wnd = WindowInColumn(r.Windows, kinds[i]);
                        if (wnd == null)
                        {
                            Txt(g, "—", fSmall, Fade(theme.DimText, dim), cell, true);
                            continue;
                        }
                        Bar(g, wnd.UsedPercent, new RectangleF(cell.X, y + S(9.5f), cell.Width - S(34), S(5)), dim,
                            wnd.PacePercent);
                        Txt(g, PercentText(wnd.UsedPercent), fSmall, Fade(ColorAt(wnd.UsedPercent), dim),
                            new RectangleF(cell.Right - S(34), y, S(34), S(24)), true);
                    }
                }
                else
                {
                    Color c = r.Status == ProviderStatus.AuthRequired ? theme.Yellow : theme.DimText;
                    RectangleF msgBox = noColumns
                        ? CompactNoColumnMessage(width, y)
                        : new RectangleF(cols + S(8), y, S(82) * kinds.Count - S(8), S(24));
                    Txt(g, r.Msg, fSmall, c, msgBox, false);
                }
                y += CompactRowPitch();
            }
        }

        void Title(Graphics g, RowVm r, RectangleF box)
        {
            float x = box.X;
            Txt(g, r.Name, fName, r.Status == ProviderStatus.NotConfigured ? theme.DimText : theme.Text, box, false);
            x += g.MeasureString(r.Name, fName, 1000, near).Width + S(6);
            if (!string.IsNullOrEmpty(r.Plan))
            {
                float planW = g.MeasureString(r.Plan, fPlan, 1000, near).Width;
                if (x + planW <= box.Right)   // the plan shows whole or not at all, never clipped
                {
                    Txt(g, r.Plan, fPlan, theme.DimText, new RectangleF(x, box.Y + S(1), planW, box.Height), false);
                    x += planW + S(6);
                }
            }
            if (r.Status == ProviderStatus.Stale && x < box.Right)
                Txt(g, r.Msg, fSmall, theme.Yellow, new RectangleF(x, box.Y + S(1), box.Right - x, box.Height), false);
        }

        void Chip(Graphics g, string glyph, RectangleF r, bool active, bool hovered)
        {
            Color fill = active ? theme.Accent : theme.Card;
            if (hovered) fill = HoverFill(fill);
            FillRound(g, fill, r, S(4));
            using (var b = new SolidBrush(active ? theme.ChipText : theme.DimText))
            using (var c = new StringFormat())
            {
                c.Alignment = StringAlignment.Center;
                c.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, fPlan, b, r, c);
            }
        }

        void Bar(Graphics g, double v, RectangleF r, bool dim, double? pacePercent)
        {
            FillRound(g, theme.Line, r, r.Height / 2);
            float w = Math.Max(r.Height, r.Width * (float)(Math.Min(100, Math.Max(0, v)) / 100));
            FillRound(g, Fade(ColorAt(v), dim), new RectangleF(r.X, r.Y, w, r.Height), r.Height / 2);
            RectangleF? tick = PaceTickRect(r, pacePercent);
            if (tick.HasValue)
                using (var b = new SolidBrush(Color.FromArgb(178, theme.Text)))
                    g.FillRectangle(b, tick.Value);
        }

        // The vertical pace tick in a usage bar: 1 px scaled, slightly taller than the bar,
        // at the even-pace position. Usage left of it means under pace. Null when the pace
        // is unknown.
        public RectangleF? PaceTickRect(RectangleF bar, double? pacePercent)
        {
            if (!pacePercent.HasValue) return null;
            double pace = Math.Min(100.0, Math.Max(0.0, pacePercent.Value));
            float x = bar.X + (float)(bar.Width * pace / 100.0);
            return new RectangleF(x, bar.Y - S(2), Math.Max(1f, S(1)), bar.Height + S(4));
        }

        public static string DefaultLabel(WindowKind kind)
        {
            if (kind == WindowKind.FiveHour) return "5 hours";
            if (kind == WindowKind.Daily) return "day";
            if (kind == WindowKind.Weekly) return "week";
            if (kind == WindowKind.Monthly) return "month";
            return "usage";
        }

        // "7" for whole numbers, "7.5" otherwise.
        static string Number(double value)
        {
            return value == Math.Round(value)
                ? value.ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        static int KindIndex(WindowKind kind)
        {
            for (int i = 0; i < KindOrder.Length; i++)
                if (KindOrder[i] == kind) return i;
            return KindOrder.Length;
        }

        // The label column fits the longest label of the row, between 48 and 110 px.
        float DetailedLabelWidth(Graphics g, List<WindowVm> windows)
        {
            float width = S(48);
            foreach (WindowVm w in windows)
                width = Math.Max(width,
                    Math.Min(S(110), g.MeasureString(w.Label, fSmall, 1000, near).Width + S(6)));
            return width;
        }

        // Compact columns: the kinds some shown row has a primary window for, in kind order.
        public static List<WindowKind> CompactColumns(List<RowVm> rows)
        {
            var result = new List<WindowKind>();
            if (rows == null) return result;
            foreach (WindowKind kind in KindOrder)
            {
                if (kind == WindowKind.Other) continue;
                bool present = false;
                foreach (RowVm r in rows)
                    foreach (WindowVm w in r.Windows)
                        if (w.Primary && w.Kind == kind) present = true;
                if (present) result.Add(kind);
            }
            return result;
        }

        static int ColumnOf(WindowKind kind)
        {
            if (kind == WindowKind.FiveHour) return 0;
            if (kind == WindowKind.Weekly) return 1;
            return 2;
        }

        static WindowVm WindowInColumn(List<WindowVm> windows, WindowKind kind)
        {
            foreach (WindowVm w in windows)
                if (w.Primary && w.Kind == kind) return w;
            return null;
        }

        // --- hit-testing -----------------------------------------------------------------

        // Where a point lands, using the same layout maths as Draw. The header buttons and
        // footer chips win over the body (the body never reaches those bands).
        public HitInfo HitTest(Point p, float winWidth, float winHeight, int mode, int scroll,
            List<RowVm> rows)
        {
            if (MenuRect(winWidth).Contains(p)) return new HitInfo { Kind = HitKind.MenuButton, Row = -1, Window = -1 };
            if (RefreshRect(winWidth).Contains(p)) return new HitInfo { Kind = HitKind.RefreshButton, Row = -1, Window = -1 };
            if (CloseRect(winWidth).Contains(p)) return new HitInfo { Kind = HitKind.CloseButton, Row = -1, Window = -1 };
            if (DetailedRect(winWidth, winHeight).Contains(p)) return new HitInfo { Kind = HitKind.DetailedChip, Row = -1, Window = -1 };
            if (CompactRect(winWidth, winHeight).Contains(p)) return new HitInfo { Kind = HitKind.CompactChip, Row = -1, Window = -1 };

            var body = new RectangleF(0, HeaderHeight, winWidth, winHeight - HeaderHeight - FooterHeight);
            if (!body.Contains(p)) return new HitInfo();
            int clamped = ClampedScroll(winWidth, winHeight, mode, scroll, rows);
            float top = HeaderHeight - clamped;
            if (mode == 0) return HitTestDetailed(p, winWidth, top, rows);
            return HitTestCompact(p, winWidth, top, rows);
        }

        HitInfo HitTestDetailed(Point p, float width, float top, List<RowVm> rows)
        {
            float y = top + DetailedBodyPad();
            for (int i = 0; i < rows.Count; i++)
            {
                RowVm r = rows[i];
                RectangleF card = DetailedCardRect(r, y, width);
                if (card.Contains(p))
                {
                    if (DetailedTitleBox(card).Contains(p))
                        return new HitInfo { Kind = HitKind.RowTitle, Row = i, Window = -1 };
                    for (int j = 0; j < r.Windows.Count; j++)
                        if (DetailedLineBox(card, j).Contains(p))
                            return new HitInfo { Kind = HitKind.UsageWindow, Row = i, Window = j };
                    return new HitInfo();   // card padding
                }
                y += RowHeight(0, r);
            }
            return new HitInfo();
        }

        HitInfo HitTestCompact(Point p, float width, float top, List<RowVm> rows)
        {
            List<WindowKind> kinds = CompactColumns(rows);
            float cols = CompactColsLeft(width, rows);
            float y = top + CompactLabelBand();
            for (int i = 0; i < rows.Count; i++)
            {
                RowVm r = rows[i];
                RectangleF titleBox = kinds.Count == 0
                    ? CompactNoColumnTitle(width, y)
                    : CompactTitleBox(cols, y);
                if (titleBox.Contains(p))
                    return new HitInfo { Kind = HitKind.RowTitle, Row = i, Window = -1 };
                for (int j = 0; j < kinds.Count; j++)
                {
                    if (r.Windows.Count == 0) break;   // message rows own the whole column band
                    RectangleF cell = CompactCellBox(cols, j, y);
                    if (!cell.Contains(p)) continue;
                    WindowVm wnd = WindowInColumn(r.Windows, kinds[j]);
                    if (wnd == null) return new HitInfo();   // the "—" placeholder
                    return new HitInfo { Kind = HitKind.UsageWindow, Row = i,
                        Window = r.Windows.IndexOf(wnd) };
                }
                y += CompactRowPitch();
            }
            return new HitInfo();
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void FillRound(Graphics g, Color color, RectangleF r, float rad)
        {
            using (var b = new SolidBrush(color))
            using (var p = Round(r, rad))
                g.FillPath(b, p);
        }

        void Txt(Graphics g, string text, Font f, Color color, RectangleF r, bool rightAlign)
        {
            using (var b = new SolidBrush(color))
                g.DrawString(text, f, b, r, rightAlign ? far : near);
        }

        public void Dispose()
        {
            fName.Dispose();
            fPlan.Dispose();
            fSmall.Dispose();
            fPct.Dispose();
            near.Dispose();
            far.Dispose();
        }
    }
}
