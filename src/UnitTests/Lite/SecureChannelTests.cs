// MwbLite: tests for the TLS + security-key proof around MWB connections, over real loopback sockets.
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
        LiteSecureChannel.Pbkdf2Iterations = 600_000;
        cert.Dispose();
    }

    [TestInitialize]
    public void TestInitialize()
    {
        LiteSecureChannel.Pbkdf2Iterations = 1000; // the real 600,000 would add ~0.3 s to every test
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

    // A stand-in for MwbLite 0.6 to 0.8 (protocol v1): after TLS both sides send "MWBL" | 1 | HMAC(HKDF(key), role |
    // certificates) at once, then check the other's and exchange a verdict byte.
    private static void V1Prove(SslStream ssl, bool isClient, string key)
    {
        byte[] local = ssl.LocalCertificate!.GetRawCertData(), remote = ssl.RemoteCertificate!.GetRawCertData();
        byte[] clientCert = isClient ? local : remote, serverCert = isClient ? remote : local;
        byte[] k = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(key), 32, Encoding.UTF8.GetBytes("MwbLite channel v1"));
        byte[] ProofFor(char role) => HMACSHA256.HashData(k, new[] { (byte)role }.Concat(SHA256.HashData(clientCert)).Concat(SHA256.HashData(serverCert)).ToArray());

        ssl.Write(Encoding.ASCII.GetBytes("MWBL").Concat(new byte[] { 1 }).Concat(ProofFor(isClient ? 'C' : 'S')).ToArray());
        ssl.Flush();
        byte[] theirs = new byte[37];
        ssl.ReadExactly(theirs);
        if (theirs[4] != 1)
        {
            throw new InvalidDataException("not a v1 peer");
        }

        bool ok = CryptographicOperations.FixedTimeEquals(theirs.AsSpan(5), ProofFor(isClient ? 'S' : 'C'));
        ssl.WriteByte(ok ? (byte)1 : (byte)0);
        ssl.Flush();
        if (!ok || ssl.ReadByte() != 1)
        {
            throw new InvalidDataException("v1 key mismatch");
        }
    }

    private static Task V1Peer(Socket socket, bool isClient, string key) =>
        Task.Run(() =>
        {
            socket.ReceiveTimeout = socket.SendTimeout = 5000;
            using var ssl = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
            LiteSecureChannel.Authenticate(ssl, isClient);
            V1Prove(ssl, isClient, key);
        });

    [TestMethod]
    public void WrongKeyGetsOnlyAMismatchVerdictAndNeverTheServersProof()
    {
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            Task<Stream> serving = Task.Run(() => LiteSecureChannel.Establish(server, isClient: false, "STRANGER", Key));

            // An unauthenticated peer: its own certificate, a guessed proof. It must get nothing it could test guesses against.
            client.ReceiveTimeout = client.SendTimeout = 5000;
            using var ssl = new SslStream(new NetworkStream(client, ownsSocket: false), leaveInnerStreamOpen: false);
            LiteSecureChannel.Authenticate(ssl, isClient: true);
            ssl.Write(Encoding.ASCII.GetBytes("MWBL").Concat(new byte[] { 2 }).Concat(new byte[32]).ToArray());
            ssl.Flush();

            Exception e = Unwrap(serving);
            Assert.IsInstanceOfType(e, typeof(KeyMismatchException));
            server.Close();

            var received = new MemoryStream();
            try
            {
                byte[] buffer = new byte[256];
                int n;
                while ((n = ssl.Read(buffer, 0, buffer.Length)) > 0)
                {
                    received.Write(buffer, 0, n);
                }
            }
            catch (IOException)
            {
            }

            CollectionAssert.AreEqual(new byte[] { 0 }, received.ToArray(), "only the mismatch verdict, no proof");
            Assert.IsTrue(LiteSecureChannel.RecentKeyMismatch("STRANGER"));
        }
    }

    [TestMethod]
    public void ClientRejectsAServerThatCannotProveTheKey()
    {
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            Task<Stream> connecting = Task.Run(() => LiteSecureChannel.Establish(client, isClient: true, "IMPOSTOR", Key));

            server.ReceiveTimeout = server.SendTimeout = 5000;
            using var ssl = new SslStream(new NetworkStream(server, ownsSocket: false), leaveInnerStreamOpen: false);
            LiteSecureChannel.Authenticate(ssl, isClient: false);
            ssl.ReadExactly(new byte[37]); // the client's proof (an impostor can't check it, but says it did)
            ssl.Write(new byte[] { 1 }.Concat(Encoding.ASCII.GetBytes("MWBL")).Concat(new byte[] { 2 }).Concat(new byte[32]).ToArray());
            ssl.Flush();

            Assert.IsInstanceOfType(Unwrap(connecting), typeof(KeyMismatchException));
            Assert.AreEqual(0, ssl.ReadByte(), "the client answers mismatch");
        }
    }

    [TestMethod]
    public void OlderClientTalkingToThisServerIsReportedAsOlder()
    {
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            Task v1Client = V1Peer(client, isClient: true, Key);
            Exception e = Unwrap(Task.Run(() => LiteSecureChannel.Establish(server, isClient: false, "V1CLIENT", Key)));
            Assert.IsInstanceOfType(e, typeof(InsecurePeerException));
            Assert.IsTrue(LiteSecureChannel.RecentInsecureRejected("V1CLIENT"));
            Assert.IsFalse(LiteSecureChannel.RecentKeyMismatch("V1CLIENT"));
            server.Close();
            Assert.IsNotNull(Unwrap(v1Client), "the old client just sees the connection end");
        }
    }

    [TestMethod]
    public void ThisClientTalkingToAnOlderServerIsReportedAsOlder()
    {
        var (client, server) = Pair();
        using (client)
        using (server)
        {
            Task v1Server = V1Peer(server, isClient: false, Key);
            Exception e = Unwrap(Task.Run(() => LiteSecureChannel.Establish(client, isClient: true, "V1SERVER", Key)));
            Assert.IsInstanceOfType(e, typeof(InsecurePeerException));
            Assert.IsTrue(LiteSecureChannel.RecentInsecureRejected("V1SERVER"));
            Assert.IsFalse(LiteSecureChannel.RecentKeyMismatch("V1SERVER"));
            client.Close();
            Assert.IsNotNull(Unwrap(v1Server));
        }
    }

    [TestMethod]
    public void TheDerivedKeyIsCachedPerKey()
    {
        int before = LiteSecureChannel.Pbkdf2Runs;
        byte[] first = LiteSecureChannel.DeriveKey("cache-test-key-0000001");
        Assert.AreEqual(before + 1, LiteSecureChannel.Pbkdf2Runs);
        Assert.AreSame(first, LiteSecureChannel.DeriveKey("cache-test-key-0000001"), "a second connection with the same key");
        Assert.AreEqual(before + 1, LiteSecureChannel.Pbkdf2Runs);

        CollectionAssert.AreNotEqual(first, LiteSecureChannel.DeriveKey("cache-test-key-0000002"));
        Assert.AreEqual(before + 2, LiteSecureChannel.Pbkdf2Runs);

        // Only the last four keys are kept: touch 0000001 again, push four others in, and the oldest is gone.
        LiteSecureChannel.DeriveKey("cache-test-key-0000001");
        for (int i = 3; i <= 6; i++)
        {
            LiteSecureChannel.DeriveKey("cache-test-key-000000" + i);
        }

        int afterFill = LiteSecureChannel.Pbkdf2Runs;
        LiteSecureChannel.DeriveKey("cache-test-key-0000006");
        Assert.AreEqual(afterFill, LiteSecureChannel.Pbkdf2Runs, "recent keys stay");
        LiteSecureChannel.DeriveKey("cache-test-key-0000001");
        Assert.AreEqual(afterFill + 1, LiteSecureChannel.Pbkdf2Runs, "an old key is derived again");

        // The iteration count is part of the cache key.
        LiteSecureChannel.Pbkdf2Iterations = 2000;
        LiteSecureChannel.DeriveKey("cache-test-key-0000006");
        Assert.AreEqual(afterFill + 2, LiteSecureChannel.Pbkdf2Runs);
    }

    [TestMethod]
    public void TheChannelKeyIsPbkdf2Sha256WithTheAgreedSaltAndIterations()
    {
        LiteSecureChannel.Pbkdf2Iterations = 600_000;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        byte[] derived = LiteSecureChannel.DeriveKey("pbkdf2-test-key-00001");
        timer.Stop();
        Console.WriteLine($"PBKDF2 600000 iterations: {timer.ElapsedMilliseconds} ms");

        byte[] expected = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("pbkdf2-test-key-00001"), Encoding.UTF8.GetBytes("MwbLite channel v2"), 600_000, HashAlgorithmName.SHA256, 32);
        CollectionAssert.AreEqual(expected, derived);
    }

    [TestMethod]
    public void WarmUpFillsTheCacheInTheBackground()
    {
        string key = "warm-up-test-key-0001";
        int before = LiteSecureChannel.Pbkdf2Runs;
        LiteSecureChannel.WarmUp(key);
        for (int i = 0; i < 100 && LiteSecureChannel.Pbkdf2Runs == before; i++)
        {
            System.Threading.Thread.Sleep(50);
        }

        Assert.AreEqual(before + 1, LiteSecureChannel.Pbkdf2Runs);
        LiteSecureChannel.DeriveKey(key);
        Assert.AreEqual(before + 1, LiteSecureChannel.Pbkdf2Runs, "the connection finds it ready");
        LiteSecureChannel.WarmUp(null); // nothing to do, and no failure
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

        // A PC running MwbLite 0.6 to 0.8 answers with the v1 proof straight after TLS.
        Task v1Server = Task.Run(() =>
        {
            using Socket s = listener.Accept();
            try
            {
                V1Peer(s, isClient: false, Key).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Security.Authentication.AuthenticationException)
            {
            }
        });
        Assert.AreEqual(LiteSecureChannel.ProbeResult.OlderVersion, await LiteSecureChannel.ProbeAsync(IPAddress.Loopback, port, Key, TimeSpan.FromSeconds(5), default));
        await v1Server;
    }
}
