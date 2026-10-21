// MwbLite: tests for sending folders / several files as one bundle.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class FileBundleTests
{
    private string root = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        root = Path.Combine(Path.GetTempPath(), "MwbLiteBundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        Directory.Delete(root, recursive: true);
    }

    private string MakeSource()
    {
        string src = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(src, "Photos", "2026", "Trip"));
        Directory.CreateDirectory(Path.Combine(src, "Photos", "Empty"));
        File.WriteAllText(Path.Combine(src, "Photos", "a.jpg"), "A");
        File.WriteAllText(Path.Combine(src, "Photos", "2026", "Trip", "b.jpg"), "BB");
        string hidden = Path.Combine(src, "Photos", ".hidden");
        File.WriteAllText(hidden, "H");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(src, "notes.txt"), "N");
        return src;
    }

    private static string WaitForZip(string manifest)
    {
        string zip = LiteFileBundle.ResolveForSend(manifest, out string error);
        Assert.IsNull(error, error);
        Assert.IsTrue(File.Exists(zip));
        StringAssert.EndsWith(zip, LiteFileBundle.ZipSuffix);
        return zip;
    }

    [TestMethod]
    public void SingleFileIsSentAsBefore()
    {
        string src = MakeSource();
        string file = Path.Combine(src, "notes.txt");
        Assert.IsNull(LiteFileBundle.Prepare(new[] { file }, limitTotalSize: true, Path.Combine(root, "out")));
        Assert.AreEqual(file, LiteFileBundle.ResolveForSend(file, out string error));
        Assert.IsNull(error);
    }

    [TestMethod]
    public void FolderAndFilesRoundTripWithStructure()
    {
        string src = MakeSource();
        string manifest = LiteFileBundle.Prepare(new[] { Path.Combine(src, "Photos"), Path.Combine(src, "notes.txt") }, limitTotalSize: false, Path.Combine(root, "out"))!;
        Assert.IsNotNull(manifest);
        StringAssert.EndsWith(manifest, "Photos and 1 more" + LiteFileBundle.ManifestExtension);
        string zip = WaitForZip(manifest);

        // Received copy, as MWB would save it, then unpacked.
        string target = Path.Combine(root, "Desktop", "MouseWithoutBorders");
        Directory.CreateDirectory(target);
        string received = Path.Combine(target, Path.GetFileName(zip));
        File.Copy(zip, received);
        Assert.IsTrue(LiteFileBundle.IsBundle(received));
        Assert.IsTrue(LiteFileBundle.TryExtract(received, target, out var items, out string description));

        CollectionAssert.AreEquivalent(new[] { Path.Combine(target, "Photos"), Path.Combine(target, "notes.txt") }, items);
        Assert.AreEqual("Photos and 1 more (2 items)", description);
        Assert.AreEqual("A", File.ReadAllText(Path.Combine(target, "Photos", "a.jpg")));
        Assert.AreEqual("BB", File.ReadAllText(Path.Combine(target, "Photos", "2026", "Trip", "b.jpg")));
        Assert.AreEqual("H", File.ReadAllText(Path.Combine(target, "Photos", ".hidden")), "hidden files are included");
        Assert.IsTrue(Directory.Exists(Path.Combine(target, "Photos", "Empty")), "empty folders are kept");
        Assert.IsFalse(File.Exists(received), "the zip is removed after unpacking");

        // A second copy doesn't overwrite the first.
        File.Copy(zip, received);
        Assert.IsTrue(LiteFileBundle.TryExtract(received, target, out items, out _));
        CollectionAssert.AreEquivalent(new[] { Path.Combine(target, "Photos (2)"), Path.Combine(target, "notes (2).txt") }, items);
    }

    [TestMethod]
    public void SeveralFilesOnly()
    {
        string src = MakeSource();
        string manifest = LiteFileBundle.Prepare(new[] { Path.Combine(src, "notes.txt"), Path.Combine(src, "Photos", "a.jpg") }, limitTotalSize: true, Path.Combine(root, "out"))!;
        string zip = WaitForZip(manifest);
        using var archive = ZipFile.OpenRead(zip);
        CollectionAssert.AreEquivalent(new[] { "notes.txt", "a.jpg" }, archive.Entries.Select(e => e.FullName).ToArray());
    }

    [TestMethod]
    public void CopyPasteSelectionsOver100MbAreRefused()
    {
        string big = Path.Combine(root, "big");
        Directory.CreateDirectory(big);
        using (var fs = new FileStream(Path.Combine(big, "large.bin"), FileMode.Create))
        {
            fs.SetLength(LiteFileBundle.CopyPasteLimit + 1);
        }

        string manifest = LiteFileBundle.Prepare(new[] { big }, limitTotalSize: true, Path.Combine(root, "out"))!;
        Assert.IsNull(LiteFileBundle.ResolveForSend(manifest, out string error));
        StringAssert.Contains(error, "File too big");
        Assert.AreEqual(-1, error.IndexOfAny(new[] { '*', '\\', ':' }), "the message is sent as a file name");
    }

    [TestMethod]
    public void ZipEntriesOutsideTheTargetAreRejected()
    {
        string evil = Path.Combine(root, "evil" + LiteFileBundle.ZipSuffix);
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escaped.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("x");
        }

        string target = Path.Combine(root, "target");
        Assert.IsFalse(LiteFileBundle.TryExtract(evil, target, out _, out _));
        Assert.IsFalse(File.Exists(Path.Combine(root, "escaped.txt")));
        Assert.IsTrue(File.Exists(evil), "a rejected bundle is kept, not deleted");
    }
}
