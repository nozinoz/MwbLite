// MwbLite: visual machine-layout editor used by the Settings window. Shows MWB's 4 layout slots as one row
// or a 2x2 grid; drag a PC onto another slot to move it there (swapping with whatever is in that slot).
//
// Mouse:    drag a PC to move it; double-click a PC or an empty slot to rename/add; right-click for a menu.
// Keyboard: arrows select a slot; Ctrl+arrows move the selected PC; F2/Enter rename; Delete removes.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace MouseWithoutBorders.Lite
{
    internal sealed class LayoutEditor : Control
    {
        private readonly string[] slots = Enumerable.Repeat(string.Empty, LiteConfig.MaxMachines).ToArray();
        private readonly EditBox editor = new();
        private readonly ContextMenuStrip menu = new();
        private bool oneRow = true;
        private int selected;
        private int editing = -1;

        // Drag state.
        private int pressedSlot = -1;
        private Point pressedAt;
        private bool dragging;
        private Point dragPoint;
        private int dropTarget = -1;

        internal LayoutEditor()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "Machine layout";
            AccessibleDescription = "Arrow keys select a slot. Ctrl+arrow keys move the selected PC. F2 renames, Delete removes.";

            editor.Visible = false;
            editor.CharacterCasing = CharacterCasing.Upper;
            editor.BorderStyle = BorderStyle.FixedSingle;
            editor.KeyDown += Editor_KeyDown;
            editor.LostFocus += (s, e) => CommitEdit();
            Controls.Add(editor);

            menu.Items.Add("Rename…", null, (s, e) => BeginEdit(selected));
            menu.Items.Add("Remove", null, (s, e) => RemoveAt(selected));
            menu.Opening += (s, e) =>
            {
                menu.Items[0].Text = slots[selected].Length > 0 ? "Rename…" : "Add a PC name…";
                menu.Items[1].Enabled = slots[selected].Length > 0;
            };
            ContextMenuStrip = menu;
        }

        /// <summary>Raised whenever the user changes the layout.</summary>
        internal event EventHandler LayoutChanged;

        /// <summary>This computer's name, highlighted in the layout.</summary>
        internal string ThisPc { get; set; } = string.Empty;

        internal bool OneRow
        {
            get => oneRow;
            set
            {
                oneRow = value;
                UpdateSize();
                Invalidate();
            }
        }

        /// <summary>The 4 slots in MWB order (row-major), "" for empty.</summary>
        internal string[] Slots
        {
            get => (string[])slots.Clone();
            set
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    slots[i] = value != null && i < value.Length ? LiteConfig.NormalizeName(value[i]) : string.Empty;
                }

                Invalidate();
            }
        }

        private int Em => TextRenderer.MeasureText("M", Font).Width;

        private Size SlotSize => new(Em * 11, Em * 6);

        private int Gap => Em;

        private int Columns => oneRow ? LiteConfig.MaxMachines : 2;

        // ---- operations (also used by tests) ----------------------------------------------------

        /// <summary>Moves the PC in <paramref name="from"/> to <paramref name="to"/>, swapping with what is there.</summary>
        internal bool MoveTile(int from, int to)
        {
            if (from == to || from < 0 || to < 0 || from >= slots.Length || to >= slots.Length || slots[from].Length == 0)
            {
                return false;
            }

            (slots[from], slots[to]) = (slots[to], slots[from]);
            selected = to;
            OnLayoutChanged();
            return true;
        }

        /// <summary>Puts a name in the first empty slot. Returns false if it is already there or all slots are used.</summary>
        internal bool AddName(string name)
        {
            name = LiteConfig.NormalizeName(name);
            if (name.Length == 0 || slots.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            int free = Array.FindIndex(slots, s => s.Length == 0);
            if (free < 0)
            {
                return false;
            }

            slots[free] = name;
            selected = free;
            OnLayoutChanged();
            return true;
        }

        internal void RemoveAt(int index)
        {
            if (index >= 0 && index < slots.Length && slots[index].Length > 0)
            {
                slots[index] = string.Empty;
                OnLayoutChanged();
            }
        }

        // Slot index in direction (dx, dy) from index, or -1 at the edge of the grid.
        internal int Neighbour(int index, int dx, int dy)
        {
            int col = (index % Columns) + dx;
            int row = (index / Columns) + dy;
            int rows = slots.Length / Columns;
            return col < 0 || col >= Columns || row < 0 || row >= rows ? -1 : (row * Columns) + col;
        }

        private void OnLayoutChanged()
        {
            Invalidate();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- geometry & painting -----------------------------------------------------------------

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateSize();
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            UpdateSize();
        }

        private void UpdateSize()
        {
            int rows = slots.Length / Columns;
            Size = new Size((Columns * SlotSize.Width) + ((Columns - 1) * Gap) + 2, (rows * SlotSize.Height) + ((rows - 1) * Gap) + 2);
        }

        private Rectangle SlotBounds(int index)
        {
            int col = index % Columns;
            int row = index / Columns;
            return new Rectangle(1 + (col * (SlotSize.Width + Gap)), 1 + (row * (SlotSize.Height + Gap)), SlotSize.Width, SlotSize.Height);
        }

        private int SlotAt(Point p)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (SlotBounds(i).Contains(p))
                {
                    return i;
                }
            }

            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            for (int i = 0; i < slots.Length; i++)
            {
                bool hidden = dragging && i == pressedSlot;
                DrawSlot(g, SlotBounds(i), hidden ? string.Empty : slots[i], i, dropTarget == i && dragging && i != pressedSlot);
            }

            if (dragging && pressedSlot >= 0)
            {
                var r = SlotBounds(pressedSlot);
                r.Offset(dragPoint.X - pressedAt.X, dragPoint.Y - pressedAt.Y);
                DrawTile(g, r, slots[pressedSlot], lifted: true);
            }
        }

        private void DrawSlot(Graphics g, Rectangle r, string name, int index, bool dropHighlight)
        {
            using var path = Rounded(r, Em / 2);
            if (name.Length == 0)
            {
                using var pen = new Pen(dropHighlight ? SystemColors.Highlight : SystemColors.ControlDark, dropHighlight ? 2 : 1) { DashStyle = DashStyle.Dash };
                g.DrawPath(pen, path);
                TextRenderer.DrawText(g, editing == index ? string.Empty : $"Empty\r\n(double-click to add)", Font, r, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            else
            {
                DrawTile(g, r, name, lifted: false, dropHighlight);
            }

            if (Focused && index == selected && !dragging)
            {
                var focus = Rectangle.Inflate(r, -3, -3);
                ControlPaint.DrawFocusRectangle(g, focus);
            }
        }

        private void DrawTile(Graphics g, Rectangle r, string name, bool lifted, bool dropHighlight = false)
        {
            bool me = string.Equals(name, ThisPc, StringComparison.OrdinalIgnoreCase);
            using var path = Rounded(r, Em / 2);
            using (var fill = new SolidBrush(SystemColors.Window))
            {
                g.FillPath(fill, path);
            }

            Color borderColor = dropHighlight || me ? SystemColors.Highlight : SystemColors.ControlDark;
            using (var border = new Pen(borderColor, me || dropHighlight || lifted ? 2 : 1))
            {
                g.DrawPath(border, path);
            }

            // Monitor glyph.
            int w = Em * 3, h = Em * 2;
            var screen = new Rectangle(r.Left + ((r.Width - w) / 2), r.Top + (Em * 3 / 4), w, h);
            using (var screenFill = new SolidBrush(me ? SystemColors.Highlight : SystemColors.ControlDark))
            {
                g.FillRectangle(screenFill, screen);
                g.FillRectangle(screenFill, screen.Left + (w / 2) - (Em / 6), screen.Bottom, Em / 3, Em / 3);
                g.FillRectangle(screenFill, screen.Left + (w / 2) - (Em / 2), screen.Bottom + (Em / 3), Em, Em / 6);
            }

            var text = new Rectangle(r.Left + 4, screen.Bottom + (Em * 2 / 3), r.Width - 8, r.Bottom - screen.Bottom - (Em * 2 / 3) - 2);
            using var bold = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(g, me ? name + "\r\n(this PC)" : name, bold, text, SystemColors.WindowText, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---- mouse ---------------------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _ = Focus();
            int slot = SlotAt(e.Location);
            if (slot < 0)
            {
                return;
            }

            selected = slot;
            if (e.Button == MouseButtons.Left && slots[slot].Length > 0)
            {
                pressedSlot = slot;
                pressedAt = e.Location;
            }

            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (pressedSlot < 0)
            {
                Cursor = SlotAt(e.Location) is int s && s >= 0 && slots[s].Length > 0 ? Cursors.SizeAll : Cursors.Default;
                return;
            }

            var drag = SystemInformation.DragSize;
            if (!dragging && (Math.Abs(e.X - pressedAt.X) >= drag.Width || Math.Abs(e.Y - pressedAt.Y) >= drag.Height))
            {
                dragging = true;
                Capture = true;
            }

            if (dragging)
            {
                dragPoint = e.Location;
                dropTarget = SlotAt(e.Location);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                int from = pressedSlot;
                int to = SlotAt(e.Location);
                dragging = false;
                Capture = false;
                pressedSlot = -1;
                dropTarget = -1;
                if (!MoveTile(from, to))
                {
                    Invalidate();
                }
            }

            pressedSlot = -1;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int slot = SlotAt(e.Location);
            if (slot >= 0)
            {
                BeginEdit(slot);
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        // ---- keyboard ------------------------------------------------------------------------------

        protected override bool IsInputKey(Keys keyData)
        {
            // Enter (rename) must not trigger the Settings window's Save button.
            return (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            (int dx, int dy) = e.KeyCode switch
            {
                Keys.Left => (-1, 0),
                Keys.Right => (1, 0),
                Keys.Up => (0, -1),
                Keys.Down => (0, 1),
                _ => (0, 0),
            };

            if (dx != 0 || dy != 0)
            {
                int target = Neighbour(selected, dx, dy);
                if (target >= 0)
                {
                    if (!e.Control || !MoveTile(selected, target))
                    {
                        selected = target;
                        Invalidate();
                    }
                }

                e.Handled = true;
            }
            else if (e.KeyCode is Keys.F2 or Keys.Enter)
            {
                BeginEdit(selected);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                RemoveAt(selected);
                e.Handled = true;
            }
        }

        // ---- inline rename / add -------------------------------------------------------------------

        private void BeginEdit(int slot)
        {
            if (slot < 0)
            {
                return;
            }

            editing = slot;
            selected = slot;
            var r = SlotBounds(slot);
            editor.Font = Font;
            editor.Text = slots[slot];
            editor.Width = r.Width - Em;
            editor.Location = new Point(r.Left + (Em / 2), r.Top + ((r.Height - editor.Height) / 2));
            editor.Visible = true;
            editor.BringToFront();
            _ = editor.Focus();
            editor.SelectAll();
            Invalidate();
        }

        private void CommitEdit()
        {
            if (editing < 0)
            {
                return;
            }

            int slot = editing;
            editing = -1;
            editor.Visible = false;
            string name = LiteConfig.NormalizeName(editor.Text);
            if (!string.Equals(name, slots[slot], StringComparison.Ordinal))
            {
                slots[slot] = name;
                OnLayoutChanged();
            }

            _ = Focus();
            Invalidate();
        }

        private void Editor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                CommitEdit();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                editing = -1;
                editor.Visible = false;
                _ = Focus();
                Invalidate();
            }
        }

        // Enter/Esc commit or cancel the rename instead of pressing the Settings window's Save/Cancel buttons.
        private sealed class EditBox : TextBox
        {
            protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Enter or Keys.Escape || base.IsInputKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                menu.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
