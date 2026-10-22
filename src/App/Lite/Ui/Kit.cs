// MwbLite: small building blocks for the WPF windows (Settings, Pairing), in the style of Windows 11 Settings:
// page titles, cards, setting rows (title + description on the left, control on the right) and toggle switches.
// Colours and styles come from WPF's Fluent theme (Application.ThemeMode = System), so light and dark mode follow
// Windows.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

using WpfButton = System.Windows.Controls.Button;

namespace MouseWithoutBorders.Lite.Ui
{
    internal static class Kit
    {
        internal const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

        private static readonly Lazy<Style> SwitchStyle = new(() => (Style)XamlReader.Parse(
            """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="CheckBox">
              <Setter Property="Cursor" Value="Hand" />
              <Setter Property="FocusVisualStyle" Value="{x:Null}" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="CheckBox">
                    <StackPanel Orientation="Horizontal" Background="Transparent">
                      <TextBlock x:Name="State" Text="Off" Width="28" VerticalAlignment="Center"
                                 Foreground="{DynamicResource TextFillColorPrimaryBrush}" />
                      <Grid Width="40" Height="20" VerticalAlignment="Center">
                        <Border x:Name="Track" CornerRadius="10" BorderThickness="1"
                                BorderBrush="{DynamicResource ControlStrongStrokeColorDefaultBrush}"
                                Background="{DynamicResource ControlAltFillColorSecondaryBrush}" />
                        <Ellipse x:Name="Thumb" Width="12" Height="12" HorizontalAlignment="Left" Margin="4,0,0,0"
                                 Fill="{DynamicResource TextFillColorSecondaryBrush}" />
                      </Grid>
                    </StackPanel>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="State" Property="Text" Value="On" />
                        <Setter TargetName="Track" Property="Background" Value="{DynamicResource AccentFillColorDefaultBrush}" />
                        <Setter TargetName="Track" Property="BorderBrush" Value="{DynamicResource AccentFillColorDefaultBrush}" />
                        <Setter TargetName="Thumb" Property="Fill" Value="{DynamicResource TextOnAccentFillColorPrimaryBrush}" />
                        <Setter TargetName="Thumb" Property="HorizontalAlignment" Value="Right" />
                        <Setter TargetName="Thumb" Property="Margin" Value="0,0,4,0" />
                      </Trigger>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Thumb" Property="Width" Value="14" />
                        <Setter TargetName="Thumb" Property="Height" Value="14" />
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="Track" Property="BorderThickness" Value="2" />
                      </Trigger>
                      <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Opacity" Value="0.45" />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """));

        /// <summary>Sidebar item like Windows 11 Settings: rounded highlight with an accent pill on the selected page.</summary>
        internal static readonly Lazy<Style> NavItemStyle = new(() => (Style)XamlReader.Parse(
            """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="ListBoxItem">
              <Setter Property="FocusVisualStyle" Value="{x:Null}" />
              <Setter Property="Margin" Value="0,1,0,1" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="Bg" CornerRadius="5" Background="Transparent" Padding="10,2,10,2">
                      <Grid>
                        <Border x:Name="Pill" Width="3" Height="16" CornerRadius="1.5" HorizontalAlignment="Left"
                                Margin="-10,0,0,0" Visibility="Collapsed"
                                Background="{DynamicResource AccentFillColorDefaultBrush}" />
                        <ContentPresenter />
                      </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Bg" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                      </Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="Bg" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                        <Setter TargetName="Pill" Property="Visibility" Value="Visible" />
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="Bg" Property="BorderThickness" Value="1" />
                        <Setter TargetName="Bg" Property="BorderBrush" Value="{DynamicResource TextFillColorPrimaryBrush}" />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """));

        internal static TextBlock PageTitle(string text) => new()
        {
            Text = text,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 18),
        };

        internal static TextBlock Section(string text) => new()
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 8),
        };

        internal static TextBlock Text(string text, double size = 14) => new()
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        };

        internal static TextBlock Hint(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            return t;
        }

        /// <summary>A rounded card, like the groups in Windows Settings.</summary>
        internal static Border Card(UIElement content, Thickness? margin = null)
        {
            var b = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 14, 18, 14),
                Margin = margin ?? new Thickness(0, 0, 0, 4),
            };
            b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            return b;
        }

        /// <summary>
        /// A setting: icon, title and description on the left, its control on the right (or, with
        /// <paramref name="below"/>, under the description, for wide controls).
        /// </summary>
        internal static Border Row(string icon, string title, string description, UIElement control, bool below = false)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (icon != null)
            {
                var glyph = Icon(icon, 18);
                glyph.Margin = new Thickness(0, 0, 16, 0);
                grid.Children.Add(glyph);
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(Text(title));
            if (!string.IsNullOrEmpty(description))
            {
                text.Children.Add(Hint(description));
            }

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (control is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                if (below)
                {
                    fe.HorizontalAlignment = HorizontalAlignment.Left;
                    fe.Margin = new Thickness(0, 10, 0, 0);
                }
            }

            if (below)
            {
                Grid.SetColumn(control, 1);
                Grid.SetColumnSpan(control, 2);
                Grid.SetRow(control, 1);
            }
            else
            {
                Grid.SetColumn(control, 2);
            }

            grid.Children.Add(control);
            return Card(grid);
        }

        internal static TextBlock Icon(string glyph, double size = 16) => new()
        {
            Text = glyph,
            FontFamily = new FontFamily(IconFont),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
        };

        internal static CheckBox Switch() => new() { Style = SwitchStyle.Value };

        internal static WpfButton Button(string text, RoutedEventHandler click, bool accent = false)
        {
            var b = new WpfButton { Content = text, MinWidth = 96, Padding = new Thickness(14, 5, 14, 6), Margin = new Thickness(6, 0, 0, 0) };
            if (accent)
            {
                b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            }

            b.Click += click;
            return b;
        }

        internal static StackPanel Stack(params UIElement[] children)
        {
            var s = new StackPanel();
            foreach (UIElement c in children)
            {
                s.Children.Add(c);
            }

            return s;
        }

        internal static StackPanel Horizontal(params UIElement[] children)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (UIElement c in children)
            {
                s.Children.Add(c);
            }

            return s;
        }

        /// <summary>A filled status dot (green / amber / red / grey).</summary>
        internal static System.Windows.Shapes.Ellipse Dot(string brushKey)
        {
            var e = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
            return e;
        }

        internal static string DotBrushKey(PcState state) => state switch
        {
            PcState.Connected => "SystemFillColorSuccessBrush",
            PcState.Connecting => "SystemFillColorCautionBrush",
            PcState.ThisPc => "AccentFillColorDefaultBrush",
            _ => "SystemFillColorCriticalBrush",
        };

        /// <summary>The app glyph (white on blue tile) as a WPF image.</summary>
        internal static Image Logo(int size)
        {
            using System.Drawing.Bitmap bm = LiteArt.LogoBitmap(size * 2);
            using var png = new System.IO.MemoryStream();
            bm.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            png.Position = 0;
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(png, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            System.Windows.Media.Imaging.BitmapFrame src = decoder.Frames[0];
            src.Freeze();
            return new Image { Source = src, Width = size, Height = size };
        }
    }
}
