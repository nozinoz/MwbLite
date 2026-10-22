// MwbLite: tests for the update check (GitHub Releases): version parsing, reading the release JSON, checksums.
using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;
using MouseWithoutBorders.Lite.Ui;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class UpdateTests
{
    [TestMethod]
    public void VersionsParseFromTagsAndNames()
    {
        Assert.AreEqual(new Version(0, 8, 0), LiteUpdate.ParseVersion("v0.8.0"));
        Assert.AreEqual(new Version(1, 2, 0), LiteUpdate.ParseVersion("MwbLite 1.2"));
        Assert.AreEqual(new Version(1, 0, 0), LiteUpdate.ParseVersion("1"));
        Assert.AreEqual(new Version(0, 8, 1), LiteUpdate.ParseVersion("0.8.1.0"));
        Assert.IsNull(LiteUpdate.ParseVersion("latest"));
        Assert.IsTrue(LiteUpdate.Current >= new Version(0, 8, 0), "this build is at least 0.8.0");
    }

    private const string ReleaseJson = """
        {
          "tag_name": "v9.1.0",
          "name": "MwbLite 9.1.0",
          "html_url": "https://github.com/nozinoz/MwbLite/releases/tag/v9.1.0",
          "published_at": "2026-11-02T09:30:00Z",
          "body": "## What's new\n- **Faster** reconnects\n- See [the docs](https://example.com)",
          "assets": [
            { "name": "MwbLite-9.1.0-x64.msi", "browser_download_url": "https://github.com/nozinoz/MwbLite/releases/download/v9.1.0/MwbLite-9.1.0-x64.msi", "size": 7000000 },
            { "name": "MwbLite-Setup-9.1.0.exe", "browser_download_url": "https://github.com/nozinoz/MwbLite/releases/download/v9.1.0/MwbLite-Setup-9.1.0.exe", "size": 8500000, "digest": "sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/nozinoz/MwbLite/releases/download/v9.1.0/SHA256SUMS.txt", "size": 300 }
          ]
        }
        """;

    [TestMethod]
    public void LatestReleaseIsReadFromGitHubJson()
    {
        ReleaseInfo r = LiteUpdate.ParseLatest(ReleaseJson);
        Assert.AreEqual(new Version(9, 1, 0), r.Version);
        Assert.AreEqual("MwbLite-Setup-9.1.0.exe", r.SetupName);
        Assert.AreEqual(8500000, r.SetupSize);
        Assert.AreEqual("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", r.SetupSha256);
        StringAssert.EndsWith(r.ChecksumsUrl, "SHA256SUMS.txt");
        Assert.AreEqual(new DateTime(2026, 11, 2, 9, 30, 0, DateTimeKind.Utc), r.PublishedUtc);
        Assert.IsTrue(LiteUpdate.IsNewer(r));
        Assert.IsFalse(LiteUpdate.IsNewer(r with { Version = new Version(0, 1, 0) }));
        Assert.IsFalse(LiteUpdate.IsNewer(null));
    }

    [TestMethod]
    public void ReleaseWithoutSetupOrDigest()
    {
        ReleaseInfo r = LiteUpdate.ParseLatest("""{ "tag_name": "v9.0.0", "assets": [] }""");
        Assert.IsNull(r.SetupUrl);
        Assert.IsNull(r.SetupSha256);
        Assert.IsNull(LiteUpdate.ParseLatest("""{ "tag_name": "nightly" }"""));
    }

    [TestMethod]
    public void ChecksumsFileIsMatchedByFileName()
    {
        string sums = "1111111111111111111111111111111111111111111111111111111111111111  portable\\MwbLite.exe\r\n"
            + "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789  MwbLite-Setup-9.1.0.exe\r\n";
        Assert.AreEqual("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", LiteUpdate.ShaFromSums(sums, "MwbLite-Setup-9.1.0.exe"));
        Assert.AreEqual("1111111111111111111111111111111111111111111111111111111111111111", LiteUpdate.ShaFromSums(sums, "MwbLite.exe"));
        Assert.IsNull(LiteUpdate.ShaFromSums(sums, "other.exe"));
    }

    [TestMethod]
    public void ReleaseNotesBecomePlainText()
    {
        string plain = SettingsWindow.PlainNotes("## What's new\n- **Faster** reconnects\n- See [the docs](https://example.com)\nUse `Ctrl+S`.");
        Assert.AreEqual("What's new\n•  Faster reconnects\n•  See the docs\nUse Ctrl+S.", plain);
    }
}
