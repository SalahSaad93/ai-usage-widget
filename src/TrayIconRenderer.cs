using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    // The live tray icon and its tooltip text, both derived from the shown rows. The icon
    // is drawn in code and rebuilt per refresh, so its handles are short-lived: the
    // bitmap's HICON is cloned into an owning Icon and DestroyIcon frees the original at
    // once (no GDI handle leak). The worst-window pick and the tooltip text are pure and
    // self-tested.
    public static class TrayIconRenderer
    {
        const int MaxTooltipLength = 63;   // the NotifyIcon hard limit
        const string EntrySeparator = " · ";

        // The rounded square the fill is drawn on: the widget's dark background.
        static readonly Color BackColor = Color.FromArgb(30, 31, 34);   // Theme.Dark.Background

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        // One tooltip entry, e.g. "Codex week 92%".
        public sealed class TrayWindow
        {
            public string Text;
            public double Percent;
        }

        // The primary windows of the shown Ok/Stale rows, worst first.
        public static List<TrayWindow> WorstWindows(List<RowVm> rows)
        {
            var windows = new List<TrayWindow>();
            if (rows == null) return windows;
            foreach (RowVm r in rows)
            {
                if (r == null) continue;
                if (r.Status != ProviderStatus.Ok && r.Status != ProviderStatus.Stale) continue;
                foreach (WindowVm w in r.Windows)
                {
                    if (w == null || !w.Primary) continue;
                    string label = string.IsNullOrEmpty(w.Label)
                        ? Painter.DefaultLabel(w.Kind) : w.Label;
                    var entry = new TrayWindow();
                    entry.Text = (r.Name ?? "") + " " + label + " "
                        + Math.Round(w.UsedPercent).ToString("0", CultureInfo.InvariantCulture) + "%";
                    entry.Percent = w.UsedPercent;
                    windows.Add(entry);
                }
            }
            windows.Sort(delegate(TrayWindow a, TrayWindow b) { return b.Percent.CompareTo(a.Percent); });
            return windows;
        }

        // The worst percent, for the icon's fill; null when there is no data.
        public static double? WorstPercent(List<RowVm> rows)
        {
            List<TrayWindow> windows = WorstWindows(rows);
            if (windows.Count == 0) return null;
            return windows[0].Percent;
        }

        // Worst windows first, whole entries dropped when they would not fit; with no data
        // (or nothing that fits) the fallback text is used, clamped to the limit.
        public static string TooltipText(List<RowVm> rows, string fallback)
        {
            string text = "";
            foreach (TrayWindow w in WorstWindows(rows))
            {
                string candidate = text.Length == 0 ? w.Text : text + EntrySeparator + w.Text;
                if (candidate.Length > MaxTooltipLength) continue;
                text = candidate;
            }
            if (text.Length == 0)
            {
                text = string.IsNullOrEmpty(fallback) ? "AI Usage Widget" : fallback;
                if (text.Length > MaxTooltipLength) text = text.Substring(0, MaxTooltipLength);
            }
            return text;
        }

        // A rounded dark square with a vertical fill proportional to percent (0..100
        // clamped), in the given colour. No data -> null: the caller keeps the app icon.
        public static Icon Render(double? worstPercent, Color fill, int size)
        {
            if (!worstPercent.HasValue || size < 1) return null;
            double percent = Math.Min(100.0, Math.Max(0.0, worstPercent.Value));
            using (var bitmap = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float pad = Math.Max(1f, size / 16f);
                var square = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                using (var b = new SolidBrush(BackColor))
                using (GraphicsPath path = Round(square, square.Width / 4f))
                    g.FillPath(b, path);

                float inset = square.Width / 5f;
                var area = new RectangleF(square.X + inset, square.Y + inset,
                    square.Width - 2 * inset, square.Height - 2 * inset);
                float fillHeight = area.Height * (float)(percent / 100.0);
                if (fillHeight > 0)
                {
                    if (fillHeight < 1f) fillHeight = 1f;
                    var bar = new RectangleF(area.X, area.Bottom - fillHeight, area.Width, fillHeight);
                    using (var b = new SolidBrush(fill))
                        g.FillRectangle(b, bar);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
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
    }
}
