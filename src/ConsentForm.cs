using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AiUsageWidget.Core;

namespace AiUsageWidget
{
    // First-run window: what each provider reads on this PC and who it contacts, one tick
    // per provider. Continue hands the ticked list back in Result; the caller stores it
    // and marks consent done. The row plan (order and initial ticks) is pure and
    // self-tested; the window itself is never opened by the self-test.
    public sealed class ConsentForm : Form
    {
        public sealed class ConsentRow
        {
            public IUsageProvider Provider;
            public bool Checked;
            public string Mark;   // null, "not found on this PC" or "experimental"
        }

        public const string NotFoundMark = "not found on this PC";
        public const string ExperimentalMark = "experimental";

        readonly List<ConsentRow> rows;
        readonly List<CheckBox> boxes;

        public ConsentForm(IList<IUsageProvider> providers, Func<string, bool> isDetected)
        {
            rows = PlanRows(providers, isDetected);
            boxes = new List<CheckBox>();

            Text = "AI Usage Widget: choose what to show";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            int wrapWidth = 460 * DeviceDpi / 96;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var intro = new Label();
            intro.Text = "The widget reads the sign-in of each tool below from this PC only, " +
                "to ask that vendor for your usage. Nothing is sent anywhere else, and tokens " +
                "are never stored or logged.";
            intro.AutoSize = true;
            intro.MaximumSize = new Size(wrapWidth, 0);
            intro.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(intro);

            var list = new TableLayoutPanel();
            list.Dock = DockStyle.Top;
            list.AutoSize = true;
            list.ColumnCount = 1;
            foreach (ConsentRow row in rows)
                boxes.Add(AddProviderRow(list, row, wrapWidth - 60 * DeviceDpi / 96));

            var scroll = new Panel();
            scroll.AutoScroll = true;
            scroll.Dock = DockStyle.Fill;
            scroll.Controls.Add(list);
            root.Controls.Add(scroll);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.AutoSize = true;
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = new Padding(0, 10, 0, 0);

            var cont = new Button();
            cont.Text = "Continue";
            cont.DialogResult = DialogResult.OK;
            var exit = new Button();
            exit.Text = "Exit";
            exit.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cont);
            buttons.Controls.Add(exit);
            root.Controls.Add(buttons);

            AcceptButton = cont;
            CancelButton = exit;
            ClientSize = new Size(500, 530);
        }

        // The pure part: detected providers first (given order kept) and ticked, the rest
        // unticked; experimental ones never tick and carry the "experimental" mark, which
        // also wins over "not found on this PC".
        public static List<ConsentRow> PlanRows(IList<IUsageProvider> providers,
            Func<string, bool> isDetected)
        {
            var planned = new List<ConsentRow>();
            if (providers == null) return planned;
            var ids = new List<IUsageProvider>();
            foreach (IUsageProvider p in providers)
                if (p != null && !ContainsId(ids, p.Id)) ids.Add(p);

            foreach (IUsageProvider p in ids)
                if (IsDetectedSafe(p, isDetected)) planned.Add(RowFor(p, true));
            foreach (IUsageProvider p in ids)
                if (!IsDetectedSafe(p, isDetected)) planned.Add(RowFor(p, false));
            return planned;
        }

        static bool ContainsId(List<IUsageProvider> list, string id)
        {
            foreach (IUsageProvider p in list)
                if (p.Id == id) return true;
            return false;
        }

        static bool IsDetectedSafe(IUsageProvider p, Func<string, bool> isDetected)
        {
            if (isDetected == null) return false;
            try
            {
                return isDetected(p.Id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        static ConsentRow RowFor(IUsageProvider p, bool detected)
        {
            bool experimental = Array.IndexOf(ProviderRegistry.ExperimentalIds, p.Id) >= 0;
            var row = new ConsentRow();
            row.Provider = p;
            row.Checked = detected && !experimental;
            row.Mark = experimental
                ? ExperimentalMark
                : (detected ? null : NotFoundMark);
            return row;
        }

        // Every provider, ticked as shown, in display order.
        public List<ProviderSetting> Result
        {
            get
            {
                var list = new List<ProviderSetting>();
                for (int i = 0; i < rows.Count; i++)
                {
                    var ps = new ProviderSetting();
                    ps.Id = rows[i].Provider.Id;
                    ps.Enabled = boxes[i].Checked;
                    list.Add(ps);
                }
                return list;
            }
        }

        // One tick row: provider name (plus the mark when there is one) with the Reads and
        // Contacts disclosure beneath in a dim smaller font, and the unofficial-endpoint
        // warning for non-official providers. Shared with the settings subscriptions tab;
        // returns the checkbox so the caller can read the final ticks.
        internal static CheckBox AddProviderRow(TableLayoutPanel parent, ConsentRow row,
            int wrapWidth)
        {
            var box = new CheckBox();
            box.Text = row.Provider.DisplayName + (row.Mark == null ? "" : " — " + row.Mark);
            box.Checked = row.Checked;
            box.AutoSize = true;
            box.Margin = new Padding(0, 8, 0, 0);
            parent.Controls.Add(box);

            ProviderInfoEntry info = ProviderInfo.For(row.Provider.Id);
            parent.Controls.Add(DimLabel(info.Reads, wrapWidth));
            parent.Controls.Add(DimLabel(info.Contacts, wrapWidth));
            if (!info.Official)
                parent.Controls.Add(DimLabel("unofficial endpoint, may stop working", wrapWidth));
            return box;
        }

        internal static Label DimLabel(string text, int wrapWidth)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = SystemColors.GrayText;
            l.Font = new Font("Segoe UI", 8.25F);
            l.MaximumSize = new Size(Math.Max(1, wrapWidth), 0);
            l.Margin = new Padding(24, 0, 0, 0);
            return l;
        }
    }
}
