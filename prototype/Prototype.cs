// PROTOTYPE - throwaway. Fake data only: no network, no token reading, no updater.
// Question it answers: what should the AI usage widget look like on the desktop?
// Switch layouts with the A / B / C chips in the footer, keys 1 / 2 / 3, or the tray menu.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using WF = System.Windows.Forms;

enum St { Ok, Stale, Auth, NotConfigured }

sealed class Row
{
    public string Name, Plan, Msg;
    public double? H5, Wk, Mo;
    public string R5, RWk, RMo;
    public St Status;
    public Row(string name, string plan, double? h5, double? wk, double? mo, St status, string msg)
    {
        Name = name; Plan = plan; H5 = h5; Wk = wk; Mo = mo; Status = status; Msg = msg;
        R5 = "2h 10m"; RWk = "3d 4h"; RMo = "19d";
    }
}

sealed class Proto : Window
{
    static Brush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }

    static readonly Brush Bg = B("#1E1F22"), Card = B("#26282C"), Line = B("#3A3D44"), Track = B("#3A3D44"),
        Fg = B("#E6E6E6"), Dim = B("#8B8F98"), Green = B("#3FB950"), Yellow = B("#D29922"), Red = B("#F85149"),
        Accent = B("#4C8DFF");

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

    readonly StackPanel body = new StackPanel();
    readonly StackPanel chips = new StackPanel { Orientation = Orientation.Horizontal };
    readonly TextBlock updated;
    readonly WF.NotifyIcon tray = new WF.NotifyIcon();
    int variant;

    static Brush ColorFor(double v) { return v < 50 ? Green : (v <= 70 ? Yellow : Red); }

    static TextBlock T(string text, double size, Brush brush)
    {
        return new TextBlock { Text = text, FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
    }

    static Grid Bar(double v, double h)
    {
        var g = new Grid { Height = h, VerticalAlignment = VerticalAlignment.Center };
        double f = Math.Max(0.0001, Math.Min(100, v));
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 100 - f), GridUnitType.Star) });
        var track = new Border { Background = Track, CornerRadius = new CornerRadius(h / 2) };
        Grid.SetColumnSpan(track, 2);
        g.Children.Add(track);
        g.Children.Add(new Border { Background = ColorFor(v), CornerRadius = new CornerRadius(h / 2) });
        return g;
    }

    static string Pct(double v) { return Math.Round(v).ToString() + "%"; }

    static Brush MsgBrush(Row r) { return r.Status == St.Auth ? Yellow : Dim; }

    static StackPanel NameBlock(Row r)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var n = T(r.Name, 12.5, r.Status == St.NotConfigured ? Dim : Fg);
        n.FontWeight = FontWeights.SemiBold;
        sp.Children.Add(n);
        if (!string.IsNullOrEmpty(r.Plan))
        {
            var p = T(r.Plan, 10.5, Dim);
            p.Margin = new Thickness(6, 1, 0, 0);
            sp.Children.Add(p);
        }
        if (r.Status == St.Stale)
        {
            var s = T(r.Msg, 10, Yellow);
            s.Margin = new Thickness(6, 1, 0, 0);
            sp.Children.Add(s);
        }
        return sp;
    }

    // ---- Variant A: compact table with bars -------------------------------------------------
    static UIElement CellA(double? v, bool dim)
    {
        var g = new Grid { Margin = new Thickness(8, 0, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        TextBlock t;
        if (!v.HasValue)
        {
            t = T("—", 11, Dim);
        }
        else
        {
            g.Children.Add(Bar(v.Value, 5));
            t = T(Pct(v.Value), 11, ColorFor(v.Value));
        }
        t.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        if (dim) g.Opacity = 0.45;
        return g;
    }

    static Grid GridA()
    {
        var g = new Grid { Margin = new Thickness(10, 4, 10, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        return g;
    }

    void BuildA()
    {
        var head = GridA();
        string[] labels = { "5 hours", "week", "month" };
        for (int i = 0; i < 3; i++)
        {
            var t = T(labels[i], 10, Dim);
            t.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(t, i + 1);
            head.Children.Add(t);
        }
        body.Children.Add(head);
        foreach (var r in Rows)
        {
            var g = GridA();
            g.Children.Add(NameBlock(r));
            if (r.Status == St.Auth || r.Status == St.NotConfigured)
            {
                var m = T(r.Msg, 10.5, MsgBrush(r));
                m.Margin = new Thickness(8, 0, 0, 0);
                m.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(m, 1);
                Grid.SetColumnSpan(m, 3);
                g.Children.Add(m);
            }
            else
            {
                double?[] vals = { r.H5, r.Wk, r.Mo };
                for (int i = 0; i < 3; i++)
                {
                    var c = CellA(vals[i], r.Status == St.Stale);
                    Grid.SetColumn((UIElement)c, i + 1);
                    g.Children.Add(c);
                }
            }
            body.Children.Add(g);
        }
    }

    // ---- Variant B: one line per tool, coloured dots ----------------------------------------
    static void DotB(StackPanel sp, string label, double? v, bool dim)
    {
        if (!v.HasValue) return;
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        item.Children.Add(T(label, 10, Dim));
        item.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = ColorFor(v.Value), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        var p = T(Pct(v.Value), 11.5, Fg);
        p.MinWidth = 28;
        item.Children.Add(p);
        if (dim) item.Opacity = 0.45;
        sp.Children.Add(item);
    }

    void BuildB()
    {
        foreach (var r in Rows)
        {
            var d = new DockPanel { Margin = new Thickness(10, 5, 10, 5), LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            if (r.Status == St.Auth || r.Status == St.NotConfigured)
            {
                right.Children.Add(T(r.Msg, 10.5, MsgBrush(r)));
            }
            else
            {
                DotB(right, "5h", r.H5, r.Status == St.Stale);
                DotB(right, "wk", r.Wk, r.Status == St.Stale);
                DotB(right, "mo", r.Mo, r.Status == St.Stale);
            }
            d.Children.Add(right);
            d.Children.Add(NameBlock(r));
            body.Children.Add(d);
        }
    }

    // ---- Variant C: roomy cards with reset times -------------------------------------------
    static void LineC(StackPanel card, string label, double? v, string reset, bool dim)
    {
        if (!v.HasValue) return;
        var g = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.Children.Add(T(label, 10.5, Dim));
        var bar = Bar(v.Value, 8);
        Grid.SetColumn(bar, 1);
        g.Children.Add(bar);
        var p = T(Pct(v.Value), 11.5, ColorFor(v.Value));
        p.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(p, 2);
        g.Children.Add(p);
        var rs = T("resets " + reset, 10, Dim);
        rs.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(rs, 3);
        g.Children.Add(rs);
        if (dim) g.Opacity = 0.45;
        card.Children.Add(g);
    }

    void BuildC()
    {
        foreach (var r in Rows)
        {
            var sp = new StackPanel();
            sp.Children.Add(NameBlock(r));
            if (r.Status == St.Auth || r.Status == St.NotConfigured)
            {
                var m = T(r.Msg, 10.5, MsgBrush(r));
                m.Margin = new Thickness(0, 4, 0, 0);
                sp.Children.Add(m);
            }
            else
            {
                LineC(sp, "5 hours", r.H5, r.R5, r.Status == St.Stale);
                LineC(sp, "week", r.Wk, r.RWk, r.Status == St.Stale);
                LineC(sp, "month", r.Mo, r.RMo, r.Status == St.Stale);
            }
            body.Children.Add(new Border
            {
                Background = Card, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 9),
                Margin = new Thickness(8, 3, 8, 3), Child = sp
            });
        }
    }

    // ---- shell ------------------------------------------------------------------------------
    Border Chip(string text, bool active, Action onClick)
    {
        var t = T(text, 11, active ? Brushes.White : Dim);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        var b = new Border
        {
            Child = t, MinWidth = 22, Height = 20, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(4), Background = active ? Accent : Card, Cursor = Cursors.Hand
        };
        b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; onClick(); };
        return b;
    }

    void Show(int v)
    {
        variant = v;
        body.Children.Clear();
        if (v == 0) BuildA(); else if (v == 1) BuildB(); else BuildC();
        chips.Children.Clear();
        string[] names = { "A", "B", "C" };
        for (int i = 0; i < 3; i++)
        {
            int n = i;
            chips.Children.Add(Chip(names[i], i == v, delegate { Show(n); }));
        }
        Width = v == 0 ? 420 : (v == 1 ? 380 : 340);
        SizeToContent = SizeToContent.Height;
    }

    public Proto()
    {
        Title = "AI Usage (prototype)";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = false;
        MinWidth = 300; MinHeight = 120;
        Background = Bg;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0)
        });

        var header = new DockPanel { Margin = new Thickness(10, 7, 6, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        updated = T("updated 2 min ago", 10, Dim);
        updated.Margin = new Thickness(8, 1, 0, 0);
        buttons.Children.Add(Chip("⟳", false, delegate { updated.Text = "updated just now"; }));
        buttons.Children.Add(Chip("✕", false, delegate { Close(); }));
        header.Children.Add(buttons);
        var title = T("AI Usage", 12, Fg);
        title.FontWeight = FontWeights.SemiBold;
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(title);
        left.Children.Add(updated);
        header.Children.Add(left);
        DockPanel.SetDock(header, Dock.Top);

        var footer = new DockPanel { Margin = new Thickness(10, 4, 8, 7) };
        DockPanel.SetDock(chips, Dock.Right);
        footer.Children.Add(chips);
        footer.Children.Add(T("PROTOTYPE · fake data · layout", 10, Dim));
        DockPanel.SetDock(footer, Dock.Bottom);

        var dock = new DockPanel();
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = new Border { BorderBrush = Line, BorderThickness = new Thickness(1), Child = dock };

        MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.Key == Key.D1) Show(0); else if (e.Key == Key.D2) Show(1); else if (e.Key == Key.D3) Show(2);
            else if (e.Key == Key.Escape) Close();
        };

        tray.Icon = System.Drawing.SystemIcons.Application;
        tray.Text = "AI Usage (prototype) - update available: v0.2.0";
        tray.Visible = true;
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Layout A - table with bars", null, delegate { Show(0); });
        menu.Items.Add("Layout B - dots", null, delegate { Show(1); });
        menu.Items.Add("Layout C - cards", null, delegate { Show(2); });
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Refresh now", null, delegate { updated.Text = "updated just now"; });
        menu.Items.Add("Toggle always on top", null, delegate { Topmost = !Topmost; });
        menu.Items.Add("Update available: v0.2.0 (fake)", null, delegate { });
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { Show(); Activate(); };
        Closed += delegate { tray.Visible = false; tray.Dispose(); };

        Show(0);
        Loaded += delegate
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 16;
            Top = wa.Bottom - ActualHeight - 16;
        };
    }

    [STAThread]
    static void Main()
    {
        new Application().Run(new Proto());
    }
}
