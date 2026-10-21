// MwbLite: tests for pairing ("Add a PC" / "Join") and layout persistence.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;
using MouseWithoutBorders.Lite.Pairing;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class PairingTests
{
    private const string Key = "0123456789abcdefXYZ";

    private sealed class FakeUi : IPairingUi
    {
        private readonly bool accept;
        private readonly TimeSpan delay;

        public FakeUi(bool accept, TimeSpan delay = default)
        {
            this.accept = accept;
            this.delay = delay;
        }

        public string? Code { get; private set; }

        public string? Peer { get; private set; }

        public List<string> Statuses { get; } = new();

        public void Status(string text) => Statuses.Add(text);

        public async Task<bool> ConfirmCodeAsync(string peerName, string code, CancellationToken cancellationToken)
        {
            Peer = peerName;
            Code = code;
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return accept;
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static LiteConfig HostConfig(params string[] machines) => new()
    {
        SecurityKey = Key,
        Machines = machines.ToList(),
        Layout = new LiteConfig.LayoutConfig { OneRow = false, WrapMouse = false },
        Advanced = new LiteConfig.AdvancedConfig { TcpPort = 16000 },
    };

    [TestMethod]
    public void BothSidesDeriveTheSameCodeAndKeyAndDetectTampering()
    {
        using var joiner = new PairingCrypto();
        using var host = new PairingCrypto();
        byte[] commit = PairingCrypto.Commitment(joiner.PublicKey, joiner.Nonce);
        Assert.IsTrue(PairingCrypto.VerifyCommitment(commit, joiner.PublicKey, joiner.Nonce));
        Assert.IsFalse(PairingCrypto.VerifyCommitment(commit, joiner.PublicKey, host.Nonce));

        joiner.Complete(host.PublicKey, "J", "H", joiner.PublicKey, host.PublicKey, joiner.Nonce, host.Nonce);
        host.Complete(joiner.PublicKey, "J", "H", joiner.PublicKey, host.PublicKey, joiner.Nonce, host.Nonce);

        Assert.AreEqual(6, host.Code.Length);
        Assert.AreEqual(host.Code, joiner.Code);
        byte[] box = host.Seal("config", Encoding.UTF8.GetBytes("secret"));
        Assert.AreEqual("secret", Encoding.UTF8.GetString(joiner.Open("config", box)));

        Assert.ThrowsException<AuthenticationTagMismatchException>(() => joiner.Open("done", box), "purpose is bound");
        box[^1] ^= 1;
        Assert.ThrowsException<AuthenticationTagMismatchException>(() => joiner.Open("config", box), "tampering detected");
    }

    [TestMethod]
    public void DifferentTranscriptsGiveDifferentCodes()
    {
        using var a = new PairingCrypto();
        using var b = new PairingCrypto();
        using var c = new PairingCrypto();
        a.Complete(b.PublicKey, "J", "H", a.PublicKey, b.PublicKey, a.Nonce, b.Nonce);
        c.Complete(b.PublicKey, "J", "H", c.PublicKey, b.PublicKey, c.Nonce, b.Nonce);
        Assert.AreNotEqual(a.Code, c.Code); // 1 in a million false failure; different key pairs
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task AddAPcAndJoinExchangeTheKeyAndLayout()
    {
        int port = FreePort();
        var hostUi = new FakeUi(accept: true);
        var joinUi = new FakeUi(accept: true, TimeSpan.FromMilliseconds(200));
        List<string>? hostApplied = null;
        PairingPayload? joinerApplied = null;

        Task<string> hostTask = PairingSession.HostAsync(HostConfig("MINIPC"), "minipc", hostUi, m => hostApplied = m, port, CancellationToken.None);
        await Task.Delay(300);
        string hostName = await PairingSession.JoinAsync("127.0.0.1", port, "laptop1", joinUi, p => joinerApplied = p, CancellationToken.None);
        string joined = await hostTask;

        Assert.AreEqual("MINIPC", hostName);
        Assert.AreEqual("LAPTOP1", joined);
        Assert.IsNotNull(hostUi.Code);
        Assert.AreEqual(hostUi.Code, joinUi.Code, "both screens show the same code");
        Assert.AreEqual("LAPTOP1", hostUi.Peer);
        Assert.AreEqual("MINIPC", joinUi.Peer);

        Assert.IsNotNull(joinerApplied);
        Assert.AreEqual(Key, joinerApplied.SecurityKey);
        CollectionAssert.AreEqual(new[] { "MINIPC", "LAPTOP1" }, joinerApplied.Machines);
        Assert.IsFalse(joinerApplied.OneRow);
        Assert.IsFalse(joinerApplied.WrapMouse);
        Assert.AreEqual(16000, joinerApplied.TcpPort);
        CollectionAssert.AreEqual(joinerApplied.Machines, hostApplied);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task CancelOnEitherPcChangesNothing()
    {
        foreach (bool hostAccepts in new[] { false, true })
        {
            int port = FreePort();
            var applied = false;
            Task<string> hostTask = PairingSession.HostAsync(HostConfig("MINIPC"), "MINIPC", new FakeUi(hostAccepts), _ => applied = true, port, CancellationToken.None);
            await Task.Delay(300);
            Task<string> joinTask = PairingSession.JoinAsync("127.0.0.1", port, "LAPTOP1", new FakeUi(!hostAccepts, TimeSpan.FromMilliseconds(300)), _ => applied = true, CancellationToken.None);

            // The side that pressed Cancel returns null; the other side gets "cancelled on the other PC".
            Task<string> canceller = hostAccepts ? joinTask : hostTask;
            Task<string> other = hostAccepts ? hostTask : joinTask;
            Assert.IsNull(await canceller);
            var e = await Assert.ThrowsExceptionAsync<PairingException>(() => other);
            StringAssert.Contains(e.Message, "cancelled");
            Assert.IsFalse(applied);
        }
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task HostRefusesWhenTheLayoutIsFull()
    {
        int port = FreePort();
        Task<string> hostTask = PairingSession.HostAsync(HostConfig("A", "B", "C", "MINIPC"), "MINIPC", new FakeUi(true), _ => Assert.Fail("must not apply"), port, CancellationToken.None);
        await Task.Delay(300);
        var e = await Assert.ThrowsExceptionAsync<PairingException>(() => PairingSession.JoinAsync("127.0.0.1", port, "LAPTOP9", new FakeUi(true), _ => Assert.Fail("must not apply"), CancellationToken.None));
        StringAssert.Contains(e.Message, "already has 4");
        await Assert.ThrowsExceptionAsync<PairingException>(() => hostTask);
    }

    [TestMethod]
    public void AddMachinesKeepsOrderAndUsesFreeSlots()
    {
        CollectionAssert.AreEqual(new[] { "MINIPC", "L1" }, PairingSession.AddMachines(HostConfig(), "MINIPC", "L1"));
        CollectionAssert.AreEqual(new[] { "L1", "MINIPC" }, PairingSession.AddMachines(HostConfig("L1", "MINIPC"), "MINIPC", "L1"), "re-pairing keeps the layout");
        CollectionAssert.AreEqual(new[] { "A", "MINIPC", "L2" }, PairingSession.AddMachines(HostConfig("A", "", "L2"), "MINIPC", "L2"));
        Assert.IsNull(PairingSession.AddMachines(HostConfig("A", "B", "C", "D"), "A", "E"));
    }

    [TestMethod]
    public void PeerLayoutIsPersistedOnlyWhenDifferentAndValid()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"securityKey\": \"" + Key + "\", \"machines\": [\"A\", \"B\"], \"shareClipboard\": false }");

            Assert.IsFalse(LiteLayoutSync.TryPersist(path, new[] { "A", "B", "", "" }, true, true), "unchanged");
            Assert.IsTrue(LiteLayoutSync.TryPersist(path, new[] { "B", "A", "C", "" }, false, true));
            var cfg = LiteConfig.TryLoad(path, out var errors, out _);
            Assert.IsNotNull(cfg, string.Join("; ", errors));
            CollectionAssert.AreEqual(new[] { "B", "A", "C" }, cfg!.Machines);
            Assert.IsFalse(cfg.Layout.OneRow);
            Assert.IsFalse(cfg.ShareClipboard, "other settings are kept");

            Assert.IsFalse(LiteLayoutSync.TryPersist(path, new[] { "A", "A" }, true, true), "invalid layout ignored");

            File.WriteAllText(path, "{ broken");
            Assert.IsFalse(LiteLayoutSync.TryPersist(path, new[] { "A" }, true, true), "a broken file is left alone");
            Assert.AreEqual("{ broken", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
