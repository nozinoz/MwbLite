// MwbLite: tests for the TLS + security-key proof around MWB connections, over real loopback sockets.
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class SecureChannelTests
{
    private const string Key = "0123456789abcdef0123";

    private static X509Certificate2 cert;
    private static Func<X509Certificate2> originalCertificate;
    private static Func<bool> originalAllowInsecure;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        // A throwaway certificate (deleted with its key on cleanup), not the account's stored one.
        cert = LiteSecureChannel.CreateCertificate(X509KeyStorageFlags.DefaultKeySet);
        originalCertificate = LiteSecureChannel.Certificate;
        originalAllowInsecure = LiteSecureChannel.AllowInsecure;
        LiteSecureChannel.Certificate = () => cert;
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        LiteSecureChannel.Certificate = originalCertificate;
        LiteSecureChannel.AllowInsecure = originalAllowInsecure;
        LiteSecureChannel.HandshakeTimeout = TimeSpan.FromSeconds(10);
        cert.Dispose();
    }

    [TestInitialize]
    public void TestInitialize()
    {
        LiteSecureChannel.AllowInsecure = () => false;
        LiteSecureChannel.HandshakeTimeout = TimeSpan.FromSeconds(10);
    }

    private static (Socket Client, Socket Server) Pair()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        Socket server = listener.Accept();
        return (client, server);
    }

    private static (Task<Stream> Client, Task<Stream> Server) Establish(Socket client, Socket server, string clientKey, string serverKey) =>
        (Task.Run(() => LiteSecureChannel.Establish(client, isClient: true, "SERVERPC", clientKey)),
         Task.Run(() => LiteSecureChannel.Establish(server, isClient: false, "CLIENTPC", serverKey)));

    private static Exception Unwrap(Task t)
    {
        try
        {
            t.GetAwaiter().GetResult();
            return null!;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    [TestMethod]
    public void SameKeyConnectsAndCarriesDataBothWays()
    {
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            var (c, s) = Establish(client, server, Key, Key);
            using Stream cs = c.Result, ss = s.Result;
            Assert.IsInstanceOfType(cs, typeof(SslStream));
            Assert.IsTrue(((SslStream)cs).SslProtocol is System.Security.Authentication.SslProtocols.Tls13 or System.Security.Authentication.SslProtocols.Tls12);

            cs.Write(new byte[] { 1, 2, 3 });
            cs.Flush();
            byte[] got = new byte[3];
            ss.ReadExactly(got);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, got);

            ss.Write(new byte[] { 9 });
            ss.Flush();
            Assert.AreEqual(9, cs.ReadByte());
        }
    }

    [TestMethod]
    public void DifferentKeysAreRejectedOnBothSides()
    {
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            var (c, s) = Establish(client, server, Key, "fedcba9876543210fedc");
            Assert.IsInstanceOfType(Unwrap(c), typeof(KeyMismatchException));
            Assert.IsInstanceOfType(Unwrap(s), typeof(KeyMismatchException));
            Assert.IsTrue(LiteSecureChannel.RecentKeyMismatch("SERVERPC"));
            Assert.IsTrue(LiteSecureChannel.RecentKeyMismatch("CLIENTPC"));
        }

        // A later successful connection clears the mark.
        var (client2, server2) = Pair();
        using (client2)
        using (server2)
        {
            var (c, s) = Establish(client2, server2, Key, Key);
            c.Result.Dispose();
            s.Result.Dispose();
            Assert.IsFalse(LiteSecureChannel.RecentKeyMismatch("SERVERPC"));
        }
    }

    [TestMethod]
    public void ProofIsBoundToBothCertificatesAndTheRole()
    {
        byte[] a = { 1, 2, 3 }, b = { 4, 5, 6 }, m = { 7, 8, 9 };
        byte[] direct = LiteSecureChannel.Proof(Key, (byte)'C', a, b);

        CollectionAssert.AreEqual(direct, LiteSecureChannel.Proof(Key, (byte)'C', a, b));
        CollectionAssert.AreNotEqual(direct, LiteSecureChannel.Proof(Key, (byte)'S', a, b), "role");
        CollectionAssert.AreNotEqual(direct, LiteSecureChannel.Proof(Key, (byte)'C', a, m), "a relay presents its own certificate");
        CollectionAssert.AreNotEqual(direct, LiteSecureChannel.Proof(Key, (byte)'C', m, b), "a relay presents its own certificate");
        CollectionAssert.AreNotEqual(direct, LiteSecureChannel.Proof("other-key-0123456789", (byte)'C', a, b), "key");
    }

    [TestMethod]
    public void OlderPeerIsRefusedUnlessAllowed()
    {
        // An older MWB opens with its (non-TLS) encryption header, not a TLS handshake record.
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            client.Send(new byte[] { 0x42, 0x00, 0x01 });
            Assert.ThrowsException<InsecurePeerException>(() => LiteSecureChannel.Establish(server, isClient: false, "OLDPC", Key));
            Assert.IsTrue(LiteSecureChannel.RecentInsecureRejected("OLDPC"));
        }

        LiteSecureChannel.AllowInsecure = () => true;
        var (client2, server2) = Pair();
        using (client2)
        using (server2)
        {
            client2.Send(new byte[] { 0x42 });
            using Stream plain = LiteSecureChannel.Establish(server2, isClient: false, "OLDPC2", Key);
            Assert.IsInstanceOfType(plain, typeof(NetworkStream));
            Assert.AreEqual(0x42, plain.ReadByte(), "the peeked byte is still there for MWB");
        }
    }

    [TestMethod]
    public void ClientTalkingToAnOlderServerIsReportedAsOlder()
    {
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            // The older server sends its own header straight away and ignores the TLS hello.
            server.Send(new byte[64]);
            Exception e = Unwrap(Task.Run(() => LiteSecureChannel.Establish(client, isClient: true, "OLDSERVER", Key)));
            Assert.IsInstanceOfType(e, typeof(InsecurePeerException));
            Assert.IsTrue(LiteSecureChannel.RecentInsecureRejected("OLDSERVER"));
        }
    }

    [TestMethod]
    public void SilentPeerTimesOut()
    {
        LiteSecureChannel.HandshakeTimeout = TimeSpan.FromSeconds(1);
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            Exception e = Unwrap(Task.Run(() => LiteSecureChannel.Establish(server, isClient: false, "SILENT", Key)));
            Assert.IsTrue(e is SocketException or IOException, e?.GetType().Name);
        }
    }

    [TestMethod]
    public async Task ProbeTellsMatchingFromDifferentKeys()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(2);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        async Task<LiteSecureChannel.ProbeResult> ProbeWith(string probeKey)
        {
            Task serve = Task.Run(() =>
            {
                using Socket s = listener.Accept();
                try
                {
                    LiteSecureChannel.Establish(s, isClient: false, "PROBER", Key).Dispose();
                }
                catch (Exception e) when (e is KeyMismatchException or IOException)
                {
                }
            });
            var result = await LiteSecureChannel.ProbeAsync(IPAddress.Loopback, port, probeKey, TimeSpan.FromSeconds(5), default);
            await serve;
            return result;
        }

        Assert.AreEqual(LiteSecureChannel.ProbeResult.KeyMatches, await ProbeWith(Key));
        Assert.AreEqual(LiteSecureChannel.ProbeResult.KeyMismatch, await ProbeWith("fedcba9876543210fedc"));
        Assert.AreEqual(LiteSecureChannel.ProbeResult.NoAnswer, await LiteSecureChannel.ProbeAsync(IPAddress.Loopback, 1, Key, TimeSpan.FromSeconds(2), default));
    }
}
