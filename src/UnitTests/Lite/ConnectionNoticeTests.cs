// MwbLite: tests for when a "PC disconnected" / "PC is back" notice is shown (fake clock), the diagnostics bundle's
// redaction and the zip it writes.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class ConnectionNoticeTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private static PcStatus[] Pcs(PcState laptop, PcState tablet = PcState.NotConnected) => new[]
    {
        new PcStatus { Name = "DESK-PC", State = PcState.ThisPc },
        new PcStatus { Name = "LAPTOP", State = laptop },
        new PcStatus { Name = "TABLET", State = tablet },
    };

    private static DateTime At(double seconds) => Start.AddSeconds(seconds);

    [TestMethod]
    public void DropIsReportedOnlyAfterTwentySecondsAndOnlyOnce()
    {
        var n = new LiteConnectionNotices(Start);
        Assert.AreEqual(0, n.Update(At(200), Pcs(PcState.Connected), true).Count);

        Assert.AreEqual(0, n.Update(At(210), Pcs(PcState.NotConnected), true).Count);
        Assert.AreEqual(0, n.Update(At(229), Pcs(PcState.NotConnected), true).Count, "19 s is a blip");
        CollectionAssert.AreEqual(new[] { "LAPTOP disconnected" }, n.Update(At(230), Pcs(PcState.NotConnected), true));
        Assert.AreEqual(0, n.Update(At(300), Pcs(PcState.NotConnected), true).Count, "once per drop");
    }

    [TestMethod]
    public void ABlipThatReconnectsInTimeSaysNothingAtAll()
    {
        var n = new LiteConnectionNotices(Start);
        _ = n.Update(At(200), Pcs(PcState.Connected), true);
        Assert.AreEqual(0, n.Update(At(205), Pcs(PcState.Connecting), true).Count);
        Assert.AreEqual(0, n.Update(At(215), Pcs(PcState.Connected), true).Count, "no 'is back' without a 'disconnected'");
        Assert.AreEqual(0, n.Update(At(300), Pcs(PcState.Connected), true).Count);
    }

    [TestMethod]
    public void AttemptsDoNotRestartTheTwentySeconds()
    {
        var n = new LiteConnectionNotices(Start);
        _ = n.Update(At(200), Pcs(PcState.Connected), true);
        _ = n.Update(At(205), Pcs(PcState.NotConnected), true);
        _ = n.Update(At(215), Pcs(PcState.Connecting), true); // a redial in progress
        _ = n.Update(At(224), Pcs(PcState.NotConnected), true);
        CollectionAssert.AreEqual(new[] { "LAPTOP disconnected" }, n.Update(At(226), Pcs(PcState.KeyMismatch), true));
    }

    [TestMethod]
    public void ReturnAfterANoticeSaysIsBackOnceThenWatchesAgain()
    {
        var n = new LiteConnectionNotices(Start);
        _ = n.Update(At(200), Pcs(PcState.Connected), true);
        _ = n.Update(At(210), Pcs(PcState.NotConnected), true);
        _ = n.Update(At(231), Pcs(PcState.NotConnected), true);

        CollectionAssert.AreEqual(new[] { "LAPTOP is back" }, n.Update(At(400), Pcs(PcState.Connected), true));
        Assert.AreEqual(0, n.Update(At(401), Pcs(PcState.Connected), true).Count);

        _ = n.Update(At(500), Pcs(PcState.NotConnected), true);
        CollectionAssert.AreEqual(new[] { "LAPTOP disconnected" }, n.Update(At(521), Pcs(PcState.NotConnected), true), "the next drop is reported too");
    }

    [TestMethod]
    public void APcThatNeverConnectedIsNeverReported()
    {
        var n = new LiteConnectionNotices(Start);
        for (int s = 200; s < 1000; s += 10)
        {
            Assert.AreEqual(0, n.Update(At(s), Pcs(PcState.Connected), true).Count(m => m.StartsWith("TABLET", StringComparison.Ordinal)));
        }

        // Connected only after the start: the TABLET has been off all along, LAPTOP is fine.
        Assert.AreEqual(0, n.Update(At(1000), Pcs(PcState.Connected, PcState.Connecting), true).Count);
    }

    [TestMethod]
    public void NothingForTwoMinutesAfterStartAndDropsInThatTimeAreForgotten()
    {
        var n = new LiteConnectionNotices(Start);
        Assert.AreEqual(0, n.Update(At(5), Pcs(PcState.Connected), true).Count);
        Assert.AreEqual(0, n.Update(At(30), Pcs(PcState.NotConnected), true).Count);
        Assert.AreEqual(0, n.Update(At(119), Pcs(PcState.NotConnected), true).Count);
        Assert.AreEqual(0, n.Update(At(121), Pcs(PcState.NotConnected), true).Count, "it was not connected at the end of the quiet time, so it was never really connected");
        Assert.AreEqual(0, n.Update(At(300), Pcs(PcState.Connected), true).Count, "and its return is not news either");
    }

    [TestMethod]
    public void SleepOrWakeStartsANewQuietTimeAndForgetsWhoWasConnected()
    {
        var n = new LiteConnectionNotices(Start);
        _ = n.Update(At(300), Pcs(PcState.Connected, PcState.Connected), true);

        n.PowerChanged(At(400)); // resume
        Assert.AreEqual(0, n.Update(At(402), Pcs(PcState.NotConnected, PcState.NotConnected), true).Count);
        Assert.AreEqual(0, n.Update(At(519), Pcs(PcState.NotConnected, PcState.NotConnected), true).Count);
        Assert.AreEqual(0, n.Update(At(600), Pcs(PcState.NotConnected, PcState.NotConnected), true).Count, "they were not connected after waking, so there is nothing to report");

        _ = n.Update(At(700), Pcs(PcState.Connected), true);
        _ = n.Update(At(710), Pcs(PcState.NotConnected), true);
        CollectionAssert.AreEqual(new[] { "LAPTOP disconnected" }, n.Update(At(730), Pcs(PcState.NotConnected), true), "normal service resumes");
    }

    [TestMethod]
    public void AReturnInsideTheQuietTimeAfterWakingIsNotReported()
    {
        var n = new LiteConnectionNotices(Start);
        _ = n.Update(At(300), Pcs(PcState.Connected), true);
        _ = n.Update(At(310), Pcs(PcState.NotConnected), true);
        CollectionAssert.AreEqual(new[] { "LAPTOP disconnected" }, n.Update(At(331), Pcs(PcState.NotConnected), true));

        n.PowerChanged(At(340));
        Assert.AreEqual(0, n.Update(At(350), Pcs(PcState.Connected), true).Count);
    }

    [TestMethod]
    public void SeveralPcsAreCombinedIntoOneNotice()
    {
        var n = new LiteConnectionNotices(Start);
        _ = n.Update(At(200), Pcs(PcState.Connected, PcState.Connected), true);
        _ = n.Update(At(210), Pcs(PcState.NotConnected, PcState.NotConnected), true);
        CollectionAssert.AreEqual(new[] { "LAPTOP and TABLET disconnected" }, n.Update(At(231), Pcs(PcState.NotConnected, PcState.NotConnected), true));
        CollectionAssert.AreEqual(new[] { "LAPTOP and TABLET are back" }, n.Update(At(300), Pcs(PcState.Connected, PcState.Connected), true));
    }

    [TestMethod]
    public void TurnedOffMeansNoDisconnectedAndNoBackNotice()
    {
        var n = new LiteConnectionNotices(Start);
        _ = n.Update(At(200), Pcs(PcState.Connected), false);
        _ = n.Update(At(210), Pcs(PcState.NotConnected), false);
        Assert.AreEqual(0, n.Update(At(240), Pcs(PcState.NotConnected), false).Count);
        Assert.AreEqual(0, n.Update(At(300), Pcs(PcState.Connected), false).Count);

        // Switched on again: the next drop is reported as usual.
        _ = n.Update(At(400), Pcs(PcState.NotConnected), true);
        CollectionAssert.AreEqual(new[] { "LAPTOP disconnected" }, n.Update(At(421), Pcs(PcState.NotConnected), true));
    }

    [TestMethod]
    public void APcRemovedFromTheLayoutIsDropped()
    {
        var n = new LiteConnectionNotices(Start);
        _ = n.Update(At(200), Pcs(PcState.Connected), true);
        _ = n.Update(At(210), new[] { new PcStatus { Name = "DESK-PC", State = PcState.ThisPc } }, true);
        _ = n.Update(At(220), Pcs(PcState.NotConnected), true); // added again, but never seen connected since
        Assert.AreEqual(0, n.Update(At(300), Pcs(PcState.NotConnected), true).Count);
    }
}

[TestClass]
public sealed class DiagnosticsBundleTests
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

    private const string Secret = "my-very-secret-pairing-key";
    private const string Blob = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA";

    [TestMethod]
    public void RedactionRemovesPlainAndProtectedKeysAndKeepsTheRest()
    {
        string json = $$"""
            {
              // my notes
              "securityKey": "dpapi:{{Blob}}",
              "SecurityKey" : "{{Secret}}",
              "machines": ["DESK-PC", "LAPTOP"],
              "other": "dpapi:{{Blob}}",
              "advanced": { "tcpPort": 15100, "name2Ip": ["LAPTOP 192.168.1.21"] }
            }
            """;

        string redacted = LiteDiagnosticsBundle.RedactSettings(json);

        Assert.IsFalse(redacted.Contains(Secret, StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains(Blob, StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("\"securityKey\": \"<redacted>\"", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("\"other\": \"dpapi:<redacted>\"", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("// my notes", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("\"tcpPort\": 15100", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("LAPTOP 192.168.1.21", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RedactionWorksOnBrokenJsonAndEscapedQuotes()
    {
        string broken = "{ \"securityKey\": \"ab\\\"cd" + Secret + "\", \"machines\": [ \"A\", ";

        string redacted = LiteDiagnosticsBundle.RedactSettings(broken);

        Assert.IsFalse(redacted.Contains(Secret, StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("abcd", StringComparison.Ordinal));
        Assert.IsTrue(redacted.EndsWith("\"A\", ", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, LiteDiagnosticsBundle.RedactSettings(null!));
    }

    [TestMethod]
    public void FirewallRulesAreFilteredToTheOnesMentioningMwbLite()
    {
        string netsh = "\r\nRule Name:                            Core Networking - DNS\r\n----------------------------------------------------------------------\r\nEnabled:                              Yes\r\nDirection:                            Out\r\n"
            + "\r\nRule Name:                            MwbLite (TCP)\r\n----------------------------------------------------------------------\r\nEnabled:                              Yes\r\nDirection:                            In\r\nLocalPort:                            15100-15101\r\nAction:                               Allow\r\n"
            + "\r\nRule Name:                            Some app\r\n----------------------------------------------------------------------\r\nProgram:                              C:\\Program Files\\MwbLite\\mwblite.exe\r\nAction:                               Block\r\n"
            + "\r\nRule Name:                            Another\r\n----------------------------------------------------------------------\r\nEnabled:                              No\r\n";

        string rules = LiteDiagnosticsBundle.FilterFirewallRules(netsh);

        Assert.IsTrue(rules.Contains("MwbLite (TCP)", StringComparison.Ordinal));
        Assert.IsTrue(rules.Contains("LocalPort:                            15100-15101", StringComparison.Ordinal));
        Assert.IsTrue(rules.Contains("Action:                               Block", StringComparison.Ordinal), "found by its program path");
        Assert.IsFalse(rules.Contains("Core Networking", StringComparison.Ordinal));
        Assert.IsFalse(rules.Contains("Another", StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, LiteDiagnosticsBundle.FilterFirewallRules("Rule Name: Other\r\n"));
    }

    [TestMethod]
    public void FileNameHasPcAndMinute()
    {
        Assert.AreEqual("MwbLite-diagnostics-DESK-PC-20260131-0945.zip", LiteDiagnosticsBundle.DefaultFileName("DESK-PC", new DateTime(2026, 1, 31, 9, 45, 59)));
        Assert.AreEqual("MwbLite-diagnostics-PC-20260131-0945.zip", LiteDiagnosticsBundle.DefaultFileName("<>:", new DateTime(2026, 1, 31, 9, 45, 0)));
    }

    [TestMethod]
    public async Task ZipHoldsTheFilesRedactedAndNotesWhatIsMissing()
    {
        string user = Path.Combine(dir, "user");
        Directory.CreateDirectory(Path.Combine(user, "logs"));
        File.WriteAllText(Path.Combine(user, "settings.json"), $"{{ \"securityKey\": \"{Secret}\", \"machines\": [\"DESK-PC\"] }}");
        File.WriteAllText(Path.Combine(user, "status.json"), "{ \"pcs\": [] }");
        File.WriteAllText(Path.Combine(user, "logs", "mwblite.log"), "2026-01-01 [1] LiteTray: hello\r\n");
        string zipPath = Path.Combine(dir, "out.zip");

        var notes = await LiteDiagnosticsBundle.CreateAsync(zipPath, null, new LiteDiagnosticsBundle.Locations(user, Path.Combine(dir, "no-service"), Live: false), CancellationToken.None);

        Assert.IsTrue(File.Exists(zipPath));
        Assert.IsFalse(File.Exists(zipPath + ".tmp"));
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        string[] names = zip.Entries.Select(e => e.FullName).ToArray();
        foreach (string expected in new[] { "settings.json", "status.json", "logs/mwblite.log", "system.txt", "network-adapters.txt", "summary.txt" })
        {
            CollectionAssert.Contains(names, expected);
        }

        string Read(string name) => new StreamReader(zip.GetEntry(name)!.Open()).ReadToEnd();
        Assert.IsFalse(Read("settings.json").Contains(Secret, StringComparison.Ordinal));
        Assert.IsTrue(Read("settings.json").Contains("<redacted>", StringComparison.Ordinal));
        Assert.IsTrue(Read("logs/mwblite.log").Contains("hello", StringComparison.Ordinal));
        Assert.IsTrue(Read("system.txt").Contains("MwbLite " + LiteUpdate.CurrentText, StringComparison.Ordinal));

        Assert.IsTrue(notes.Any(n => n.Contains("update.json", StringComparison.Ordinal)), "a missing file is a note");
        Assert.IsTrue(notes.Any(n => n.Contains("Service files", StringComparison.Ordinal)), "so is an unreadable service folder");
        string summary = Read("summary.txt");
        Assert.IsTrue(summary.Contains("update.json", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains(Secret, StringComparison.Ordinal));
    }
}
