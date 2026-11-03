// MwbLite: Settings > PCs > Line up screens (WPF). One row per pair of neighbouring PCs: a small diagram of the two
// screens side by side (or stacked) where you drag the second screen along the first one's edge and drag its ends
// to resize it, plus Position and Size sliders for precision (and for the keyboard), Reset, and "Use real sizes".
// The maths is LiteEdgeAlignment; this only draws and edits one (start, end) span.
//
// Keyboard on the diagram: the arrow keys along the edge move the screen by 1 % (Shift: 5 %), Ctrl+arrow keys resize it.
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using WpfButton = System.Windows.Controls.Button;

namespace MouseWithoutBorders.Lite.Ui
{
    /// <summary>The diagram of one pair: the "from" screen and, beside or under it, the "to" screen at its span.</summary>
    internal sealed class EdgeAlignmentCanvas : Canvas
    {
        private const double Along = 200; // px along the shared edge
        private const double Gap = 6;
        private const double RangeMargin = 0.35; // room (in "from" edge lengths) kept around what is drawn
        private const double GrabPx = 7;

        private enum Grab
        {
            None,
            Body,
            Start,
            End,
        }

        private readonly EdgePair pair;
        private readonly double thick; // px across the edge, per screen
        private double start;
        private double end = 1;
        private double lo = -RangeMargin;
        private double hi = 1 + RangeMargin;
        private Grab grab;
        private double grabAt;
        private double grabStart;
        private double grabEnd;

        internal EdgeAlignmentCanvas(EdgePair pair)
        {
            this.pair = pair;
            thick = pair.Horizontal ? 84 : 54;
            Width = pair.Horizontal ? (2 * thick) + Gap : Along;
            Height = pair.Horizontal ? Along : (2 * thick) + Gap;
            Focusable = true;
            FocusVisualStyle = null;
            Background = Brushes.Transparent;
            ClipToBounds = true;
            GotKeyboardFocus += (_, _) => Rebuild();
            LostKeyboardFocus += (_, _) => Rebuild();

            string edge = pair.Horizontal ? "up and down" : "left and right";
            AutomationProperties.SetName(this, $"Position and size of {pair.To}'s screen next to {pair.From}'s");
            AutomationProperties.SetHelpText(this, $"Arrow keys move {pair.To}'s screen {edge} along {pair.From}'s edge, Shift moves further, Ctrl and arrow keys change its size.");
            Rebuild();
        }

        /// <summary>Raised when the user changes the span by dragging or with the keyboard.</summary>
        internal event EventHandler Changed;

        internal double Start => start;

        internal double End => end;

        /// <summary>Sets the span (clamped to the allowed range) without raising <see cref="Changed"/>.</summary>
        internal void SetSpan(double newStart, double newEnd)
        {
            (start, end) = Clamp(newStart, newEnd);
            if (grab == Grab.None)
            {
                lo = Math.Min(0, start) - RangeMargin;
                hi = Math.Max(1, end) + RangeMargin;
            }

            Rebuild();
        }

        private static (double Start, double End) Clamp(double s, double e)
        {
            double size = Math.Clamp(e - s, LiteEdgeAlignment.MinSpan, LiteEdgeAlignment.MaxValue - LiteEdgeAlignment.MinValue);
            s = Math.Clamp(s, LiteEdgeAlignment.MinValue, LiteEdgeAlignment.MaxValue - size);
            return (Math.Round(s, 3), Math.Round(s + size, 3));
        }

        // ---- drawing --------------------------------------------------------------------------------------

        private double ToPx(double fraction) => (fraction - lo) / (hi - lo) * Along;

        private double ToFraction(double px) => lo + (px / Along * (hi - lo));

        // The rectangle of a screen covering [a0, a1] along the edge; index 0 is "from", 1 is "to".
        private Rect ScreenRect(double a0, double a1, int index)
        {
            double across = index * (thick + Gap);
            double p0 = ToPx(a0);
            double len = ToPx(a1) - p0;
            return pair.Horizontal ? new Rect(across, p0, thick, len) : new Rect(p0, across, len, thick);
        }

        private double AlongOf(Point p) => pair.Horizontal ? p.Y : p.X;

        private void Rebuild()
        {
            Children.Clear();

            Rect from = ScreenRect(0, 1, 0);
            Rect to = ScreenRect(start, end, 1);

            Children.Add(Screen(from, pair.From, accent: false));
            Children.Add(Screen(to, pair.To, accent: true));

            // Where the cursor passes: the part of the "from" edge that faces the "to" screen.
            double a0 = Math.Max(0, start), a1 = Math.Min(1, end);
            if (a1 > a0)
            {
                Rect shared = ScreenRect(a0, a1, 0);
                Rect bar = pair.Horizontal ? new Rect(shared.Right - 4, shared.Top, 4, shared.Height) : new Rect(shared.Left, shared.Bottom - 4, shared.Width, 4);
                var line = new Rectangle { Width = bar.Width, Height = bar.Height, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false };
                line.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
                SetLeft(line, bar.X);
                SetTop(line, bar.Y);
                Children.Add(line);
            }

            // Grips on the two ends of the "to" screen.
            foreach (bool atStart in new[] { true, false })
            {
                double p = atStart ? (pair.Horizontal ? to.Top : to.Left) : (pair.Horizontal ? to.Bottom : to.Right);
                double across = (pair.Horizontal ? to.Left : to.Top) + ((pair.Horizontal ? to.Width : to.Height) / 2);
                var grip = new Border { Width = pair.Horizontal ? 26 : 5, Height = pair.Horizontal ? 5 : 26, CornerRadius = new CornerRadius(2.5), IsHitTestVisible = false };
                grip.SetResourceReference(Border.BackgroundProperty, "TextOnAccentFillColorPrimaryBrush");
                grip.BorderThickness = new Thickness(1);
                grip.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                SetLeft(grip, pair.Horizontal ? across - 13 : p - 2.5);
                SetTop(grip, pair.Horizontal ? p - 2.5 : across - 13);
                Children.Add(grip);
            }

            if (IsKeyboardFocused)
            {
                var focus = new Rectangle { Width = to.Width + 6, Height = to.Height + 6, RadiusX = 8, RadiusY = 8, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
                focus.SetResourceReference(Shape.StrokeProperty, "TextFillColorPrimaryBrush");
                SetLeft(focus, to.X - 3);
                SetTop(focus, to.Y - 3);
                Children.Add(focus);
            }
        }

        private static Border Screen(Rect r, string name, bool accent)
        {
            var box = new Border
            {
                Width = Math.Max(1, r.Width),
                Height = Math.Max(1, r.Height),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                IsHitTestVisible = false,
            };
            box.SetResourceReference(Border.BackgroundProperty, accent ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush");
            box.SetResourceReference(Border.BorderBrushProperty, accent ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush");

            var label = new TextBlock
            {
                Text = name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, accent ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
            box.Child = label;
            SetLeft(box, r.X);
            SetTop(box, r.Y);
            return box;
        }

        // ---- mouse ----------------------------------------------------------------------------------------

        private Grab HitTest(Point p)
        {
            Rect to = ScreenRect(start, end, 1);
            Rect hit = new(to.X - GrabPx, to.Y - GrabPx, to.Width + (2 * GrabPx), to.Height + (2 * GrabPx));
            if (!hit.Contains(p))
            {
                return Grab.None;
            }

            double a = AlongOf(p), a0 = ToPx(start), a1 = ToPx(end);
            bool nearStart = Math.Abs(a - a0) <= GrabPx, nearEnd = Math.Abs(a - a1) <= GrabPx;
            return nearStart && nearEnd ? (a > (a0 + a1) / 2 ? Grab.End : Grab.Start) : nearStart ? Grab.Start : nearEnd ? Grab.End : Grab.Body;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _ = Focus();
            Point p = e.GetPosition(this);
            grab = HitTest(p);
            if (grab != Grab.None)
            {
                grabAt = ToFraction(AlongOf(p));
                grabStart = start;
                grabEnd = end;
                _ = CaptureMouse();
            }

            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = e.GetPosition(this);
            if (grab == Grab.None)
            {
                Cursor = HitTest(p) == Grab.None ? Cursors.Arrow : pair.Horizontal ? Cursors.SizeNS : Cursors.SizeWE;
                return;
            }

            // The drawn range stays put while dragging, so the picture doesn't rescale under the mouse.
            double min = Math.Max(LiteEdgeAlignment.MinValue, lo), max = Math.Min(LiteEdgeAlignment.MaxValue, hi);
            double f = ToFraction(AlongOf(p));
            double delta = f - grabAt;
            double s = grabStart, t = grabEnd;
            bool snap = !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

            switch (grab)
            {
                case Grab.Start:
                    s = Math.Clamp(Snap(grabStart + delta, snap), min, grabEnd - LiteEdgeAlignment.MinSpan);
                    break;
                case Grab.End:
                    t = Math.Clamp(Snap(grabEnd + delta, snap), grabStart + LiteEdgeAlignment.MinSpan, max);
                    break;
                default:
                    double size = grabEnd - grabStart;
                    double near = Snap(grabStart + delta, snap), far = Snap(grabEnd + delta, snap) - size;
                    s = Math.Abs(near - (grabStart + delta)) <= Math.Abs(far - (grabStart + delta)) ? near : far; // whichever end sits closer to a magnet
                    s = Math.Clamp(s, min, Math.Max(min, max - size));
                    t = s + size;
                    break;
            }

            Update(s, t);
        }

        // Magnets at 0 and 1 (the "from" screen's own edges) and otherwise whole percent.
        private static double Snap(double value, bool snap)
        {
            if (snap)
            {
                foreach (double magnet in new[] { 0.0, 1.0 })
                {
                    if (Math.Abs(value - magnet) < 0.02)
                    {
                        return magnet;
                    }
                }
            }

            return Math.Round(value, 2);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (grab != Grab.None)
            {
                grab = Grab.None;
                ReleaseMouseCapture();
                SetSpan(start, end); // now the drawn range may grow
            }
        }

        // ---- keyboard -------------------------------------------------------------------------------------

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int direction = e.Key is Key.Up or Key.Left ? -1 : e.Key is Key.Down or Key.Right ? 1 : 0;
            if (direction == 0)
            {
                return;
            }

            double step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.05 : 0.01) * direction;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                (double s, double t) = Clamp(start, end + step);
                Update(s, t);
            }
            else
            {
                (double s, double t) = Clamp(start + step, end + step);
                Update(s, t);
            }

            if (grab == Grab.None)
            {
                SetSpan(start, end);
            }

            e.Handled = true;
        }

        private void Update(double s, double t)
        {
            (s, t) = Clamp(s, t);
            if (s == start && t == end)
            {
                return;
            }

            start = s;
            end = t;
            Rebuild();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>One pair in the "Line up screens" card: title, diagram, Position and Size sliders, Reset and Use real sizes.</summary>
    internal sealed class EdgeAlignmentRow : Grid
    {
        private readonly EdgeAlignmentCanvas canvas;
        private readonly Slider position = new() { Minimum = -150, Maximum = 150, SmallChange = 1, LargeChange = 10, Width = 230, TickFrequency = 25, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight, VerticalAlignment = VerticalAlignment.Center };
        private readonly Slider size = new() { Minimum = 5, Maximum = 300, SmallChange = 1, LargeChange = 10, Width = 230, TickFrequency = 25, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock positionText = new() { Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private readonly TextBlock sizeText = new() { Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private readonly TextBlock summary = Kit.Hint(string.Empty);
        private readonly TextBlock realHint = Kit.Hint(string.Empty);
        private readonly WpfButton resetButton;
        private readonly WpfButton realButton;
        private bool settingSliders;
        private bool realSizesAvailable = true;
        private bool busy;

        internal EdgeAlignmentRow(EdgePair pair, double start, double end, Action<EdgePair, double, double> changed, Action<EdgeAlignmentRow> useRealSizes)
        {
            Pair = pair;
            Changed = changed;
            string edge = pair.Horizontal ? "height" : "width";
            string side = pair.Horizontal ? "right" : "bottom";

            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });

            canvas = new EdgeAlignmentCanvas(pair) { VerticalAlignment = VerticalAlignment.Top };
            Children.Add(canvas);

            var title = new TextBlock { Text = $"{pair.From}  {(pair.Horizontal ? "→" : "↓")}  {pair.To}", FontWeight = FontWeights.SemiBold };
            var describe = Kit.Hint($"Where {pair.To}'s screen sits along {pair.From}'s {side} edge. Drag it, drag its ends to resize, or use the sliders.");

            position.ToolTip = $"Where {pair.To}'s screen starts, in % of {pair.From}'s {edge} (0 % is the top or left, 100 % the bottom or right)";
            size.ToolTip = $"How big {pair.To}'s screen is, in % of {pair.From}'s {edge}";
            AutomationProperties.SetName(position, $"Position of {pair.To}'s screen along {pair.From}'s edge, in percent");
            AutomationProperties.SetName(size, $"Size of {pair.To}'s screen, in percent of {pair.From}'s {edge}");
            position.ValueChanged += (_, e) => FromSlider(e.NewValue, null);
            size.ValueChanged += (_, e) => FromSlider(null, e.NewValue);

            resetButton = Kit.Button("Reset", (_, _) => SetSpan(0, 1, raise: true));
            resetButton.Margin = new Thickness(0, 0, 8, 0);
            resetButton.ToolTip = "Both screens meet along the whole edge: the cursor keeps its relative position, as without this setting";
            realButton = Kit.Button("Use real sizes", (_, _) => useRealSizes(this));
            realButton.Margin = new Thickness(0);
            realButton.ToolTip = "Asks both PCs for their physical screen size and lines the screens up to scale";

            var right = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
            right.Children.Add(title);
            right.Children.Add(describe);
            right.Children.Add(SliderLine("Position", position, positionText));
            right.Children.Add(SliderLine("Size", size, sizeText));
            summary.Margin = new Thickness(0, 6, 0, 0);
            right.Children.Add(summary);
            right.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { resetButton, realButton } });
            realHint.Margin = new Thickness(0, 4, 0, 0);
            right.Children.Add(realHint);
            SetColumn(right, 1);
            Children.Add(right);

            canvas.Changed += (_, _) => FromCanvas();
            canvas.SetSpan(start, end);
            Sync();
        }

        internal EdgePair Pair { get; }

        internal double Start => canvas.Start;

        internal double End => canvas.End;

        private Action<EdgePair, double, double> Changed { get; }

        private static StackPanel SliderLine(string label, Slider slider, TextBlock value)
        {
            var name = new TextBlock { Text = label, Width = 64, VerticalAlignment = VerticalAlignment.Center };
            return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { name, slider, value } };
        }

        /// <summary>Sets the span (for Reset, Use real sizes); <paramref name="raise"/>: tell the window it changed.</summary>
        internal void SetSpan(double start, double end, bool raise)
        {
            canvas.SetSpan(start, end);
            Sync();
            if (raise)
            {
                Changed(Pair, canvas.Start, canvas.End);
            }
        }

        /// <summary>Whether "Use real sizes" can work now, and why not.</summary>
        internal void SetRealSizesAvailable(bool available, string whyNot)
        {
            realSizesAvailable = available;
            if (!busy)
            {
                realButton.IsEnabled = available;
                realHint.Text = available ? string.Empty : whyNot;
            }
        }

        /// <summary>Shows progress (disables the button) or, with null, ends it; <paramref name="note"/> is the outcome.</summary>
        internal void SetBusy(bool isBusy, string note)
        {
            busy = isBusy;
            realButton.IsEnabled = !isBusy && realSizesAvailable;
            realHint.Text = note ?? string.Empty;
        }

        private void FromCanvas()
        {
            Sync();
            Changed(Pair, canvas.Start, canvas.End);
        }

        private void FromSlider(double? newPosition, double? newSize)
        {
            if (settingSliders)
            {
                return;
            }

            double s = newPosition.HasValue ? Math.Round(newPosition.Value) / 100 : canvas.Start;
            double length = newSize.HasValue ? Math.Round(newSize.Value) / 100 : canvas.End - canvas.Start;
            canvas.SetSpan(s, s + length);
            SyncText();
            Changed(Pair, canvas.Start, canvas.End);
        }

        // Moves the sliders to the span (widening their range when it lies outside the usual one).
        private void Sync()
        {
            settingSliders = true;
            try
            {
                double s = canvas.Start * 100, length = (canvas.End - canvas.Start) * 100;
                position.Minimum = Math.Min(-150, Math.Floor(s));
                position.Maximum = Math.Max(150, Math.Ceiling(s));
                size.Maximum = Math.Max(300, Math.Ceiling(length));
                position.Value = s;
                size.Value = length;
            }
            finally
            {
                settingSliders = false;
            }

            SyncText();
        }

        private void SyncText()
        {
            double s = canvas.Start, e = canvas.End;
            positionText.Text = (s * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " %";
            sizeText.Text = ((e - s) * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " %";
            string edge = Pair.Horizontal ? "height" : "width";
            summary.Text = s == 0 && e == 1
                ? "Default: the cursor keeps its relative position along the edge."
                : $"{Pair.To}'s screen covers {s * 100:0} % to {e * 100:0} % of {Pair.From}'s {edge}.";
            resetButton.IsEnabled = !(s == 0 && e == 1);
        }
    }
}
