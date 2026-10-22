// MwbLite: the PCs as tiles in MWB's 4 slots (WPF). Drag a tile onto another slot to move it there (it swaps with
// what's there); double-click a tile or an empty slot to rename or add; Delete removes; arrow keys select,
// Ctrl+arrows move, F2 renames. Each tile shows the PC's live connection state.
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MouseWithoutBorders.Lite.Ui
{
    internal sealed class LayoutEditorView : Canvas
    {
        private const double TileW = 148, TileH = 96, Gap = 12;

        private readonly LayoutModel model;
        private readonly TextBox editor = new() { FontSize = 14, Padding = new Thickness(4, 2, 4, 2) };
        private readonly Border[] tiles = new Border[LiteConfig.MaxMachines];

        private int editing = -1;
        private int pressed = -1;
        private Point pressAt;
        private bool dragging;
        private int dropTarget = -1;

        internal LayoutEditorView(LayoutModel model)
        {
            this.model = model;
            Focusable = true;
            FocusVisualStyle = null;
            Background = Brushes.Transparent;
            ClipToBounds = false;
            model.Redraw += (_, _) => Rebuild();

            editor.KeyDown += Editor_KeyDown;
            editor.LostKeyboardFocus += (_, _) => CommitEdit();

            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Rename or add…", () => BeginEdit(model.Selected)));
            menu.Items.Add(MenuItem("Remove", () => model.RemoveAt(model.Selected)));
            ContextMenu = menu;

            AutomationProperties.SetName(this, "PC layout");
            AutomationProperties.SetHelpText(this, "Arrow keys select a slot, Ctrl+arrow keys move the selected PC, F2 renames, Delete removes.");
            GotKeyboardFocus += (_, _) => Rebuild();
            LostKeyboardFocus += (_, _) => Rebuild();
            Rebuild();
        }

        /// <summary>Live state of a PC by name (null: unknown); the window refreshes it and calls <see cref="Rebuild"/>.</summary>
        internal Func<string, PcState?> StateOf { get; set; } = _ => null;

        private static MenuItem MenuItem(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            return item;
        }

        private Point SlotOrigin(int index) => new((index % model.Columns) * (TileW + Gap), (index / model.Columns) * (TileH + Gap));

        private int SlotAt(Point p)
        {
            for (int i = 0; i < LiteConfig.MaxMachines; i++)
            {
                Point o = SlotOrigin(i);
                if (p.X >= o.X && p.X < o.X + TileW && p.Y >= o.Y && p.Y < o.Y + TileH)
                {
                    return i;
                }
            }

            return -1;
        }

        internal void Rebuild()
        {
            // The rename box stays put (removing it would take its focus and end the edit).
            for (int c = Children.Count - 1; c >= 0; c--)
            {
                if (Children[c] != editor)
                {
                    Children.RemoveAt(c);
                }
            }

            Width = (model.Columns * TileW) + ((model.Columns - 1) * Gap);
            Height = (model.Rows * TileH) + ((model.Rows - 1) * Gap);

            for (int i = 0; i < LiteConfig.MaxMachines; i++)
            {
                tiles[i] = MakeSlot(i);
                Point o = SlotOrigin(i);
                SetLeft(tiles[i], o.X);
                SetTop(tiles[i], o.Y);
                Children.Add(tiles[i]);
            }

            if (editing >= 0)
            {
                Point o = SlotOrigin(editing);
                editor.Width = TileW - 20;
                SetLeft(editor, o.X + 10);
                SetTop(editor, o.Y + ((TileH - 30) / 2));
                SetZIndex(editor, 20);
                if (!Children.Contains(editor))
                {
                    Children.Add(editor);
                }
            }
            else if (Children.Contains(editor))
            {
                Children.Remove(editor);
            }
        }

        private Border MakeSlot(int i)
        {
            string name = model[i];
            bool selected = IsKeyboardFocusWithin && i == model.Selected && !dragging;
            var tile = new Border { Width = TileW, Height = TileH, CornerRadius = new CornerRadius(8) };

            if (name.Length == 0)
            {
                var dashes = new Rectangle
                {
                    RadiusX = 8,
                    RadiusY = 8,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    StrokeThickness = i == dropTarget ? 2 : 1,
                };
                dashes.SetResourceReference(Shape.StrokeProperty, i == dropTarget ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush");
                var hint = Kit.Hint(editing == i ? string.Empty : "Empty\nDouble-click to add");
                hint.TextAlignment = TextAlignment.Center;
                hint.HorizontalAlignment = HorizontalAlignment.Center;
                hint.VerticalAlignment = VerticalAlignment.Center;
                var g = new Grid();
                g.Children.Add(dashes);
                g.Children.Add(hint);
                tile.Child = g;
                if (selected)
                {
                    tile.BorderThickness = new Thickness(2);
                    tile.SetResourceReference(Border.BorderBrushProperty, "TextFillColorPrimaryBrush");
                }

                return tile;
            }

            bool me = model.IsThisPc(i);
            tile.BorderThickness = new Thickness(me || selected || i == dropTarget ? 2 : 1);
            tile.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            tile.SetResourceReference(Border.BorderBrushProperty, selected ? "TextFillColorPrimaryBrush" : me || i == dropTarget ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
            tile.Cursor = Cursors.SizeAll;

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Kit.Icon("", 26); // TV monitor
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            if (me)
            {
                icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentFillColorDefaultBrush");
            }

            stack.Children.Add(icon);
            stack.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 6, 6, 0), MaxWidth = TileW - 12 });

            PcState? state = me ? PcState.ThisPc : StateOf(name);
            if (state != null)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                var dot = Kit.Dot(Kit.DotBrushKey(state.Value));
                dot.Width = dot.Height = 8;
                dot.Margin = new Thickness(0, 0, 6, 0);
                line.Children.Add(dot);
                line.Children.Add(Kit.Hint(state == PcState.ThisPc ? "This PC" : LiteStatus.StateText(state.Value)));
                ((TextBlock)line.Children[1]).Margin = new Thickness(0);
                stack.Children.Add(line);
            }

            tile.Child = stack;
            if (dragging && i == pressed)
            {
                tile.Opacity = 0.9;
                tile.RenderTransform = new TranslateTransform();
                SetZIndex(tile, 10);
                tile.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35 };
            }

            return tile;
        }

        // ---- mouse ----------------------------------------------------------------------------------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _ = Focus();
            int slot = SlotAt(e.GetPosition(this));
            if (slot < 0)
            {
                return;
            }

            model.Selected = slot;
            if (e.ClickCount == 2)
            {
                BeginEdit(slot);
                e.Handled = true;
                return;
            }

            if (model[slot].Length > 0)
            {
                pressed = slot;
                pressAt = e.GetPosition(this);
                _ = CaptureMouse();
            }

            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (pressed < 0)
            {
                return;
            }

            Point p = e.GetPosition(this);
            if (!dragging && (Math.Abs(p.X - pressAt.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(p.Y - pressAt.Y) >= SystemParameters.MinimumVerticalDragDistance))
            {
                dragging = true;
                Rebuild();
            }

            if (dragging)
            {
                int target = SlotAt(p);
                if (target != dropTarget)
                {
                    dropTarget = target;
                    Rebuild();
                }

                if (tiles[pressed].RenderTransform is TranslateTransform t)
                {
                    t.X = p.X - pressAt.X;
                    t.Y = p.Y - pressAt.Y;
                }
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            int from = pressed, to = SlotAt(e.GetPosition(this));
            bool wasDragging = dragging;
            pressed = -1;
            dragging = false;
            dropTarget = -1;
            ReleaseMouseCapture();
            if (!wasDragging || !model.MoveTile(from, to))
            {
                Rebuild();
            }
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            int slot = SlotAt(e.GetPosition(this));
            if (slot >= 0)
            {
                model.Selected = slot;
            }
        }

        // ---- keyboard -------------------------------------------------------------------------------------

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (editing >= 0)
            {
                return;
            }

            (int dx, int dy) = e.Key switch
            {
                Key.Left => (-1, 0),
                Key.Right => (1, 0),
                Key.Up => (0, -1),
                Key.Down => (0, 1),
                _ => (0, 0),
            };

            if (dx != 0 || dy != 0)
            {
                int target = model.Neighbour(model.Selected, dx, dy);
                if (target >= 0 && (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || !model.MoveTile(model.Selected, target)))
                {
                    model.Selected = target;
                }

                e.Handled = true;
            }
            else if (e.Key is Key.F2 or Key.Enter)
            {
                BeginEdit(model.Selected);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                model.RemoveAt(model.Selected);
                e.Handled = true;
            }
        }

        // ---- inline rename / add --------------------------------------------------------------------------

        private void BeginEdit(int slot)
        {
            if (slot < 0)
            {
                return;
            }

            editing = slot;
            model.Selected = slot; // rebuilds, placing the editor
            editor.Text = model[slot];
            Rebuild();
            _ = editor.Focus();
            editor.SelectAll();
        }

        private void CommitEdit()
        {
            if (editing < 0)
            {
                return;
            }

            int slot = editing;
            editing = -1;
            model.SetName(slot, editor.Text);
            Rebuild();
        }

        private void Editor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitEdit();
                _ = Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                editing = -1;
                Rebuild();
                _ = Focus();
                e.Handled = true;
            }
        }
    }
}
