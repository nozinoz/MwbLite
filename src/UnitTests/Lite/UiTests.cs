// MwbLite: tests for the 0.7 look: tray dot colours, the artwork, the layout model's rename, and Setup's
// "Start MwbLite when I sign in" (--enable-startup).
using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;
using MouseWithoutBorders.Lite.Ui;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class UiTests
{
    private static StatusSnapshot With(params PcState[] others)
    {
        var s = new StatusSnapshot();
        s.Pcs.Add(new PcStatus { Name = "ME", State = PcState.ThisPc });
        for (int i = 0; i < others.Length; i++)
        {
            s.Pcs.Add(new PcStatus { Name = "PC" + i, State = others[i] });
        }

        return s;
    }

    [TestMethod]
    public void TrayDotFollowsTheWorstPc()
    {
        Assert.AreEqual(TrayDot.None, LiteArt.DotFor(With()));
        Assert.AreEqual(TrayDot.Green, LiteArt.DotFor(With(PcState.Connected, PcState.Connected)));
        Assert.AreEqual(TrayDot.Amber, LiteArt.DotFor(With(PcState.Connected, PcState.Connecting)));
        Assert.AreEqual(TrayDot.Red, LiteArt.DotFor(With(PcState.Connecting, PcState.NotConnected)));
        Assert.AreEqual(TrayDot.Red, LiteArt.DotFor(With(PcState.Connected, PcState.KeyMismatch)));
        Assert.AreEqual(TrayDot.Red, LiteArt.DotFor(With(PcState.OlderVersion)));
    }

    [TestMethod]
    public void ArtRendersAtEverySizeAndState()
    {
        foreach (int size in new[] { 16, 20, 24, 32, 48, 64 })
        {
            foreach (TrayDot dot in Enum.GetValues<TrayDot>())
            {
                using var bm = LiteArt.TrayBitmap(size, lightTaskbar: size % 2 == 0, dot, activity: dot == TrayDot.Green, allPcsMode: dot == TrayDot.Red);
                Assert.AreEqual(size, bm.Width);
                if (dot != TrayDot.None)
                {
                    // The dot sits bottom-right, in its colour.
                    var pixel = bm.GetPixel(size * 25 / 32, size * 25 / 32);
                    Assert.AreEqual(LiteArt.ColorOf(dot).ToArgb(), pixel.ToArgb(), $"{dot} at {size}px");
                }
            }
        }

        using var logo = LiteArt.LogoBitmap(256);
        Assert.AreEqual(255, logo.GetPixel(128, 6).A, "the tile is opaque");
        using var banner = LiteArt.BannerBitmap(1120, 200);
        Assert.AreEqual(1120, banner.Width);

        string ico = Path.Combine(Path.GetTempPath(), "MwbLiteTest-" + Guid.NewGuid().ToString("N") + ".ico");
        try
        {
            LiteArt.WriteIco(ico, new[] { 16, 32, 256 }, LiteArt.LogoBitmap);
            using var icon = new System.Drawing.Icon(ico, 32, 32);
            Assert.AreEqual(32, icon.Width);
        }
        finally
        {
            File.Delete(ico);
        }
    }

    [TestMethod]
    public void RenamingAndSelectionInTheLayoutModel()
    {
        var model = new LayoutModel { Slots = new[] { "a", "", "", "" }, ThisPc = "A" };
        int changes = 0;
        model.LayoutChanged += (_, _) => changes++;

        model.SetName(1, " laptop1 ");
        CollectionAssert.AreEqual(new[] { "A", "LAPTOP1", "", "" }, model.Slots);
        model.SetName(1, "LAPTOP1"); // unchanged: no event
        Assert.AreEqual(1, changes);
        Assert.IsTrue(model.IsThisPc(0));
        Assert.IsFalse(model.IsThisPc(1));

        model.Selected = 9;
        Assert.AreEqual(3, model.Selected, "clamped to the last slot");
        model.OneRow = false;
        Assert.AreEqual(2, model.Rows);
    }

    [TestMethod]
    public void EnableStartupTurnsTheSettingOnAndKeepsTheKey()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MwbLiteStartup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            Assert.IsTrue(LiteCommands.EnableStartupInConfig(path), "created with defaults, then turned on");
            var cfg = LiteConfig.TryLoad(path, out var errors, out _)!;
            Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
            Assert.IsTrue(cfg.StartWithWindows);
            string key = cfg.SecurityKey;

            Assert.IsFalse(LiteCommands.EnableStartupInConfig(path), "already on: not rewritten");
            Assert.AreEqual(key, LiteConfig.TryLoad(path, out _, out _)!.SecurityKey);

            File.WriteAllText(path, "{ \"securityKey\": \"dpapi:AQIDBAUGBwgJ\", \"machines\": [\"PC1\"] }");
            Assert.IsFalse(LiteCommands.EnableStartupInConfig(path), "a key this account can't read is left alone");
            StringAssert.Contains(File.ReadAllText(path), "dpapi:AQIDBAUGBwgJ");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
