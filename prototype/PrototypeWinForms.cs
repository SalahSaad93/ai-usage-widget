// PROTOTYPE - throwaway. Fake data only: no network, no token reading, no updater.
// Question it answers: does a custom-drawn WinForms window give the approved look under 50 MB?
// Layouts: Detailed (default) and Compact. Switch with the footer chips, keys 1 / 2, or the tray menu.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

enum St { Ok, Stale, Auth, NotConfigured }

sealed class Row
{
    public string Name, Plan, Msg;
    public double? H5, Wk, Mo;
    public St Status;
    public Row(string name, string plan, double? h5, double? wk, double? mo, St status, string msg)
    {
        Name = name; Plan = plan; H5 = h5; Wk = wk; Mo = mo; Status = status; Msg = msg;
    }
}

sealed class Proto : Form
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    static readonly Color Bg = ColorTranslator.FromHtml("#1E1F22"), Card = ColorTranslator.FromHtml("#26282C"),
        Line = ColorTranslator.FromHtml("#3A3D44"), Fg = ColorTranslator.FromHtml("#E6E6E6"),
        Dim = ColorTranslator.FromHtml("#8B8F98"), Green = ColorTranslator.FromHtml("#3FB950"),
        Yellow = ColorTranslator.FromHtml("#D29922"), Red = ColorTranslator.FromHtml("#F85149"),
        Accent = ColorTranslator.FromHtml("#4C8DFF");

    static readonly Row[] Rows = new Row[]
    {
        new Row("Claude Code", "Max 5x", 32, 58, null, St.Ok, null),
        new Row("Codex", "Plus", 15, 41, null, St.Ok, null),
        new Row("Z.ai GLM", "Coding Pro", 76, 64, 48, St.Ok, null),
        new Row("OpenCode Go", "Go", null, 22, 71, St.Ok, null),
        new Row("Muse", "Pro", 88, 52, null, St.Stale, "stale 42m"),
        new Row("Cursor", "Pro", null, null, null, St.Auth, "sign in needed: run cursor-agent login"),
        new Row("Grok Build", "", null, null, null, St.NotConfigured, "not configured"),
    };
    static readonly string[] Labels = { "5 hours", "week", "month" };
    static readonly string[] Resets = { "2h 10m", "3d 4h", "19d" };

    readonly float s;
    readonly Font fName, fPlan, fSmall, fPct;
    readonly StringFormat near = new StringFormat(StringFormat.GenericTypographic);
    readonly StringFormat far = new StringFormat(StringFormat.GenericTypographic);
    readonly List<KeyValuePair<RectangleF, Action>> hits = new List<KeyValuePair<RectangleF, Action>>();
    readonly NotifyIcon tray = new NotifyIcon();
    int mode;            // 0 = Detailed, 1 = Compact
    int scroll;
    float contentH;
    string updated = "updated 2 min ago";

    float S(float v) { return v * s; }
    static Color ColorFor(double v) { return v < 50 ? Green : (v <= 70 ? Yellow : Red); }
    static Color Fade(Color c, bool dim) { return dim ? Color.FromArgb(115, c) : c; }
    static string Pct(double v) { return Math.Round(v).ToString() + "%"; }

    public Proto()
    {
        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        fName = new Font("Segoe UI Semibold", S(13), GraphicsUnit.Pixel);
        fPlan = new Font("Segoe UI", S(11), GraphicsUnit.Pixel);
        fSmall = new Font("Segoe UI", S(10.5f), GraphicsUnit.Pixel);
        fPct = new Font("Segoe UI", S(11.5f), GraphicsUnit.Pixel);
        near.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        near.LineAlignment = StringAlignment.Center;
        near.Trimming = StringTrimming.EllipsisCharacter;
        far.FormatFlags |= StringFormatFlags.NoWrap;
        far.LineAlignment = StringAlignment.Center;
        far.Alignment = StringAlignment.Far;

        Text = "AI Usage (prototype)";
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        ResizeRedraw = true;
        KeyPreview = true;
        MinimumSize = new Size((int)S(300), (int)S(120));

        tray.Icon = SystemIcons.Application;
        tray.Text = "AI Usage (prototype) - update available: v0.2.0";
        tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Layout: Detailed", null, delegate { SetMode(0); });
        menu.Items.Add("Layout: Compact", null, delegate { SetMode(1); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, delegate { updated = "updated just now"; Invalidate(); });
        menu.Items.Add("Toggle always on top", null, delegate { TopMost = !TopMost; });
        menu.Items.Add("Update available: v0.2.0 (fake)", null, delegate { });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { Show(); Activate(); };
        FormClosed += delegate { tray.Visible = false; tray.Dispose(); };

        SetMode(0);
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - (int)S(16), wa.Bottom - Height - (int)S(16));
    }

    float HeaderH { get { return S(30); } }
    float FooterH { get { return S(30); } }

    float RowHeight(Row r)
    {
        if (mode == 1) return S(24);
        int lines = 0;
        if (r.Status == St.Auth || r.Status == St.NotConfigured) lines = 1;
        else { if (r.H5.HasValue) lines++; if (r.Wk.HasValue) lines++; if (r.Mo.HasValue) lines++; }
        return S(8 + 18 + lines * 19 + 7 + 6);   // padding + title + lines + padding + gap
    }

    float MeasureContent()
    {
        float h = mode == 1 ? S(20) : S(2);
        foreach (var r in Rows) h += RowHeight(r);
        return h + S(2);
    }

    void SetMode(int m)
    {
        mode = m;
        scroll = 0;
        contentH = MeasureContent();
        ClientSize = new Size((int)S(m == 0 ? 340 : 420), (int)(HeaderH + contentH + FooterH));
        Invalidate();
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

    static void FillRound(Graphics g, Color c, RectangleF r, float rad)
    {
        using (var b = new SolidBrush(c))
        using (var p = Round(r, rad)) g.FillPath(b, p);
    }

    void Txt(Graphics g, string t, Font f, Color c, RectangleF r, bool right)
    {
        using (var b = new SolidBrush(c)) g.DrawString(t, f, b, r, right ? far : near);
    }

    void Bar(Graphics g, double v, RectangleF r, bool dim)
    {
        FillRound(g, Line, r, r.Height / 2);
        float w = Math.Max(r.Height, r.Width * (float)(Math.Min(100, Math.Max(0, v)) / 100));
        FillRound(g, Fade(ColorFor(v), dim), new RectangleF(r.X, r.Y, w, r.Height), r.Height / 2);
    }

    // name, plan and the stale tag on one line; returns nothing, draws within r
    void Title(Graphics g, Row r, RectangleF box)
    {
        float x = box.X;
        Txt(g, r.Name, fName, r.Status == St.NotConfigured ? Dim : Fg, box, false);
        x += g.MeasureString(r.Name, fName, 1000, near).Width + S(6);
        if (!string.IsNullOrEmpty(r.Plan) && x < box.Right)
        {
            Txt(g, r.Plan, fPlan, Dim, new RectangleF(x, box.Y + S(1), box.Right - x, box.Height), false);
            x += g.MeasureString(r.Plan, fPlan, 1000, near).Width + S(6);
        }
        if (r.Status == St.Stale && x < box.Right)
            Txt(g, r.Msg, fSmall, Yellow, new RectangleF(x, box.Y + S(1), box.Right - x, box.Height), false);
    }

    void DrawDetailed(Graphics g, float y, float width)
    {
        y += S(2);
        foreach (var r in Rows)
        {
            float h = RowHeight(r) - S(6);
            var card = new RectangleF(S(8), y, width - S(16), h);
            FillRound(g, Card, card, S(6));
            float x0 = card.X + S(10), x1 = card.Right - S(10), ly = y + S(8);
            Title(g, r, new RectangleF(x0, ly, x1 - x0, S(18)));
            ly += S(18);
            if (r.Status == St.Auth || r.Status == St.NotConfigured)
            {
                Txt(g, r.Msg, fSmall, r.Status == St.Auth ? Yellow : Dim, new RectangleF(x0, ly, x1 - x0, S(19)), false);
            }
            else
            {
                bool dim = r.Status == St.Stale;
                double?[] vals = { r.H5, r.Wk, r.Mo };
                for (int i = 0; i < 3; i++)
                {
                    if (!vals[i].HasValue) continue;
                    double v = vals[i].Value;
                    Txt(g, Labels[i], fSmall, Fade(Dim, dim), new RectangleF(x0, ly, S(48), S(19)), false);
                    float bx = x0 + S(48), bw = x1 - S(84) - S(40) - bx;
                    if (bw > S(10)) Bar(g, v, new RectangleF(bx, ly + S(5.5f), bw, S(8)), dim);
                    Txt(g, Pct(v), fPct, Fade(ColorFor(v), dim), new RectangleF(x1 - S(84) - S(40), ly, S(40), S(19)), true);
                    Txt(g, "resets " + Resets[i], fSmall, Fade(Dim, dim), new RectangleF(x1 - S(84), ly, S(84), S(19)), true);
                    ly += S(19);
                }
            }
            y += h + S(6);
        }
    }

    void DrawCompact(Graphics g, float y, float width)
    {
        float x0 = S(10), x1 = width - S(10), col = S(82), cols = x1 - col * 3;
        for (int i = 0; i < 3; i++)
            Txt(g, Labels[i], fSmall, Dim, new RectangleF(cols + col * i + S(8), y, col, S(20)), false);
        y += S(20);
        foreach (var r in Rows)
        {
            Title(g, r, new RectangleF(x0, y, cols - x0, S(24)));
            if (r.Status == St.Auth || r.Status == St.NotConfigured)
            {
                Txt(g, r.Msg, fSmall, r.Status == St.Auth ? Yellow : Dim, new RectangleF(cols + S(8), y, col * 3 - S(8), S(24)), false);
            }
            else
            {
                bool dim = r.Status == St.Stale;
                double?[] vals = { r.H5, r.Wk, r.Mo };
                for (int i = 0; i < 3; i++)
                {
                    float cx = cols + col * i + S(8), cw = col - S(8);
                    if (!vals[i].HasValue) { Txt(g, "—", fSmall, Dim, new RectangleF(cx, y, cw, S(24)), true); continue; }
                    double v = vals[i].Value;
                    Bar(g, v, new RectangleF(cx, y + S(9.5f), cw - S(34), S(5)), dim);
                    Txt(g, Pct(v), fSmall, Fade(ColorFor(v), dim), new RectangleF(cx + cw - S(34), y, S(34), S(24)), true);
                }
            }
            y += S(24);
        }
    }

    void Chip(Graphics g, string text, RectangleF r, bool active, Action onClick)
    {
        FillRound(g, active ? Accent : Card, r, S(4));
        using (var b = new SolidBrush(active ? Color.White : Dim))
        using (var c = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(text, fPlan, b, r, c);
        hits.Add(new KeyValuePair<RectangleF, Action>(r, onClick));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        hits.Clear();
        float w = ClientSize.Width, h = ClientSize.Height;

        // body (clipped, scrollable with the mouse wheel when the window is shorter than the content)
        var bodyRect = new RectangleF(0, HeaderH, w, h - HeaderH - FooterH);
        contentH = MeasureContent();
        scroll = (int)Math.Max(0, Math.Min(scroll, contentH - bodyRect.Height));
        g.SetClip(bodyRect);
        if (mode == 0) DrawDetailed(g, HeaderH - scroll, w); else DrawCompact(g, HeaderH - scroll, w);
        g.ResetClip();

        // header
        Txt(g, "AI Usage", fName, Fg, new RectangleF(S(10), 0, S(70), HeaderH), false);
        float tx = S(10) + g.MeasureString("AI Usage", fName, 1000, near).Width + S(8);
        Txt(g, updated, fSmall, Dim, new RectangleF(tx, S(1), w - tx - S(60), HeaderH), false);
        Chip(g, "⟳", new RectangleF(w - S(54), S(5), S(22), S(20)), false, delegate { updated = "updated just now"; Invalidate(); });
        Chip(g, "✕", new RectangleF(w - S(28), S(5), S(22), S(20)), false, delegate { Close(); });

        // footer
        float fy = h - FooterH;
        Txt(g, "PROTOTYPE · fake data", fSmall, Dim, new RectangleF(S(10), fy, w - S(160), FooterH), false);
        Chip(g, "Detailed", new RectangleF(w - S(132), fy + S(4), S(60), S(20)), mode == 0, delegate { SetMode(0); });
        Chip(g, "Compact", new RectangleF(w - S(68), fy + S(4), S(60), S(20)), mode == 1, delegate { SetMode(1); });

        using (var pen = new Pen(Line)) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        foreach (var hit in hits.ToArray())
            if (hit.Key.Contains(e.Location)) { hit.Value(); return; }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll -= e.Delta / 4;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.D1) SetMode(0);
        else if (e.KeyCode == Keys.D2) SetMode(1);
        else if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDBLCLK = 0xA3;
        if (m.Msg == WM_NCLBUTTONDBLCLK) return;          // no maximise on double-click
        if (m.Msg == WM_NCHITTEST)
        {
            long lp = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int grip = (int)S(6), w = ClientSize.Width, h = ClientSize.Height;
            bool l = p.X < grip, r = p.X >= w - grip, t = p.Y < grip, b = p.Y >= h - grip;
            int code;
            if (t && l) code = 13; else if (t && r) code = 14; else if (b && l) code = 16; else if (b && r) code = 17;
            else if (l) code = 10; else if (r) code = 11; else if (t) code = 12; else if (b) code = 15;
            else
            {
                code = 2;                                  // drag anywhere
                foreach (var hit in hits) if (hit.Key.Contains(p)) { code = 1; break; }
            }
            m.Result = (IntPtr)code;
            return;
        }
        base.WndProc(ref m);
    }

    [STAThread]
    static void Main()
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.Run(new Proto());
    }
}
