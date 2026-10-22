// MwbLite: small progress window for file copies (bottom-right, above the taskbar), styled like a Windows 11
// flyout: rounded corners, light or dark to match the user's app theme, a thin accent progress bar per copy and a
// Cancel link. It never takes focus, so moving the mouse to another PC or typing is not interrupted. It closes itself
// a few seconds after the last copy finished. Drawn by hand (no child controls) so it looks the same everywhere,
// including when the service-mode (SYSTEM) instance shows it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseWithoutBorders.Lite
{
    internal sealed class TransferWindow : System.Windows.Forms.Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int CS_DROPSHADOW = 0x00020000;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        private const int MaxRows = 3;

        private static TransferWindow instance;

        private readonly Timer refresh = new() { Interval = 200 };
        private readonly Dictionary<int, RectangleF> cancelRects = new();
        private List<LiteTransfers.Transfer> rows = new();
        private int hoverCancel = -1;
        private bool light;

        private TransferWindow()
        {
            Text = "MwbLite – copying files";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Font = new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            refresh.Tick += (_, _) => UpdateRows();
        }

        internal static bool IsOpen => instance != null && !instance.IsDisposed;

        /// <summary>Development preview (UiPreview): the window as it would look now, drawn to a bitmap.</summary>
        internal static Bitmap Snapshot(bool light)
        {
            using var w = new TransferWindow();
            w.UpdateRows();
            w.light = light;
            w.BackColor = w.Back;
            var bm = new Bitmap(w.Width, w.Height);
            w.DrawToBitmap(bm, new Rectangle(0, 0, w.Width, w.Height));
            return bm;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        /// <summary>Shows the window (UI thread) if it isn't already open.</summary>
        internal static void ShowIfNeeded()
        {
            if (IsOpen)
            {
                return;
            }

            instance = new TransferWindow();
            instance.UpdateRows();
            instance.Show();
            instance.refresh.Start();
        }

        private float S => DeviceDpi / 96f;

        private Color Back => light ? Color.FromArgb(0xF9, 0xF9, 0xF9) : Color.FromArgb(0x2B, 0x2B, 0x2B);

        private Color Fore => light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;

        private Color Secondary => light ? Color.FromArgb(0x60, 0x60, 0x60) : Color.FromArgb(0xC5, 0xC5, 0xC5);

        private Color Track => light ? Color.FromArgb(0xDD, 0xDD, 0xDD) : Color.FromArgb(0x48, 0x48, 0x48);

        private Color AccentColor => light ? Ui.LiteArt.Accent : Ui.LiteArt.Activity;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }

        private void UpdateRows()
        {
            DateTime now = DateTime.UtcNow;
            rows = LiteTransfers.Snapshot(now).Take(MaxRows).ToList();
            if (rows.Count == 0)
            {
                refresh.Stop();
                Close();
                return;
            }

            light = Ui.TaskbarTheme.AppsLight;
            BackColor = Back;
            int w = (int)(380 * S);
            int h = (int)((44 + (rows.Count * 66) + 8) * S);
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            SetBounds(area.Right - w - (int)(12 * S), area.Bottom - h - (int)(12 * S), w, h);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float s = S, pad = 16 * s;
            DateTime now = DateTime.UtcNow;
            cancelRects.Clear();

            using (var border = new Pen(light ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.FromArgb(0x40, 0x40, 0x40)))
            {
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }

            // Header: logo and title.
            using (Bitmap logo = Ui.LiteArt.LogoBitmap((int)(18 * s)))
            {
                g.DrawImage(logo, pad, 13 * s);
            }

            using var titleFont = new Font(Font, FontStyle.Bold);
            using var fore = new SolidBrush(Fore);
            using var secondary = new SolidBrush(Secondary);
            g.DrawString("Copying files", titleFont, fore, pad + (26 * s), 12 * s);

            float y = 44 * s;
            using var nameFormat = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            foreach (LiteTransfers.Transfer t in rows)
            {
                float right = Width - pad;
                if (t.IsActive)
                {
                    SizeF cs = g.MeasureString("Cancel", Font);
                    var cr = new RectangleF(right - cs.Width, y - (2 * s), cs.Width, cs.Height + (4 * s));
                    cancelRects[t.Id] = cr;
                    using var link = new SolidBrush(hoverCancel == t.Id ? Fore : AccentColor);
                    using var linkFont = new Font(Font, hoverCancel == t.Id ? FontStyle.Underline : FontStyle.Regular);
                    g.DrawString("Cancel", linkFont, link, cr.X, y);
                    right = cr.X - (8 * s);
                }

                g.DrawString(t.Name, titleFont, fore, new RectangleF(pad, y, right - pad, 20 * s), nameFormat);
                g.DrawString(LiteTransfers.Describe(t, now), Font, secondary, new RectangleF(pad, y + (20 * s), Width - (2 * pad), 18 * s), nameFormat);

                // Progress bar: thin, rounded; marquee-like stripe while the size isn't known yet.
                var track = new RectangleF(pad, y + (44 * s), Width - (2 * pad), 4 * s);
                using (var trackBrush = new SolidBrush(Track))
                using (GraphicsPath p = Pill(track))
                {
                    g.FillPath(trackBrush, p);
                }

                double f = t.Fraction;
                RectangleF fill = track;
                if (t.IsActive && (t.Preparing || f < 0))
                {
                    float phase = (float)((now.Ticks / TimeSpan.TicksPerMillisecond) % 1500) / 1500f;
                    fill = new RectangleF(track.X + ((track.Width * 1.3f) * phase) - (track.Width * 0.3f), track.Y, track.Width * 0.3f, track.Height);
                    fill.Intersect(track);
                }
                else
                {
                    fill.Width = (float)(track.Width * (t.IsActive ? Math.Max(f, 0) : t.Succeeded ? 1 : Math.Max(f, 0)));
                }

                if (fill.Width > 0)
                {
                    Color c = !t.IsActive && !t.Succeeded ? Ui.LiteArt.Red : t.Succeeded && !t.IsActive ? Ui.LiteArt.Green : AccentColor;
                    using var fillBrush = new SolidBrush(c);
                    using GraphicsPath p = Pill(fill);
                    g.FillPath(fillBrush, p);
                }

                y += 66 * s;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = -1;
            foreach (var (id, r) in cancelRects)
            {
                if (r.Contains(e.Location))
                {
                    hit = id;
                }
            }

            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            if (hit != hoverCancel)
            {
                hoverCancel = hit;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverCancel = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            foreach (var (id, r) in cancelRects)
            {
                if (r.Contains(e.Location))
                {
                    LiteTransfers.Cancel(id);
                }
            }
        }

        private static GraphicsPath Pill(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(r.Height, r.Width));
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
            }

            base.Dispose(disposing);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
