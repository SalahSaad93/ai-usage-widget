using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    // What the painter draws; UsageSnapshot stays free of UI concerns.
    public sealed class WindowVm
    {
        public WindowKind Kind;
        public double UsedPercent;
        public string Resets;   // "2h 10m", or null when the vendor gave no reset time
    }

    public sealed class RowVm
    {
        public string Name;
        public string Plan;
        public ProviderStatus Status;
        public string Msg;      // auth / not-configured / error text, or the stale tag
        public List<WindowVm> Windows;

        public RowVm()
        {
            Windows = new List<WindowVm>();
        }
    }

    // All drawing. Reproduces prototype/PrototypeWinForms.cs (the approved look), minus the
    // PROTOTYPE footer and layout chips; layout is chosen from the tray menu.
    public sealed class Painter : IDisposable
    {
        static readonly Color Bg = FromHtml("#1E1F22");
        static readonly Color Card = FromHtml("#26282C");
        static readonly Color Line = FromHtml("#3A3D44");
        static readonly Color Fg = FromHtml("#E6E6E6");
        static readonly Color Dim = FromHtml("#8B8F98");
        static readonly Color Green = FromHtml("#3FB950");
        static readonly Color Yellow = FromHtml("#D29922");
        static readonly Color Red = FromHtml("#F85149");

        static readonly string[] WindowLabels = { "5 hours", "week", "month" };

        readonly float s;
        readonly Font fName, fPlan, fSmall, fPct;
        readonly StringFormat near, far;

        public Painter(float dpiScale)
        {
            s = dpiScale;
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

        public static Color Background { get { return Bg; } }

        public float S(float v)
        {
            return v * s;
        }

        static Color FromHtml(string hex)
        {
            return ColorTranslator.FromHtml(hex);
        }

        // One colour rule for everything: < 50 green, 50..70 yellow (inclusive), > 70 red.
        public static Color ColorFor(double percent)
        {
            return percent < 50 ? Green : (percent <= 70 ? Yellow : Red);
        }

        static Color Fade(Color c, bool dim)
        {
            return dim ? Color.FromArgb(115, c) : c;
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

        public static List<RowVm> BuildRows(List<UsageSnapshot> snapshots, DateTime nowUtc,
            ICollection<string> visibleIds)
        {
            var rows = new List<RowVm>();
            if (snapshots == null) return rows;
            foreach (UsageSnapshot sn in snapshots)
            {
                if (visibleIds != null && !visibleIds.Contains(sn.ProviderId)) continue;
                var row = new RowVm();
                row.Name = string.IsNullOrEmpty(sn.DisplayName) ? sn.ProviderId : sn.DisplayName;
                row.Plan = sn.PlanName;
                row.Status = sn.Status;
                if (sn.Status == ProviderStatus.Stale)
                    row.Msg = "stale " + Duration(nowUtc - sn.FetchedAtUtc);
                else if (sn.Status != ProviderStatus.Ok)
                    row.Msg = string.IsNullOrEmpty(sn.Message) ? sn.Status.ToString() : sn.Message;
                else if (sn.Windows.Count == 0)
                    row.Msg = string.IsNullOrEmpty(sn.Message) ? "no usage reported" : sn.Message;
                foreach (UsageWindow w in sn.Windows)
                {
                    var vm = new WindowVm();
                    vm.Kind = w.Kind;
                    vm.UsedPercent = w.UsedPercent;
                    if (w.ResetsAtUtc.HasValue)
                    {
                        TimeSpan until = w.ResetsAtUtc.Value - nowUtc;
                        vm.Resets = until.TotalSeconds <= 0 ? "now" : Duration(until);
                    }
                    row.Windows.Add(vm);
                }
                // five-hour above weekly above monthly, regardless of vendor field order
                row.Windows.Sort(delegate(WindowVm a, WindowVm b)
                {
                    return ColumnOf(a.Kind).CompareTo(ColumnOf(b.Kind));
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

        public RectangleF RefreshRect(float w)
        {
            return new RectangleF(w - S(54), S(5), S(22), S(20));
        }

        public RectangleF CloseRect(float w)
        {
            return new RectangleF(w - S(28), S(5), S(22), S(20));
        }

        public void Draw(Graphics g, float winWidth, float winHeight, int mode, int scroll,
            List<RowVm> rows, string headerNote)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float header = HeaderHeight;
            var body = new RectangleF(0, header, winWidth, winHeight - header);
            float contentH = ContentHeight(mode, rows);
            int clamped = (int)Math.Max(0, Math.Min(scroll, contentH - body.Height));
            g.SetClip(body);
            if (mode == 0) DrawDetailed(g, header - clamped, winWidth, rows);
            else DrawCompact(g, header - clamped, winWidth, rows);
            g.ResetClip();

            DrawHeader(g, winWidth, headerNote);
            using (var pen = new Pen(Line)) g.DrawRectangle(pen, 0, 0, winWidth - 1, winHeight - 1);
        }

        void DrawHeader(Graphics g, float w, string headerNote)
        {
            float header = HeaderHeight;
            Txt(g, "AI Usage", fName, Fg, new RectangleF(S(10), 0, S(70), header), false);
            float tx = S(10) + g.MeasureString("AI Usage", fName, 1000, near).Width + S(8);
            Txt(g, headerNote, fSmall, Dim, new RectangleF(tx, S(1), w - tx - S(60), header), false);
            Chip(g, "⟳", RefreshRect(w));
            Chip(g, "✕", CloseRect(w));
        }

        void DrawDetailed(Graphics g, float y, float width, List<RowVm> rows)
        {
            y += S(2);
            foreach (RowVm r in rows)
            {
                float h = RowHeight(0, r) - S(6);
                var card = new RectangleF(S(8), y, width - S(16), h);
                FillRound(g, Card, card, S(6));
                float x0 = card.X + S(10), x1 = card.Right - S(10), ly = y + S(8);
                Title(g, r, new RectangleF(x0, ly, x1 - x0, S(18)));
                ly += S(18);
                bool dim = r.Status == ProviderStatus.Stale;
                if (r.Windows.Count > 0)
                {
                    foreach (WindowVm wnd in r.Windows)
                    {
                        Txt(g, Label(ColumnOf(wnd.Kind)), fSmall, Fade(Dim, dim),
                            new RectangleF(x0, ly, S(48), S(19)), false);
                        float bx = x0 + S(48), bw = x1 - S(84) - S(40) - bx;
                        if (bw > S(10))
                            Bar(g, wnd.UsedPercent, new RectangleF(bx, ly + S(5.5f), bw, S(8)), dim);
                        Txt(g, PercentText(wnd.UsedPercent), fPct, Fade(ColorFor(wnd.UsedPercent), dim),
                            new RectangleF(x1 - S(84) - S(40), ly, S(40), S(19)), true);
                        if (wnd.Resets != null)
                            Txt(g, "resets " + wnd.Resets, fSmall, Fade(Dim, dim),
                                new RectangleF(x1 - S(84), ly, S(84), S(19)), true);
                        ly += S(19);
                    }
                }
                else
                {
                    Color c = r.Status == ProviderStatus.AuthRequired ? Yellow : Dim;
                    Txt(g, r.Msg, fSmall, c, new RectangleF(x0, ly, x1 - x0, S(19)), false);
                }
                y += h + S(6);
            }
        }

        void DrawCompact(Graphics g, float y, float width, List<RowVm> rows)
        {
            float x0 = S(10), x1 = width - S(10), col = S(82), cols = x1 - col * 3;
            for (int i = 0; i < 3; i++)
                Txt(g, Label(i), fSmall, Dim, new RectangleF(cols + col * i + S(8), y, col, S(20)), false);
            y += S(20);
            foreach (RowVm r in rows)
            {
                Title(g, r, new RectangleF(x0, y, cols - x0, S(24)));
                bool dim = r.Status == ProviderStatus.Stale;
                if (r.Windows.Count > 0)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float cx = cols + col * i + S(8), cw = col - S(8);
                        WindowVm wnd = WindowInColumn(r.Windows, i);
                        if (wnd == null)
                        {
                            Txt(g, "—", fSmall, Fade(Dim, dim), new RectangleF(cx, y, cw, S(24)), true);
                            continue;
                        }
                        Bar(g, wnd.UsedPercent, new RectangleF(cx, y + S(9.5f), cw - S(34), S(5)), dim);
                        Txt(g, PercentText(wnd.UsedPercent), fSmall, Fade(ColorFor(wnd.UsedPercent), dim),
                            new RectangleF(cx + cw - S(34), y, S(34), S(24)), true);
                    }
                }
                else
                {
                    Color c = r.Status == ProviderStatus.AuthRequired ? Yellow : Dim;
                    Txt(g, r.Msg, fSmall, c, new RectangleF(cols + S(8), y, col * 3 - S(8), S(24)), false);
                }
                y += S(24);
            }
        }

        void Title(Graphics g, RowVm r, RectangleF box)
        {
            float x = box.X;
            Txt(g, r.Name, fName, r.Status == ProviderStatus.NotConfigured ? Dim : Fg, box, false);
            x += g.MeasureString(r.Name, fName, 1000, near).Width + S(6);
            if (!string.IsNullOrEmpty(r.Plan) && x < box.Right)
            {
                Txt(g, r.Plan, fPlan, Dim, new RectangleF(x, box.Y + S(1), box.Right - x, box.Height), false);
                x += g.MeasureString(r.Plan, fPlan, 1000, near).Width + S(6);
            }
            if (r.Status == ProviderStatus.Stale && x < box.Right)
                Txt(g, r.Msg, fSmall, Yellow, new RectangleF(x, box.Y + S(1), box.Right - x, box.Height), false);
        }

        void Chip(Graphics g, string glyph, RectangleF r)
        {
            FillRound(g, Card, r, S(4));
            using (var b = new SolidBrush(Dim))
            using (var c = new StringFormat())
            {
                c.Alignment = StringAlignment.Center;
                c.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, fPlan, b, r, c);
            }
        }

        void Bar(Graphics g, double v, RectangleF r, bool dim)
        {
            FillRound(g, Line, r, r.Height / 2);
            float w = Math.Max(r.Height, r.Width * (float)(Math.Min(100, Math.Max(0, v)) / 100));
            FillRound(g, Fade(ColorFor(v), dim), new RectangleF(r.X, r.Y, w, r.Height), r.Height / 2);
        }

        static string Label(int column)
        {
            return WindowLabels[column];
        }

        static int ColumnOf(WindowKind kind)
        {
            if (kind == WindowKind.FiveHour) return 0;
            if (kind == WindowKind.Weekly) return 1;
            return 2;
        }

        static WindowVm WindowInColumn(List<WindowVm> windows, int column)
        {
            foreach (WindowVm w in windows)
                if (ColumnOf(w.Kind) == column) return w;
            return null;
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
