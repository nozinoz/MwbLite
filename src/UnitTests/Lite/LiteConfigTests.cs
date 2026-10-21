// MwbLite: tests for the settings.json loader that replaces the PowerToys settings pipeline.
using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class LiteConfigTests
{
    private string dir = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        dir = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        Directory.Delete(dir, recursive: true);
    }

    private string Write(string json)
    {
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [TestMethod]
    public void MissingFileIsCreatedWithValidDefaultsAndNeverOverwritten()
    {
        string path = Path.Combine(dir, "sub", "settings.json");

        Assert.IsTrue(LiteConfig.WriteDefaultIfMissing(path));
        var cfg = LiteConfig.TryLoad(path, out var errors, out _);
        Assert.IsNotNull(cfg, string.Join("; ", errors));
        Assert.IsTrue(Encryption.IsKeyValid(cfg!.SecurityKey, out _));
        CollectionAssert.AreEqual(new[] { LiteConfig.NormalizeName(Environment.MachineName) }, cfg.Machines);
        Assert.IsTrue(cfg.ShareClipboard);
        Assert.IsTrue(cfg.TransferFile);
        Assert.AreEqual(15100, cfg.Advanced.TcpPort);

        File.WriteAllText(path, "{ \"user\": \"edited\" }");
        Assert.IsFalse(LiteConfig.WriteDefaultIfMissing(path));
        Assert.AreEqual("{ \"user\": \"edited\" }", File.ReadAllText(path));
    }

    [TestMethod]
    public void ParsesCommentsTrailingCommasAndNormalizesNamesAndKey()
    {
        string path = Write("""
            {
              // comment
              "securityKey": "abcd efgh ijkl mnop",
              "machines": ["minipc", " laptop1 ", "", "laptop2",],
              "layout": { "oneRow": false, "wrapMouse": false },
              "shareClipboard": false,
              "advanced": { "easyMouse": "ctrl", "tcpPort": 16000, "name2Ip": ["LAPTOP1 192.168.1.20"] },
            }
            """);

        var cfg = LiteConfig.TryLoad(path, out var errors, out _);

        Assert.IsNotNull(cfg, string.Join("; ", errors));
        Assert.AreEqual("abcdefghijklmnop", cfg!.SecurityKey);
        CollectionAssert.AreEqual(new[] { "MINIPC", "LAPTOP1", string.Empty, "LAPTOP2" }, cfg.MatrixSlots());
        Assert.IsFalse(cfg.Layout.OneRow);
        Assert.IsFalse(cfg.ShareClipboard);
        Assert.AreEqual(EasyMouseOption.Ctrl, cfg.Advanced.EasyMouse);
        Assert.AreEqual(16000, cfg.Advanced.TcpPort);
        Assert.AreEqual("LAPTOP1 192.168.1.20", cfg.Name2IpString());
    }

    [DataTestMethod]
    [DataRow("{ \"securityKey\": \"short\" }", "securityKey")]
    [DataRow("{ \"securityKey\": \"0123456789abcdef\", \"machines\": [\"A\",\"B\",\"C\",\"D\",\"E\"] }", "at most 4")]
    [DataRow("{ \"securityKey\": \"0123456789abcdef\", \"machines\": [\"A\",\"a\"] }", "twice")]
    [DataRow("{ \"securityKey\": \"0123456789abcdef\", \"advanced\": { \"tcpPort\": 80 } }", "tcpPort")]
    [DataRow("{ \"securityKey\": \"0123456789abcdef\", \"advanced\": { \"hotkeys\": { \"reconnect\": \"Ctrl+Nope\" } } }", "reconnect")]
    [DataRow("{ \"securityKey\": \"0123456789abcdef\", \"advanced\": { \"name2Ip\": [\"PC notanip\"] } }", "name2Ip")]
    [DataRow("{ not json", "not valid JSON")]
    public void InvalidConfigIsRejectedWithAReason(string json, string expected)
    {
        var cfg = LiteConfig.TryLoad(Write(json), out var errors, out _);

        Assert.IsNull(cfg);
        Assert.IsTrue(errors.Any(e => e.Contains(expected, StringComparison.OrdinalIgnoreCase)), string.Join("; ", errors));
    }

    [TestMethod]
    public void HotkeysParseToUpstreamDefaults()
    {
        var cfg = LiteConfig.TryLoad(Write("{ \"securityKey\": \"0123456789abcdef\" }"), out var errors, out _);
        Assert.IsNotNull(cfg, string.Join("; ", errors));

        // Upstream MouseWithoutBordersProperties.DefaultHotKeyToggleEasyMouse = (win, ctrl, alt, !shift, 'E').
        var hk = cfg!.Hotkey(cfg.Advanced.Hotkeys.ToggleEasyMouse);
        Assert.IsTrue(hk.Win && hk.Ctrl && hk.Alt && !hk.Shift);
        Assert.AreEqual(0x45, hk.Code);
        Assert.AreEqual(0x4C, cfg.Hotkey(cfg.Advanced.Hotkeys.LockMachine).Code);
        Assert.AreEqual(0x52, cfg.Hotkey(cfg.Advanced.Hotkeys.Reconnect).Code);
        Assert.IsTrue(cfg.Hotkey(cfg.Advanced.Hotkeys.SwitchToAllPcs).IsEmpty());
        Assert.AreEqual((int)VK.F1, cfg.SwitchMachineHotkeyCode());

        Assert.IsTrue(LiteConfig.TryParseHotkey("shift+ctrl+1", out var digit));
        Assert.AreEqual(0x31, digit.Code);
        Assert.IsTrue(digit.Shift && digit.Ctrl);
        Assert.IsFalse(LiteConfig.TryParseHotkey("Ctrl+Alt", out _));
        Assert.IsFalse(LiteConfig.TryParseHotkey("Ctrl+A+B", out _));
    }

    [TestMethod]
    public void SaveRoundTripsKeepsABackupAndPreservesFieldsNotOnTheForm()
    {
        string path = Write("""
            {
              // hand-written comment
              "securityKey": "0123456789abcdef",
              "machines": ["PC1"],
              "advanced": { "easyMouseFullscreenExcludedApps": ["game.exe"] }
            }
            """);
        string original = File.ReadAllText(path);
        Assert.IsTrue(LiteConfig.HasComments(path));

        var cfg = LiteConfig.TryLoad(path, out var errors, out _)!;
        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
        cfg.Machines = new() { "pc1", "pc2" };
        cfg.Advanced.Hotkeys.Reconnect = "Ctrl+Shift+R";
        cfg.Normalize();
        cfg.Save(path);

        Assert.AreEqual(original, File.ReadAllText(path + ".bak"));
        Assert.IsFalse(LiteConfig.HasComments(path));
        Assert.IsFalse(File.Exists(path + ".tmp"));

        var reloaded = LiteConfig.TryLoad(path, out errors, out _);
        Assert.IsNotNull(reloaded, string.Join("; ", errors));
        CollectionAssert.AreEqual(new[] { "PC1", "PC2" }, reloaded!.Machines);
        Assert.AreEqual("Ctrl+Shift+R", reloaded.Advanced.Hotkeys.Reconnect);
        CollectionAssert.AreEqual(new[] { "game.exe" }, reloaded.Advanced.EasyMouseFullscreenExcludedApps);
        Assert.IsTrue(File.ReadAllText(path).Contains("\"Ctrl+Shift+R\"", StringComparison.Ordinal), "'+' must not be escaped");
    }

    [TestMethod]
    public void HasCommentsHandlesByteOrderMarkAndPlainFiles()
    {
        string path = Path.Combine(dir, "bom.json");
        File.WriteAllText(path, "{ /* c */ \"a\": 1 }", new System.Text.UTF8Encoding(true));
        Assert.IsTrue(LiteConfig.HasComments(path));
        File.WriteAllText(path, "{ \"a\": \"// not a comment\" }", new System.Text.UTF8Encoding(true));
        Assert.IsFalse(LiteConfig.HasComments(path));
    }

    [TestMethod]
    public void InvalidValuesStillParseForTheSettingsWindow()
    {
        string path = Write("{ \"securityKey\": \"short\", \"machines\": [\"a\",\"A\"] }");

        var cfg = LiteConfig.TryParse(path, out string error);

        Assert.IsNotNull(cfg, error);
        var errors = cfg!.Validate(out _);
        Assert.AreEqual(2, errors.Count, string.Join("; ", errors));
        Assert.IsNull(LiteConfig.TryParse(Write("{ nope"), out error));
        StringAssert.Contains(error, "not valid JSON");
    }

    [TestMethod]
    public void SettingsFacadeMapsConfigAndPersistsOnlyState()
    {
        string settingsFile = Write("""
            { "securityKey": "0123456789abcdef", "machines": ["PC1", "PC2"], "transferFile": false }
            """);
        string stateFile = Path.Combine(dir, "state.json");
        string before = File.ReadAllText(settingsFile);

        var settings = new Settings(new PassthroughPairingKeyCodec(), settingsFile, stateFile);

        Assert.AreEqual(0, settings.ConfigErrors.Count);
        Assert.AreEqual("PC1,PC2,,", settings.MachineMatrixString);
        Assert.AreEqual("0123456789abcdef", settings.MyKey);
        Assert.IsFalse(settings.TransferFile);
        Assert.IsTrue(settings.ShareClipboard);
        Assert.IsFalse(settings.FirstRun);
        Assert.IsTrue(settings.ShowOriginalUI);

        // Runtime changes stay in memory; settings.json is never rewritten.
        settings.MachineMatrixString = "PC2,PC1,,";
        settings.MyKey = "fedcba9876543210";
        Assert.AreEqual("PC2,PC1,,", settings.MachineMatrixString);
        Assert.AreEqual(before, File.ReadAllText(settingsFile));
    }

    [TestMethod]
    public void InvalidConfigAtStartupFallsBackToARandomKeyWithoutTouchingTheFile()
    {
        string settingsFile = Write("{ \"securityKey\": \"tooshort\" }");

        var settings = new Settings(new PassthroughPairingKeyCodec(), settingsFile, Path.Combine(dir, "state.json"));

        Assert.IsTrue(settings.ConfigErrors.Count > 0);
        Assert.AreNotEqual("tooshort", settings.MyKey);
        Assert.IsTrue(Encryption.IsKeyValid(settings.MyKey, out _));
        Assert.AreEqual("{ \"securityKey\": \"tooshort\" }", File.ReadAllText(settingsFile));
    }
}
