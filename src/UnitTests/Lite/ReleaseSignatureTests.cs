// MwbLite: tests for signed releases: the release key check, the checksum list taken from a verified signature,
// the pinned download URLs, and the download itself (with a fake HTTP server, no network).
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class ReleaseSignatureTests
{
    private const string Base = "https://github.com/nozinoz/MwbLite/releases/download/v9.1.0/";
    private const string SetupName = "MwbLite-Setup-9.1.0.exe";

    private ECDsa key = null!;
    private Func<string> originalProvider = null!;

    [TestInitialize]
    public void Init()
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        originalProvider = LiteReleaseKey.PublicKeyProvider;
        LiteReleaseKey.PublicKeyProvider = () => PublicKeyOf(key);
    }

    [TestCleanup]
    public void Cleanup()
    {
        LiteReleaseKey.PublicKeyProvider = originalProvider;
        key.Dispose();
    }

    private static string PublicKeyOf(ECDsa k) => Convert.ToBase64String(k.ExportSubjectPublicKeyInfo());

    private static string Sign(ECDsa k, byte[] data) =>
        Convert.ToBase64String(k.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] SumsFor(byte[] setup) =>
        Encoding.ASCII.GetBytes(
            "1111111111111111111111111111111111111111111111111111111111111111  MwbLite-9.1.0-x64.msi\r\n"
            + Hex(setup) + "  " + SetupName + "\r\n");

    // ---- the key -----------------------------------------------------------------------------------------

    [TestMethod]
    public void GoodSignatureIsAccepted()
    {
        byte[] sums = SumsFor(new byte[] { 1, 2, 3 });
        Assert.IsTrue(LiteReleaseKey.Verify(sums, Sign(key, sums)));
        Assert.IsTrue(LiteReleaseKey.Verify(sums, "  " + Sign(key, sums) + "\r\n"), "a trailing newline in the .sig file is fine");
    }

    [TestMethod]
    public void TamperedSumsAreRejected()
    {
        byte[] sums = SumsFor(new byte[] { 1, 2, 3 });
        string sig = Sign(key, sums);
        byte[] changed = (byte[])sums.Clone();
        changed[3] ^= 1;
        Assert.IsFalse(LiteReleaseKey.Verify(changed, sig));
        Assert.IsFalse(LiteReleaseKey.Verify(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(sums) + "\r\n"), sig), "even an added newline");
    }

    [TestMethod]
    public void TamperedOrMalformedSignatureIsRejected()
    {
        byte[] sums = SumsFor(new byte[] { 1, 2, 3 });
        byte[] sig = Convert.FromBase64String(Sign(key, sums));
        sig[10] ^= 0x80;
        Assert.IsFalse(LiteReleaseKey.Verify(sums, Convert.ToBase64String(sig)));
        Assert.IsFalse(LiteReleaseKey.Verify(sums, "not base64 at all!"));
        Assert.IsFalse(LiteReleaseKey.Verify(sums, Convert.ToBase64String(new byte[64])), "64 zero bytes");
        Assert.IsFalse(LiteReleaseKey.Verify(sums, Convert.ToBase64String(new byte[10])), "wrong length");

        byte[] der = key.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.IsFalse(LiteReleaseKey.Verify(sums, Convert.ToBase64String(der)), "only the r||s form is accepted");
    }

    [TestMethod]
    public void SignatureFromAnotherKeyIsRejected()
    {
        byte[] sums = SumsFor(new byte[] { 1, 2, 3 });
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.IsFalse(LiteReleaseKey.Verify(sums, Sign(other, sums)));
    }

    [TestMethod]
    public void MissingSignatureIsRejected()
    {
        byte[] sums = SumsFor(new byte[] { 1, 2, 3 });
        Assert.IsFalse(LiteReleaseKey.Verify(sums, null!));
        Assert.IsFalse(LiteReleaseKey.Verify(sums, string.Empty));
        Assert.IsFalse(LiteReleaseKey.Verify(sums, "   "));
        Assert.IsFalse(LiteReleaseKey.Verify(null!, Sign(key, sums)));
    }

    [TestMethod]
    public void OnlyAP256KeyIsAccepted()
    {
        byte[] sums = SumsFor(new byte[] { 1, 2, 3 });
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string sig = Convert.ToBase64String(p384.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.IsFalse(LiteReleaseKey.Verify(sums, sig, PublicKeyOf(p384)));
        Assert.IsFalse(LiteReleaseKey.Verify(sums, Sign(key, sums), "not a key"));
        Assert.IsFalse(LiteReleaseKey.Verify(sums, Sign(key, sums), string.Empty));
    }

    [TestMethod]
    public void TestKeysAreNotTheBuiltInKey()
    {
        byte[] sums = SumsFor(new byte[] { 1, 2, 3 });
        LiteReleaseKey.PublicKeyProvider = originalProvider;
        Assert.IsFalse(LiteReleaseKey.Verify(sums, Sign(key, sums)), "a signature by a test key doesn't verify with the built-in key (or the placeholder)");
    }

    // ---- the checksum list ---------------------------------------------------------------------------------

    [TestMethod]
    public void ExpectedHashComesFromTheVerifiedSums()
    {
        byte[] setup = { 9, 8, 7 };
        byte[] sums = SumsFor(setup);
        Assert.AreEqual(Hex(setup), LiteUpdate.SignedSha256(sums, Sign(key, sums), SetupName));
        Assert.AreEqual("1111111111111111111111111111111111111111111111111111111111111111", LiteUpdate.SignedSha256(sums, Sign(key, sums), "MwbLite-9.1.0-x64.msi"));
    }

    [TestMethod]
    public void NoHashIsGivenWithoutAValidSignature()
    {
        byte[] sums = SumsFor(new byte[] { 9, 8, 7 });
        _ = Assert.ThrowsExactly<ReleaseNotSignedException>(() => LiteUpdate.SignedSha256(sums, string.Empty, SetupName));
        _ = Assert.ThrowsExactly<ReleaseNotSignedException>(() => LiteUpdate.SignedSha256(sums, Sign(key, SumsFor(new byte[] { 1 })), SetupName));
        StringAssert.Contains(new ReleaseNotSignedException().Message, "isn't signed with MwbLite's release key");
    }

    [TestMethod]
    public void SignedListWithoutSetupIsRefused()
    {
        byte[] sums = Encoding.ASCII.GetBytes("1111111111111111111111111111111111111111111111111111111111111111  other.exe\r\n");
        _ = Assert.ThrowsExactly<InvalidDataException>(() => LiteUpdate.SignedSha256(sums, Sign(key, sums), SetupName));
    }

    // ---- pinned URLs ---------------------------------------------------------------------------------------

    [TestMethod]
    public void OnlyReleaseDownloadsOfThisRepositoryArePinned()
    {
        Assert.IsTrue(LiteUpdate.IsPinnedUrl(Base + "SHA256SUMS.txt"));
        Assert.IsTrue(LiteUpdate.IsPinnedUrl("HTTPS://GITHUB.COM/nozinoz/mwblite/releases/download/v9.1.0/SHA256SUMS.txt.sig"));

        Assert.IsFalse(LiteUpdate.IsPinnedUrl(null!));
        Assert.IsFalse(LiteUpdate.IsPinnedUrl(string.Empty));
        Assert.IsFalse(LiteUpdate.IsPinnedUrl("http://github.com/nozinoz/MwbLite/releases/download/v9.1.0/SHA256SUMS.txt"), "http");
        Assert.IsFalse(LiteUpdate.IsPinnedUrl("https://github.com/other/MwbLite/releases/download/v9.1.0/SHA256SUMS.txt"), "other owner");
        Assert.IsFalse(LiteUpdate.IsPinnedUrl("https://github.com/nozinoz/MwbLite2/releases/download/v9.1.0/SHA256SUMS.txt"), "other repository");
        Assert.IsFalse(LiteUpdate.IsPinnedUrl("https://github.com.evil.example/nozinoz/MwbLite/releases/download/v9.1.0/SHA256SUMS.txt"), "other host");
        Assert.IsFalse(LiteUpdate.IsPinnedUrl("https://github.com@evil.example/nozinoz/MwbLite/releases/download/v9.1.0/SHA256SUMS.txt"), "user info");
        Assert.IsFalse(LiteUpdate.IsPinnedUrl("https://evil.example/https://github.com/nozinoz/MwbLite/releases/download/x"), "prefix inside the path");
        Assert.IsFalse(LiteUpdate.IsPinnedUrl("https://github.com:8443/nozinoz/MwbLite/releases/download/v9.1.0/SHA256SUMS.txt"), "port");
        Assert.IsFalse(LiteUpdate.IsPinnedUrl(Base + "../../../../other/Repo/releases/download/v1/x.exe"), "path climbs out");
        Assert.IsFalse(LiteUpdate.IsPinnedUrl("https://objects.githubusercontent.com/nozinoz/MwbLite/releases/download/v9.1.0/SHA256SUMS.txt"), "CDN host");
    }

    [TestMethod]
    public void ReleaseJsonGivesTheSignatureAsset()
    {
        ReleaseInfo r = LiteUpdate.ParseLatest($$"""
            { "tag_name": "v9.1.0", "assets": [
              { "name": "{{SetupName}}", "browser_download_url": "{{Base}}{{SetupName}}", "size": 5, "digest": "sha256:{{new string('a', 64)}}" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "{{Base}}SHA256SUMS.txt", "size": 5 },
              { "name": "SHA256SUMS.txt.sig", "browser_download_url": "{{Base}}SHA256SUMS.txt.sig", "size": 5 } ] }
            """);
        Assert.AreEqual(Base + "SHA256SUMS.txt.sig", r.SignatureUrl);
        Assert.AreEqual(Base + "SHA256SUMS.txt", r.ChecksumsUrl);

        Assert.IsNull(LiteUpdate.ParseLatest("""{ "tag_name": "v9.1.0", "assets": [] }""").SignatureUrl);
    }

    // ---- the download, against a fake server ---------------------------------------------------------------

    private sealed class FakeServer : HttpMessageHandler
    {
        internal readonly Dictionary<string, byte[]> Files = new();
        internal readonly List<string> Requested = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requested.Add(url);
            return Task.FromResult(Files.TryGetValue(url, out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static ReleaseInfo Release(byte[] setup, bool withSignature = true, string? checksumsUrl = null) =>
        new(new Version(9, 1, 0), "v9.1.0", "MwbLite 9.1.0", string.Empty, string.Empty, DateTime.UtcNow, SetupName, Base + SetupName, setup.Length,
            checksumsUrl ?? Base + "SHA256SUMS.txt", withSignature ? Base + "SHA256SUMS.txt.sig" : null!);

    private FakeServer Server(byte[] setup, byte[]? sums = null, string? signature = null)
    {
        sums ??= SumsFor(setup);
        var server = new FakeServer();
        server.Files[Base + SetupName] = setup;
        server.Files[Base + "SHA256SUMS.txt"] = sums;
        server.Files[Base + "SHA256SUMS.txt.sig"] = Encoding.ASCII.GetBytes(signature ?? Sign(key, sums));
        return server;
    }

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "MwbLite-test-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public async Task SignedReleaseIsDownloadedAndChecked()
    {
        byte[] setup = RandomNumberGenerator.GetBytes(5000);
        FakeServer server = Server(setup);
        string dir = NewDir();
        try
        {
            LiteUpdate.VerifiedSetup v = await LiteUpdate.DownloadVerifiedAsync(Release(setup), null, dir, CancellationToken.None, server);
            Assert.AreEqual(Hex(setup), v.Sha256);
            CollectionAssert.AreEqual(setup, File.ReadAllBytes(v.Path));
            Assert.IsNull(await LiteUpdate.CheckSignatureAsync(Release(setup), CancellationToken.None, server));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReleaseWithoutSignatureAssetIsRefusedBeforeAnythingIsDownloaded()
    {
        byte[] setup = RandomNumberGenerator.GetBytes(100);
        FakeServer server = Server(setup);
        string dir = NewDir();
        _ = await Assert.ThrowsExactlyAsync<ReleaseNotSignedException>(() => LiteUpdate.DownloadVerifiedAsync(Release(setup, withSignature: false), null, dir, CancellationToken.None, server));
        Assert.AreEqual(0, server.Requested.Count, "nothing is fetched");
        Assert.IsFalse(Directory.Exists(dir));
        StringAssert.Contains(await LiteUpdate.CheckSignatureAsync(Release(setup, withSignature: false), CancellationToken.None, server), "isn't signed");
    }

    [TestMethod]
    public async Task BadSignatureIsRefusedAndSetupIsNeverRequested()
    {
        byte[] setup = RandomNumberGenerator.GetBytes(100);
        byte[] sums = SumsFor(setup);
        using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        FakeServer server = Server(setup, sums, Sign(attacker, sums));
        string dir = NewDir();
        _ = await Assert.ThrowsExactlyAsync<ReleaseNotSignedException>(() => LiteUpdate.DownloadVerifiedAsync(Release(setup), null, dir, CancellationToken.None, server));
        CollectionAssert.DoesNotContain(server.Requested, Base + SetupName);
        Assert.IsFalse(Directory.Exists(dir));
    }

    [TestMethod]
    public async Task SwappedSetupIsRefusedEvenWithTheAttackersOwnDigest()
    {
        // GitHub's API digest (or a re-uploaded Setup) can't help: the signed list still names the real file's hash.
        byte[] real = RandomNumberGenerator.GetBytes(300);
        byte[] evil = RandomNumberGenerator.GetBytes(300);
        FakeServer server = Server(real);
        server.Files[Base + SetupName] = evil;
        string dir = NewDir();
        try
        {
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LiteUpdate.DownloadVerifiedAsync(Release(evil), null, dir, CancellationToken.None, server));
            Assert.IsFalse(File.Exists(Path.Combine(dir, SetupName)), "the mismatching file is deleted");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ListSwappedForAnotherWithTheOldSignatureIsRefused()
    {
        byte[] setup = RandomNumberGenerator.GetBytes(100);
        byte[] sums = SumsFor(setup);
        string sig = Sign(key, sums);
        FakeServer server = Server(setup, sums, sig);
        server.Files[Base + "SHA256SUMS.txt"] = SumsFor(RandomNumberGenerator.GetBytes(100)); // the attacker's list, the old signature
        string dir = NewDir();
        _ = await Assert.ThrowsExactlyAsync<ReleaseNotSignedException>(() => LiteUpdate.DownloadVerifiedAsync(Release(setup), null, dir, CancellationToken.None, server));
        CollectionAssert.DoesNotContain(server.Requested, Base + SetupName);
    }

    [TestMethod]
    public async Task ChecksumsFromAnotherPlaceAreNotFetched()
    {
        byte[] setup = RandomNumberGenerator.GetBytes(100);
        FakeServer server = Server(setup);
        string evil = "https://example.invalid/SHA256SUMS.txt";
        server.Files[evil] = SumsFor(setup);
        string dir = NewDir();
        _ = await Assert.ThrowsExactlyAsync<ReleaseNotSignedException>(() => LiteUpdate.DownloadVerifiedAsync(Release(setup, checksumsUrl: evil), null, dir, CancellationToken.None, server));
        Assert.AreEqual(0, server.Requested.Count);

        // A Setup URL outside the repository's release downloads is refused as before.
        ReleaseInfo elsewhere = Release(setup) with { SetupUrl = "https://example.invalid/" + SetupName };
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => LiteUpdate.DownloadVerifiedAsync(elsewhere, null, dir, CancellationToken.None, server));
        Assert.AreEqual(0, server.Requested.Count);
    }

    [TestMethod]
    public async Task NetworkErrorsAreNotReportedAsUnsigned()
    {
        byte[] setup = RandomNumberGenerator.GetBytes(100);
        FakeServer server = new(); // every file is a 404
        _ = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => LiteUpdate.CheckSignatureAsync(Release(setup), CancellationToken.None, server));
    }

    [TestMethod]
    public async Task OversizedSignatureFileIsNotRead()
    {
        byte[] setup = RandomNumberGenerator.GetBytes(100);
        FakeServer server = Server(setup);
        server.Files[Base + "SHA256SUMS.txt.sig"] = new byte[1024 * 1024];
        string? problem = await LiteUpdate.CheckSignatureAsync(Release(setup), CancellationToken.None, server);
        Assert.IsNotNull(problem);
    }
}
