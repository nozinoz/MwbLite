// MwbLite: the app's artwork, drawn in code so it is crisp at every size and display scaling.
//
// One glyph (two screens, the front one with a stand) is used for the tray icon (white or black to match the
// taskbar, plus a status dot), the app icon (white on a blue tile) and the Settings window. tools\make-art.ps1 runs
// "MwbLite.exe --make-art" to regenerate Logo.ico and the installer images from it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace MouseWithoutBorders.Lite.Ui
{
    internal enum TrayDot
    {
        None,
        Green,
        Amber,
        Red,
    }

    internal static class LiteArt
    {
        internal static readonly Color Green = Color.FromArgb(0x10, 0xB0, 0x48);
        internal static readonly Color Amber = Color.FromArgb(0xF7, 0xA8, 0x00);
        internal static readonly Color Red = Color.FromArgb(0xE0, 0x32, 0x2E);
        internal static readonly Color Accent = Color.FromArgb(0x00, 0x67, 0xC0);
        internal static readonly Color AccentLight = Color.FromArgb(0x2D, 0x8C, 0xE8);
        internal static readonly Color Activity = Color.FromArgb(0x4C, 0xC2, 0xFF);

        /// <summary>The status dot for a status snapshot.</summary>
        internal static TrayDot DotFor(StatusSnapshot s)
        {
            var others = s.Pcs.Where(p => p.State != PcState.ThisPc).ToList();
            if (others.Count == 0)
            {
                return TrayDot.None;
            }

            if (others.Any(p => p.State is PcState.NotConnected or PcState.KeyMismatch or PcState.OlderVersion))
            {
                return TrayDot.Red;
            }

            return others.Any(p => p.State == PcState.Connecting) ? TrayDot.Amber : TrayDot.Green;
        }

        internal static Color ColorOf(TrayDot dot) => dot switch
        {
            TrayDot.Green => Green,
            TrayDot.Amber => Amber,
            TrayDot.Red => Red,
            _ => Color.Transparent,
        };

        /// <summary>
        /// Tray icon bitmap. <paramref name="lightTaskbar"/> picks a dark glyph; <paramref name="activity"/> adds a small
        /// pip (clipboard/file activity); <paramref name="allPcsMode"/> outlines the icon in red (keys go to all PCs).
        /// </summary>
        internal static Bitmap TrayBitmap(int size, bool lightTaskbar, TrayDot dot, bool activity, bool allPcsMode)
        {
            var bm = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bm);
            Prepare(g);
            float u = size / 32f;
            Color ink = lightTaskbar ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.White;

            DrawGlyph(g, u, ink, dot != TrayDot.None);

            if (dot != TrayDot.None)
            {
                // Dot bottom-right, with a transparent gap around it so it reads on any glyph colour.
                var r = new RectangleF(18.5f * u, 18.5f * u, 13f * u, 13f * u);
                Cut(g, Inflate(r, 1.6f * u));
                using var b = new SolidBrush(ColorOf(dot));
                g.FillEllipse(b, r);
            }

            if (activity)
            {
                var r = new RectangleF(0.5f * u, 0.5f * u, 9f * u, 9f * u);
                Cut(g, Inflate(r, 1.2f * u));
                using var b = new SolidBrush(Activity);
                g.FillEllipse(b, r);
            }

            if (allPcsMode)
            {
                using var p = new Pen(Red, Math.Max(1f, 2f * u));
                g.DrawRectangle(p, p.Width / 2, p.Width / 2, size - p.Width, size - p.Width);
            }

            return bm;
        }

        /// <summary>App icon / logo: a blue rounded tile with the white glyph.</summary>
        internal static Bitmap LogoBitmap(int size)
        {
            var bm = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bm);
            Prepare(g);
            float u = size / 32f;
            var tile = new RectangleF(0.5f * u, 0.5f * u, 31f * u, 31f * u);
            using (GraphicsPath path = RoundRect(tile, 7f * u))
            using (var fill = new LinearGradientBrush(tile, AccentLight, Accent, LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(fill, path);
            }

            // Glyph drawn on its own layer (its overlap cut-out must not punch through the tile), then inset.
            int gs = Math.Max(8, (int)Math.Round(size * 0.70));
            using (var glyph = new Bitmap(gs, gs, PixelFormat.Format32bppArgb))
            {
                using (Graphics gg = Graphics.FromImage(glyph))
                {
                    Prepare(gg);
                    DrawGlyph(gg, gs / 32f, Color.White, roomForDot: false);
                }

                g.DrawImage(glyph, (size - gs) / 2f, (size - gs) / 2f + (0.4f * u), gs, gs);
            }

            return bm;
        }

        /// <summary>The two-screens glyph on a 32-unit grid. With roomForDot the front stand is shortened.</summary>
        private static void DrawGlyph(Graphics g, float u, Color ink, bool roomForDot)
        {
            float w = Math.Max(2.6f * u, 1.5f); // at least 1.5 px, so the 16 px tray icon stays legible
            using var pen = new Pen(ink, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

            // Back screen (top right).
            var back = new RectangleF(12f * u, 3.5f * u, 17.5f * u, 12.5f * u);
            using (GraphicsPath p = RoundRect(back, 2.2f * u))
            {
                g.DrawPath(pen, p);
            }

            // Front screen (bottom left) hides the back one where they overlap.
            var front = new RectangleF(2.5f * u, 10f * u, 19f * u, 13.5f * u);
            using (GraphicsPath p = RoundRect(Inflate(front, w / 2), 2.2f * u + (w / 2)))
            {
                Cut(g, p);
            }

            using (GraphicsPath p = RoundRect(front, 2.2f * u))
            {
                g.DrawPath(pen, p);
            }

            // Stand.
            float cx = front.X + (front.Width / 2);
            g.DrawLine(pen, cx, front.Bottom + (1.5f * u), cx, 27f * u);
            g.DrawLine(pen, cx - (roomForDot ? 3.5f : 5f) * u, 28.3f * u, cx + (roomForDot ? 3.5f : 5f) * u, 28.3f * u);
        }

        // ---- icon files ---------------------------------------------------------------------------------------

        /// <summary>A .ico with PNG frames (Windows Vista and later read these).</summary>
        internal static void WriteIco(string path, IEnumerable<int> sizes, Func<int, Bitmap> render)
        {
            var frames = sizes.Select(s =>
            {
                using Bitmap bm = render(s);
                using var ms = new MemoryStream();
                bm.Save(ms, ImageFormat.Png);
                return (Size: s, Png: ms.ToArray());
            }).ToList();

            using var f = new BinaryWriter(File.Create(path));
            f.Write((short)0);
            f.Write((short)1);
            f.Write((short)frames.Count);
            int offset = 6 + (16 * frames.Count);
            foreach (var (size, png) in frames)
            {
                f.Write((byte)(size >= 256 ? 0 : size));
                f.Write((byte)(size >= 256 ? 0 : size));
                f.Write((byte)0);
                f.Write((byte)0);
                f.Write((short)1);
                f.Write((short)32);
                f.Write(png.Length);
                f.Write(offset);
                offset += png.Length;
            }

            foreach (var (_, png) in frames)
            {
                f.Write(png);
            }
        }

        /// <summary>Installer header: blue gradient with the glyph, the name and the tagline (drawn at 2× for sharpness).</summary>
        internal static Bitmap BannerBitmap(int width, int height)
        {
            var bm = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bm);
            Prepare(g);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var all = new RectangleF(0, 0, width, height);
            using (var fill = new LinearGradientBrush(all, AccentLight, Accent, LinearGradientMode.Horizontal))
            {
                g.FillRectangle(fill, all);
            }

            float h = height;
            int icon = (int)(h * 0.46);
            using (var glyph = new Bitmap(icon, icon, PixelFormat.Format32bppArgb))
            {
                using (Graphics gg = Graphics.FromImage(glyph))
                {
                    Prepare(gg);
                    DrawGlyph(gg, icon / 32f, Color.White, roomForDot: false);
                }

                g.DrawImage(glyph, h * 0.26f, (h - icon) / 2f, icon, icon);
            }

            float x = (h * 0.26f) + icon + (h * 0.16f);
            using var title = new Font("Segoe UI Variable Display", h * 0.22f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var tagline = new Font("Segoe UI Variable Text", h * 0.12f, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString("MwbLite", title, Brushes.White, x, h * 0.22f);
            using var soft = new SolidBrush(Color.FromArgb(225, 255, 255, 255));
            g.DrawString("One keyboard and mouse for all your PCs", tagline, soft, x + (h * 0.01f), h * 0.53f);
            return bm;
        }

        /// <summary>"--make-art &lt;repo&gt;": regenerates the committed icon and installer images.</summary>
        internal static void MakeArt(string repo)
        {
            WriteIco(Path.Combine(repo, "src", "App", "Logo.ico"), new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, LogoBitmap);
            string assets = Path.Combine(repo, "installer", "Assets");
            Directory.CreateDirectory(assets);
            using (Bitmap logo = LogoBitmap(64))
            {
                logo.Save(Path.Combine(assets, "logo.png"), ImageFormat.Png);
            }

            using (Bitmap logo = LogoBitmap(128))
            {
                logo.Save(Path.Combine(assets, "logo-128.png"), ImageFormat.Png);
            }

            using (Bitmap banner = BannerBitmap(1120, 200))
            {
                banner.Save(Path.Combine(assets, "banner.png"), ImageFormat.Png);
            }
        }

        /// <summary>A sheet of every tray state at several sizes, on dark and light taskbars (for checking the art).</summary>
        internal static void PreviewSheet(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 64 };
            var states = new (TrayDot Dot, bool Activity, bool All)[]
            {
                (TrayDot.None, false, false), (TrayDot.Green, false, false), (TrayDot.Amber, false, false),
                (TrayDot.Red, false, false), (TrayDot.Green, true, false), (TrayDot.Green, false, true),
            };
            int cell = 76;
            using var sheet = new Bitmap(cell * states.Length, cell * sizes.Length * 2 + cell);
            using Graphics g = Graphics.FromImage(sheet);
            g.Clear(Color.Gray);
            for (int light = 0; light < 2; light++)
            {
                for (int si = 0; si < sizes.Length; si++)
                {
                    int y = ((light * sizes.Length) + si) * cell;
                    using (var bg = new SolidBrush(light == 1 ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x20, 0x20, 0x20)))
                    {
                        g.FillRectangle(bg, 0, y, sheet.Width, cell);
                    }

                    for (int i = 0; i < states.Length; i++)
                    {
                        using Bitmap b = TrayBitmap(sizes[si], light == 1, states[i].Dot, states[i].Activity, states[i].All);
                        g.DrawImageUnscaled(b, (i * cell) + ((cell - sizes[si]) / 2), y + ((cell - sizes[si]) / 2));
                    }
                }
            }

            using (Bitmap logo = LogoBitmap(64))
            {
                g.DrawImageUnscaled(logo, 6, (sizes.Length * 2 * cell) + 6);
            }

            using (Bitmap logo = LogoBitmap(32))
            {
                g.DrawImageUnscaled(logo, 90, (sizes.Length * 2 * cell) + 22);
            }

            using (Bitmap logo = LogoBitmap(16))
            {
                g.DrawImageUnscaled(logo, 140, (sizes.Length * 2 * cell) + 30);
            }

            sheet.Save(path, ImageFormat.Png);
        }

        // ---- helpers ------------------------------------------------------------------------------------------

        private static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
        }

        private static void Cut(Graphics g, RectangleF ellipse)
        {
            using var p = new GraphicsPath();
            p.AddEllipse(ellipse);
            Cut(g, p);
        }

        private static void Cut(Graphics g, GraphicsPath p)
        {
            CompositingMode old = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            using var clear = new SolidBrush(Color.Transparent);
            g.FillPath(clear, p);
            g.CompositingMode = old;
        }

        private static RectangleF Inflate(RectangleF r, float by) => new(r.X - by, r.Y - by, r.Width + (2 * by), r.Height + (2 * by));

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
