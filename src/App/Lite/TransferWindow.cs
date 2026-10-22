// MwbLite: small progress window for file copies (bottom-right, above the taskbar). It never takes focus, so
// moving the mouse to another PC or typing is not interrupted. One row per transfer, each with Cancel. It closes
// itself a few seconds after the last transfer finished.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MouseWithoutBorders.Lite
{
    internal sealed class TransferWindow : System.Windows.Forms.Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int MaxRows = 3;

        private readonly Timer refresh = new() { Interval = 250 };
        private readonly TableLayoutPanel rows;
        private readonly Dictionary<int, Row> rowById = new();

        private static TransferWindow instance;

        private sealed class Row
        {
            internal Label Name;
            internal Label Detail;
            internal ProgressBar Bar;
            internal Button Cancel;
            internal Panel Panel;
        }

        private TransferWindow()
        {
            Text = "MwbLite – copying files";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(8);

            rows = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            Controls.Add(rows);

            refresh.Tick += (_, _) => UpdateRows();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        /// <summary>Shows the window (UI thread) if it isn't already open.</summary>
        internal static void ShowIfNeeded()
        {
            if (instance != null && !instance.IsDisposed)
            {
                return;
            }

            instance = new TransferWindow();
            instance.UpdateRows();
            instance.Show();
            instance.PlaceBottomRight();
            instance.refresh.Start();
        }

        internal static bool IsOpen => instance != null && !instance.IsDisposed;

        private void PlaceBottomRight()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        }

        private Row NewRow(LiteTransfers.Transfer t)
        {
            int width = (int)(Font.Height * 24);
            var row = new Row
            {
                Name = new Label { AutoSize = false, Width = width, Height = Font.Height + 4, Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true },
                Detail = new Label { AutoSize = false, Width = width, Height = Font.Height + 4, AutoEllipsis = true },
                Bar = new ProgressBar { Width = width, Height = Font.Height, Maximum = 1000 },
                Cancel = new Button { Text = "Cancel", AutoSize = true },
            };
            int id = t.Id;
            row.Cancel.Click += (_, _) =>
            {
                row.Cancel.Enabled = false;
                LiteTransfers.Cancel(id);
            };

            var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            grid.Controls.Add(row.Name, 0, 0);
            grid.Controls.Add(row.Detail, 0, 1);
            grid.Controls.Add(row.Bar, 0, 2);
            grid.Controls.Add(row.Cancel, 1, 2);
            row.Panel = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            row.Panel.Controls.Add(grid);
            return row;
        }

        private void UpdateRows()
        {
            DateTime now = DateTime.UtcNow;
            List<LiteTransfers.Transfer> list = LiteTransfers.Snapshot(now).Take(MaxRows).ToList();
            if (list.Count == 0)
            {
                refresh.Stop();
                Close();
                return;
            }

            foreach (int gone in rowById.Keys.Except(list.Select(t => t.Id)).ToList())
            {
                rows.Controls.Remove(rowById[gone].Panel);
                rowById[gone].Panel.Dispose();
                rowById.Remove(gone);
            }

            foreach (LiteTransfers.Transfer t in list)
            {
                if (!rowById.TryGetValue(t.Id, out Row row))
                {
                    row = NewRow(t);
                    rowById[t.Id] = row;
                    rows.Controls.Add(row.Panel);
                }

                row.Name.Text = t.Name;
                row.Detail.Text = LiteTransfers.Describe(t, now);
                double f = t.Fraction;
                if (t.IsActive && (t.Preparing || f < 0))
                {
                    row.Bar.Style = ProgressBarStyle.Marquee;
                }
                else
                {
                    row.Bar.Style = ProgressBarStyle.Continuous;
                    row.Bar.Value = t.IsActive ? (int)(Math.Max(f, 0) * 1000) : (t.Succeeded ? 1000 : row.Bar.Value);
                }

                row.Cancel.Visible = t.IsActive;
            }

            PlaceBottomRight();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
