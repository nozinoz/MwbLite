// MwbLite: tests for lining up screens of different sizes: the mapping, the layout neighbours, the config entries and
// the wire formats (the PC-to-PC sync of the entries is exercised through the same helpers).
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class EdgeAlignmentTests
{
    private const int Full = 65535;

    private static EdgeAlignmentEntry Entry(string from, string to, double start, double end) => new() { From = from, To = to, Start = start, End = end };

    private static System.Drawing.Point At(int x, int y) => new(x, y);

    [TestMethod]
    public void ForwardMapsTheFromEdgeOntoTheSmallerToScreen()
    {
        // The "to" screen covers the middle half of the "from" edge.
        Assert.AreEqual(0.0, LiteEdgeAlignment.ToTarget(0.25, 0.25, 0.75), 1e-9);
        Assert.AreEqual(0.5, LiteEdgeAlignment.ToTarget(0.5, 0.25, 0.75), 1e-9);
        Assert.AreEqual(1.0, LiteEdgeAlignment.ToTarget(0.75, 0.25, 0.75), 1e-9);
        Assert.AreEqual(0.0, LiteEdgeAlignment.ToTarget(0.0, 0.25, 0.75), 1e-9, "above the screen clamps to its top");
        Assert.AreEqual(1.0, LiteEdgeAlignment.ToTarget(1.0, 0.25, 0.75), 1e-9, "below the screen clamps to its bottom");
    }

    [TestMethod]
    public void ReverseMapsTheToScreenOntoTheFromEdgeAndClamps()
    {
        Assert.AreEqual(0.25, LiteEdgeAlignment.ToSource(0, 0.25, 0.75), 1e-9);
        Assert.AreEqual(0.5, LiteEdgeAlignment.ToSource(0.5, 0.25, 0.75), 1e-9);
        Assert.AreEqual(0.75, LiteEdgeAlignment.ToSource(1, 0.25, 0.75), 1e-9);

        // A bigger "to" screen (-0.5..1.5): its top and bottom parts have no counterpart on the "from" screen.
        Assert.AreEqual(0.0, LiteEdgeAlignment.ToSource(0.1, -0.5, 1.5), 1e-9);
        Assert.AreEqual(1.0, LiteEdgeAlignment.ToSource(0.9, -0.5, 1.5), 1e-9);
        Assert.AreEqual(0.5, LiteEdgeAlignment.ToSource(0.5, -0.5, 1.5), 1e-9);
    }

    [TestMethod]
    public void NoEntryOrTheDefaultEntryKeepsMwbsPoint()
    {
        var p = At(2, 12345);
        Assert.AreEqual(p, LiteEdgeAlignment.Apply(Array.Empty<EdgeAlignmentEntry>(), "A", "B", EdgeDirection.Right, p));
        Assert.AreEqual(p, LiteEdgeAlignment.Apply(null, "A", "B", EdgeDirection.Right, p));
        Assert.AreEqual(p, LiteEdgeAlignment.Apply(new[] { Entry("A", "B", 0, 1) }, "A", "B", EdgeDirection.Right, p));
        Assert.AreEqual(p, LiteEdgeAlignment.Apply(new[] { Entry("A", "C", 0.2, 0.6) }, "A", "B", EdgeDirection.Right, p), "another pair");
    }

    [TestMethod]
    public void RightAndDownCrossFromTheFirstNameToTheSecond()
    {
        var entries = new[] { Entry("Desk", "laptop", 0.25, 0.75) }; // names compare like layout names

        // Right: the cursor leaves DESK at half height (0.5) -> half way down the laptop (0.5).
        var right = LiteEdgeAlignment.Apply(entries, "DESK", "LAPTOP", EdgeDirection.Right, At(2, Full / 2));
        Assert.AreEqual(2, right.X, "the coordinate across the edge is untouched");
        Assert.AreEqual(Full / 2.0, right.Y, 2);

        // Leaving at 0.25 (where the laptop's top is) -> the laptop's top; above that -> clamps.
        Assert.AreEqual(0, LiteEdgeAlignment.Apply(entries, "DESK", "LAPTOP", EdgeDirection.Right, At(2, Full / 4)).Y, 1);
        Assert.AreEqual(0, LiteEdgeAlignment.Apply(entries, "DESK", "LAPTOP", EdgeDirection.Right, At(2, 0)).Y);
        Assert.AreEqual(Full, LiteEdgeAlignment.Apply(entries, "DESK", "LAPTOP", EdgeDirection.Right, At(2, Full)).Y);

        // Down uses the same entry, on X.
        var down = LiteEdgeAlignment.Apply(entries, "DESK", "LAPTOP", EdgeDirection.Down, At(Full * 3 / 8, 2));
        Assert.AreEqual(2, down.Y);
        Assert.AreEqual(Full / 4.0, down.X, 3);
    }

    [TestMethod]
    public void LeftAndUpCrossFromTheSecondNameBackToTheFirst()
    {
        var entries = new[] { Entry("DESK", "LAPTOP", 0.25, 0.75) };

        // Leaving the laptop's top / middle / bottom comes out at 0.25 / 0.5 / 0.75 of DESK's edge.
        Assert.AreEqual(Full * 0.25, LiteEdgeAlignment.Apply(entries, "LAPTOP", "DESK", EdgeDirection.Left, At(Full - 2, 0)).Y, 1);
        Assert.AreEqual(Full * 0.5, LiteEdgeAlignment.Apply(entries, "LAPTOP", "DESK", EdgeDirection.Left, At(Full - 2, Full / 2)).Y, 1);
        Assert.AreEqual(Full * 0.75, LiteEdgeAlignment.Apply(entries, "LAPTOP", "DESK", EdgeDirection.Left, At(Full - 2, Full)).Y, 1);
        Assert.AreEqual(Full - 2, LiteEdgeAlignment.Apply(entries, "LAPTOP", "DESK", EdgeDirection.Left, At(Full - 2, Full)).X);
        Assert.AreEqual(Full * 0.75, LiteEdgeAlignment.Apply(entries, "LAPTOP", "DESK", EdgeDirection.Up, At(Full, Full - 2)).X, 1);
    }

    [TestMethod]
    public void WrapAroundUsesTheDefaultUnlessThatExactPairHasAnEntry()
    {
        var entries = new[] { Entry("DESK", "LAPTOP", 0.25, 0.75) };
        var p = At(2, 20000);

        // LAPTOP is the last PC: leaving it to the right wraps to DESK, which is not what the entry describes.
        Assert.AreEqual(p, LiteEdgeAlignment.Apply(entries, "LAPTOP", "DESK", EdgeDirection.Right, p));
        // ...and leaving DESK to the left wraps to LAPTOP.
        Assert.AreEqual(p, LiteEdgeAlignment.Apply(entries, "DESK", "LAPTOP", EdgeDirection.Left, p));

        var wrap = new[] { Entry("LAPTOP", "DESK", 0.5, 1.0) };
        Assert.AreNotEqual(p, LiteEdgeAlignment.Apply(wrap, "LAPTOP", "DESK", EdgeDirection.Right, p));
    }

    [TestMethod]
    public void ABiggerToScreenSpreadsTheFromEdgeOverItsMiddle()
    {
        // A 4K monitor twice as tall as the laptop it follows: it covers -0.5..1.5 of the laptop's edge.
        var entries = new[] { Entry("LAPTOP", "MONITOR", -0.5, 1.5) };

        Assert.AreEqual(Full * 0.25, LiteEdgeAlignment.Apply(entries, "LAPTOP", "MONITOR", EdgeDirection.Right, At(2, 0)).Y, 1, "the laptop's top is a quarter down the monitor");
        Assert.AreEqual(Full * 0.75, LiteEdgeAlignment.Apply(entries, "LAPTOP", "MONITOR", EdgeDirection.Right, At(2, Full)).Y, 1);
        Assert.AreEqual(0, LiteEdgeAlignment.Apply(entries, "MONITOR", "LAPTOP", EdgeDirection.Left, At(Full - 2, 100)).Y, "the monitor's top corner has no laptop counterpart: clamps to the laptop's top");
    }

    [TestMethod]
    public void CrossingThereAndBackLandsWhereItStartedInsideTheSharedRange()
    {
        var entries = new[] { Entry("A", "B", 0.1, 0.8) };
        foreach (int y in new[] { 7500, 20000, 40000, 50000 })
        {
            var there = LiteEdgeAlignment.Apply(entries, "A", "B", EdgeDirection.Right, At(2, y));
            var back = LiteEdgeAlignment.Apply(entries, "B", "A", EdgeDirection.Left, there);
            Assert.AreEqual(y, back.Y, 2, "y=" + y);
        }
    }

    [TestMethod]
    public void NeighboursFollowOneRowAndTheGrid()
    {
        var row = LiteEdgeAlignment.Neighbours(new[] { "a", "", "c", "d" }, oneRow: true);
        CollectionAssert.AreEqual(new[] { new EdgePair("A", "C", true), new EdgePair("C", "D", true) }, row, "empty slots are skipped, like MWB does");

        var grid = LiteEdgeAlignment.Neighbours(new[] { "A", "B", "", "D" }, oneRow: false);
        CollectionAssert.AreEqual(new[] { new EdgePair("A", "B", true), new EdgePair("B", "D", false) }, grid);

        Assert.AreEqual(0, LiteEdgeAlignment.Neighbours(new[] { "A" }, oneRow: true).Count);
        Assert.AreEqual(4, LiteEdgeAlignment.Neighbours(new[] { "A", "B", "C", "D" }, oneRow: false).Count);
    }

    [TestMethod]
    public void EntriesForPcsThatAreNoLongerNeighboursAreDroppedFromTheLayoutView()
    {
        var entries = new[] { Entry("A", "B", 0.2, 0.9), Entry("B", "C", 0.1, 0.5), Entry("C", "A", 0, 0.5), Entry("A", "C", 0, 1) };

        var kept = LiteEdgeAlignment.ForLayout(entries, new[] { "A", "B", "", "" }, oneRow: true);

        Assert.AreEqual(1, kept.Count);
        Assert.AreEqual("A", kept[0].From);
        Assert.AreEqual(0.9, kept[0].End);
        Assert.AreEqual(0, LiteEdgeAlignment.ForLayout(entries, new[] { "B", "A", "", "" }, oneRow: true).Count, "A and B swapped places: the old entry no longer applies");
    }

    [TestMethod]
    public void RealSizesCentreTheSmallerScreen()
    {
        var (start, end) = LiteEdgeAlignment.FromRealSizes(400, 200)!.Value;
        Assert.AreEqual(0.25, start, 1e-9);
        Assert.AreEqual(0.75, end, 1e-9);

        var (bigStart, bigEnd) = LiteEdgeAlignment.FromRealSizes(200, 400)!.Value;
        Assert.AreEqual(-0.5, bigStart, 1e-9);
        Assert.AreEqual(1.5, bigEnd, 1e-9);

        var (sameStart, sameEnd) = LiteEdgeAlignment.FromRealSizes(300, 300)!.Value;
        Assert.AreEqual(0, sameStart, 1e-9);
        Assert.AreEqual(1, sameEnd, 1e-9);

        Assert.IsNull(LiteEdgeAlignment.FromRealSizes(0, 300));
        Assert.IsNull(LiteEdgeAlignment.FromRealSizes(300, double.NaN));
        var (_, hugeEnd) = LiteEdgeAlignment.FromRealSizes(10, 100000)!.Value;
        Assert.IsTrue(hugeEnd <= LiteEdgeAlignment.MaxValue, "stays inside the allowed range");
    }

    [DataTestMethod]
    [DataRow("A", "B", 0.0, 1.0, null)]
    [DataRow("A", "B", -4.0, 5.0, null)]
    [DataRow("", "B", 0.0, 1.0, "PC names")]
    [DataRow("A", " ", 0.0, 1.0, "PC names")]
    [DataRow("A", "B", double.NaN, 1.0, "numbers")]
    [DataRow("A", "B", 0.0, double.PositiveInfinity, "numbers")]
    [DataRow("A", "B", -4.01, 1.0, "between")]
    [DataRow("A", "B", 0.0, 5.01, "between")]
    [DataRow("A", "B", 0.5, 0.52, "more than start")]
    [DataRow("A", "B", 0.8, 0.2, "more than start")]
    public void EntriesAreValidated(string from, string to, double start, double end, string? expected)
    {
        string? problem = LiteEdgeAlignment.Problem(Entry(from, to, start, end));

        if (expected == null)
        {
            Assert.IsNull(problem);
        }
        else
        {
            StringAssert.Contains(problem, expected);
        }
    }

    [TestMethod]
    public void TheConfigReadsNormalizesAndValidatesTheEntries()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """
                {
                  "securityKey": "0123456789abcdef",
                  "machines": ["desk", "laptop"],
                  "advanced": { "edgeAlignment": [
                    { "from": "desk", "to": " laptop ", "start": 0.25, "end": 0.75 },
                    { "from": "gone", "to": "away", "start": 0.1, "end": 0.9 } ] }
                }
                """);

            var cfg = LiteConfig.TryLoad(path, out var errors, out _);

            Assert.IsNotNull(cfg, string.Join("; ", errors));
            Assert.AreEqual(2, cfg!.Advanced.EdgeAlignment.Count, "an entry for PCs that aren't neighbours is not an error");
            Assert.AreEqual("LAPTOP", cfg.Advanced.EdgeAlignment[0].To);

            // Saved and read back unchanged.
            string saved = Path.Combine(dir, "saved.json");
            cfg.Save(saved);
            var again = LiteConfig.TryLoad(saved, out errors, out _);
            Assert.IsNotNull(again, string.Join("; ", errors));
            Assert.AreEqual(LiteEdgeAlignment.Canonical(cfg.Advanced.EdgeAlignment), LiteEdgeAlignment.Canonical(again!.Advanced.EdgeAlignment));

            File.WriteAllText(path, """
                { "securityKey": "0123456789abcdef",
                  "advanced": { "edgeAlignment": [ { "from": "A", "to": "B", "start": 0.5, "end": 0.51 }, { "from": "", "to": "B", "start": 0, "end": 1 } ] } }
                """);
            Assert.IsNull(LiteConfig.TryLoad(path, out errors, out _));
            Assert.AreEqual(2, errors.Count(e => e.StartsWith("advanced.edgeAlignment[", StringComparison.Ordinal)), string.Join("; ", errors));

            File.WriteAllText(path, "{ \"securityKey\": \"0123456789abcdef\" }");
            Assert.AreEqual(0, LiteConfig.TryLoad(path, out _, out _)!.Advanced.EdgeAlignment.Count, "default: no entries");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void ReceivedEntriesReplaceTheRunningOnesWithoutTouchingTheFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"securityKey\": \"0123456789abcdef\", \"machines\": [\"A\", \"B\"] }");
            string before = File.ReadAllText(path);
            var settings = new Settings(new PassthroughPairingKeyCodec(), path, Path.Combine(dir, "state.json"));

            var entries = new[] { Entry("A", "B", 0.2, 0.9) };
            Assert.IsTrue(settings.SetEdgeAlignment(entries));
            Assert.IsFalse(settings.SetEdgeAlignment(entries), "same set again: nothing changed (so nothing is saved or sent on)");
            Assert.AreEqual(1, settings.EdgeAlignment.Count);
            Assert.AreEqual(before, File.ReadAllText(path));

            Assert.IsTrue(settings.SetEdgeAlignment(Array.Empty<EdgeAlignmentEntry>()));
            Assert.AreEqual(0, settings.EdgeAlignment.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void PersistingAReceivedSetWritesItOnceAndKeepsEverythingElse()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"securityKey\": \"0123456789abcdef\", \"machines\": [\"A\", \"B\"], \"shareClipboard\": false }");
            var entries = new[] { Entry("A", "B", 0.2, 0.9) };

            Assert.IsTrue(LiteAlignmentSync.TryPersist(path, entries));
            Assert.IsFalse(LiteAlignmentSync.TryPersist(path, entries), "already saved");

            var cfg = LiteConfig.TryLoad(path, out var errors, out _);
            Assert.IsNotNull(cfg, string.Join("; ", errors));
            Assert.IsFalse(cfg!.ShareClipboard);
            Assert.AreEqual("A", cfg.Advanced.EdgeAlignment.Single().From);
            Assert.AreEqual(0.9, cfg.Advanced.EdgeAlignment.Single().End);

            Assert.IsFalse(LiteAlignmentSync.TryPersist(path, new[] { Entry("A", "B", 0.5, 0.5) }), "an invalid set is not written");
            File.WriteAllText(path, "{ broken");
            Assert.IsFalse(LiteAlignmentSync.TryPersist(path, entries), "a file the user has to fix is never overwritten");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void TheControlRequestRoundTripsAndRejectsBadInput()
    {
        var entries = new[] { Entry("A", "B", 0.2, 0.9), Entry("C", "D", -0.5, 1.5) };

        var back = LiteEdgeAlignment.FromJson(LiteEdgeAlignment.ToJson(entries));
        Assert.AreEqual(LiteEdgeAlignment.Canonical(entries), LiteEdgeAlignment.Canonical(back));
        Assert.AreEqual(0, LiteEdgeAlignment.FromJson(new JsonObject { ["entries"] = new JsonArray() }).Count, "an empty set clears the entries");

        Assert.ThrowsException<ArgumentException>(() => LiteEdgeAlignment.FromJson(new JsonObject()));
        Assert.ThrowsException<ArgumentException>(() => LiteEdgeAlignment.FromJson(new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["from"] = "A" }) }));
        Assert.ThrowsException<ArgumentException>(() => LiteEdgeAlignment.FromJson(LiteEdgeAlignment.ToJson(new[] { Entry("A", "B", 0.9, 0.1) })));
        Assert.ThrowsException<ArgumentException>(() => LiteEdgeAlignment.FromJson(LiteEdgeAlignment.ToJson(Enumerable.Repeat(Entry("A", "B", 0.2, 0.9), 9))));
        Assert.ThrowsException<ArgumentException>(() => LiteEdgeAlignment.FromJson(new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["from"] = "A", ["to"] = "B", ["start"] = "x", ["end"] = 1 }) }));
    }

    [TestMethod]
    public void ReceivedEntriesWithInvalidPcNamesAreDropped()
    {
        Assert.IsTrue(LiteEdgeAlignment.IsValidPcName("DESK-PC_1.corp"));
        Assert.IsTrue(LiteEdgeAlignment.IsValidPcName(new string('a', 64)));
        foreach (string bad in new[] { "", " ", null!, new string('a', 65), "A B", "A:B", "A,B", "A;B", "A\\B", "A\"B\"", "A\r\nB", "<script>", "A/B" })
        {
            Assert.IsFalse(LiteEdgeAlignment.IsValidPcName(bad), bad ?? "(null)");
        }

        var request = LiteEdgeAlignment.ToJson(new[]
        {
            Entry("DESK", "LAPTOP", 0.2, 0.9),
            Entry("DESK", "LAP:TOP", 0.2, 0.9),
            Entry("BAD NAME", "LAPTOP", 0.2, 0.9),
            Entry(new string('x', 65), "LAPTOP", 0.2, 0.9),
            Entry("LAPTOP", "SERVER-2", -0.5, 1.5),
        });

        var kept = LiteEdgeAlignment.FromJson(request);

        Assert.AreEqual(2, kept.Count);
        Assert.AreEqual("DESK", kept[0].From);
        Assert.AreEqual("SERVER-2", kept[1].To);
    }

    [TestMethod]
    public void TheCommandLineFormRoundTrips()
    {
        var entries = new[] { Entry("DESK-PC", "LAPTOP", 0.125, 0.875), Entry("A", "B", -0.5, 1.5) };

        string text = LiteEdgeAlignment.Encode(entries);
        Assert.AreEqual("DESK-PC:LAPTOP:0.125:0.875,A:B:-0.5:1.5", text);
        Assert.AreEqual(LiteEdgeAlignment.Canonical(entries), LiteEdgeAlignment.Canonical(LiteEdgeAlignment.Decode(text)!));

        Assert.AreEqual("-", LiteEdgeAlignment.Encode(Array.Empty<EdgeAlignmentEntry>()));
        Assert.AreEqual(0, LiteEdgeAlignment.Decode("-")!.Count);
        Assert.IsNull(LiteEdgeAlignment.Decode("A:B:0.1"));
        Assert.IsNull(LiteEdgeAlignment.Decode("A:B:x:1"));
        Assert.IsNull(LiteEdgeAlignment.Decode("A:B:0.9:0.1"));
    }

    [TestMethod]
    public void CanonicalIgnoresOrderCaseAndDefaultEntries()
    {
        var one = new[] { Entry("a", "b", 0.2, 0.9), Entry("c", "d", 0, 1), Entry("e", "f", 0.1, 0.4) };
        var two = new[] { Entry("E", "F", 0.1, 0.4), Entry("A", "B", 0.2, 0.9) };

        Assert.AreEqual(LiteEdgeAlignment.Canonical(one), LiteEdgeAlignment.Canonical(two));
        Assert.AreNotEqual(LiteEdgeAlignment.Canonical(one), LiteEdgeAlignment.Canonical(new[] { Entry("A", "B", 0.2, 0.8) }));
        Assert.AreEqual(string.Empty, LiteEdgeAlignment.Canonical(null!));
    }
}
