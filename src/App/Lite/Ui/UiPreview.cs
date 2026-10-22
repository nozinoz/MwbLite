// MwbLite (development): "MwbLite.exe --render-ui <folder>" renders the Settings pages and the pairing window, in
// light and dark mode, to PNG files, using a sample settings file. Used to check the look and for the README images.
using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MouseWithoutBorders.Lite.Ui
{
    internal static class UiPreview
    {
        internal static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            string sample = Path.Combine(dir, "sample-settings.json");
            var cfg = LiteConfig.CreateDefault();
            cfg.Machines = new() { "LAPTOP1", LiteConfig.NormalizeName(Environment.MachineName), "TABLET" };
            cfg.StartWithWindows = true;
            cfg.UseService = true;
            cfg.Save(sample);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001
            foreach (ThemeMode mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                app.ThemeMode = mode;
#pragma warning restore WPF0001
                string suffix = mode == ThemeMode.Light ? "light" : "dark";
                var w = SettingsWindow.Create(sample, _ => { });
                w.Left = -30000;
                w.ShowActivated = false;
                w.ShowInTaskbar = false;
                w.SetResourceReference(Window.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
                w.Show();
                for (int i = 0; i < w.PageCount; i++)
                {
                    w.SelectPage(i);
                    if (w.PageName(i) == "Connection")
                    {
                        w.RunCheckForPreview();
                        Pump(TimeSpan.FromSeconds(12));
                    }

                    Pump(TimeSpan.FromMilliseconds(400));
                    Save(w, Path.Combine(dir, $"settings-{i + 1}-{w.PageName(i).Split(' ')[0].ToLowerInvariant()}-{suffix}.png"));
                }

                w.Close();

                var p = new PairingWindow(host: true, sample) { AutoStart = false, Left = -30000, ShowActivated = false };
                p.SetResourceReference(Window.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
                p.Show();
                _ = p.ConfirmCodeAsync("LAPTOP1", "482915", CancellationToken.None);
                Pump(TimeSpan.FromMilliseconds(400));
                Save(p, Path.Combine(dir, $"pairing-{suffix}.png"));
                p.Close();
            }

            // Progress window with three copies in different states.
            var a = LiteTransfers.Begin(@"C:\x\Holiday photos.mwbbundle.zip", "LAPTOP1", sending: true, total: 840L << 20, socket: null);
            a.Report(512L << 20);
            var b = LiteTransfers.Begin(@"C:\x\Project" + LiteFileBundle.ManifestExtension, "TABLET", sending: true, total: 0, socket: null);
            b.Preparing = true;
            var c = LiteTransfers.Begin(@"C:\x\Report.pdf", "LAPTOP1", sending: false, total: 12L << 20, socket: null);
            c.Report(12L << 20);
            c.Succeeded = true;
            c.EndedUtc = DateTime.UtcNow;
            foreach (bool light in new[] { true, false })
            {
                using System.Drawing.Bitmap bm = TransferWindow.Snapshot(light);
                bm.Save(Path.Combine(dir, $"progress-{(light ? "light" : "dark")}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            a.EndedUtc = b.EndedUtc = DateTime.UtcNow;
            app.Shutdown();
            return 0;
        }

        private static void Pump(TimeSpan time)
        {
            DateTime end = DateTime.UtcNow + time;
            while (DateTime.UtcNow < end)
            {
                var frame = new DispatcherFrame();
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(20);
            }
        }

        private static void Save(Window w, string path)
        {
            var content = (FrameworkElement)w.Content;
            double scale = 1.25;
            var bmp = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle((Brush)w.Background ?? Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }

            bmp.Render(visual);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using FileStream f = File.Create(path);
            png.Save(f);
        }
    }
}
