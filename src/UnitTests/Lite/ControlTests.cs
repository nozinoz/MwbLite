// MwbLite: tests for the control channel (LiteControl): message framing, and requests and replies over a real
// loopback TLS connection with a throwaway certificate and the security-key proof.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class ControlTests
{
    private const string ServerKey = "0123456789abcdef0123";
    private const string OtherKey = "fedcba9876543210fedc";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    // The client and the server share one process, so a test that needs different keys sets this (only on the
    // client's side of the call; the server's threads captured their context earlier).
    private static readonly AsyncLocal<string?> ClientKey = new();

    private static X509Certificate2 cert = null!;
    private static Func<X509Certificate2> originalCertificate = null!;
    private static Func<string> originalKey = null!;
    private static Func<int> originalPort = null!;
    private static string dir = null!;
    private static bool savedSaveToDisk;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        cert = LiteSecureChannel.CreateCertificate(X509KeyStorageFlags.DefaultKeySet);
        originalCertificate = LiteSecureChannel.Certificate;
        originalKey = LiteSecureChannel.SecurityKey;
        originalPort = LiteControl.PortProvider;
        LiteSecureChannel.Certificate = () => cert;
        LiteSecureChannel.SecurityKey = () => ClientKey.Value ?? ServerKey;
        dir = Path.Combine(Path.GetTempPath(), "MwbLiteControlTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        savedSaveToDisk = LiteAddressBook.SaveToDisk;
        LiteAddressBook.SaveToDisk = true; // another test turns it off
        LiteAddressBook.UseFileForTests(Path.Combine(dir, "addresses.json"));
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        LiteControl.Stop();
        LiteControl.PortProvider = originalPort;
        LiteSecureChannel.Certificate = originalCertificate;
        LiteSecureChannel.SecurityKey = originalKey;
        LiteAddressBook.SaveToDisk = savedSaveToDisk;
        LiteAddressBook.UseFileForTests(null);
        cert.Dispose();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestCleanup]
    public void TestCleanup()
    {
        ClientKey.Value = null;
        LiteControl.Stop();
    }

    // Listens on a free loopback-reachable port and points the client side at it.
    private static int StartListening()
    {
        LiteControl.PortProvider = () => 0;
        LiteControl.Start();
        Assert.IsTrue(LiteControl.IsListening, "the listener should have bound");
        int port = LiteControl.ListeningPort;
        Assert.AreNotEqual(0, port);
        LiteControl.PortProvider = () => port;
        return port;
    }

    private static T Run<T>(Task<T> task) => task.WaitAsync(Wait).GetAwaiter().GetResult();

    // ---- framing -------------------------------------------------------------------------------------------

    [TestMethod]
    public void PortIsTcpPortPlusThree()
    {
        Assert.AreEqual(15103, LiteControl.PortFor(15100));
    }

    [TestMethod]
    public void MessageRoundTripsThroughTheFraming()
    {
        var message = new JsonObject { ["op"] = "hello", ["text"] = "héllo ✓", ["plain"] = "abc", ["n"] = 42 };
        using var stream = new MemoryStream();
        LiteControl.WriteMessage(stream, message);

        byte[] bytes = stream.ToArray();
        int length = BitConverter.ToInt32(bytes, 0);
        Assert.AreEqual(bytes.Length - 4, length, "4-byte little-endian length, then the JSON");
        StringAssert.Contains(Encoding.UTF8.GetString(bytes, 4, length), "\"plain\":\"abc\",\"n\":42}");

        stream.Position = 0;
        JsonObject back = LiteControl.ReadMessage(stream);
        Assert.AreEqual("hello", (string?)back["op"]);
        Assert.AreEqual("héllo ✓", (string?)back["text"]);
        Assert.AreEqual(42, (int?)back["n"]);
    }

    [TestMethod]
    public void OversizeMessagesAreRejected()
    {
        // Reading: the length alone is enough to refuse (the body is never read).
        byte[] header = BitConverter.GetBytes(LiteControl.MaxMessageBytes + 1);
        Assert.ThrowsExactly<InvalidDataException>(() => LiteControl.ReadMessage(new MemoryStream(header)));
        Assert.ThrowsExactly<InvalidDataException>(() => LiteControl.ReadMessage(new MemoryStream(BitConverter.GetBytes(-1))));

        // Writing: one over the limit is refused, the limit itself is fine.
        var big = new JsonObject { ["x"] = new string('a', LiteControl.MaxMessageBytes) };
        Assert.ThrowsExactly<InvalidDataException>(() => LiteControl.WriteMessage(new MemoryStream(), big));

        int overhead = new JsonObject { ["x"] = string.Empty }.ToJsonString().Length;
        var exact = new JsonObject { ["x"] = new string('a', LiteControl.MaxMessageBytes - overhead) };
        using var ok = new MemoryStream();
        LiteControl.WriteMessage(ok, exact);
        Assert.AreEqual(LiteControl.MaxMessageBytes + 4, ok.Length);
    }

    [TestMethod]
    public void MessagesThatAreNotJsonObjectsAreRejected()
    {
        foreach (string text in new[] { "[1,2]", "42", "not json", "" })
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] frame = new byte[4 + body.Length];
            BitConverter.GetBytes(body.Length).CopyTo(frame, 0);
            body.CopyTo(frame, 4);
            Assert.ThrowsExactly<InvalidDataException>(() => LiteControl.ReadMessage(new MemoryStream(frame)), text);
        }

        Assert.ThrowsExactly<EndOfStreamException>(() => LiteControl.ReadMessage(new MemoryStream(new byte[] { 5, 0, 0, 0, (byte)'{' })));
    }

    // ---- dispatch ------------------------------------------------------------------------------------------

    [TestMethod]
    public void UnknownOpAndHandlerErrorsAreReportedNotThrown()
    {
        JsonObject unknown = LiteControl.Dispatch(new JsonObject { ["op"] = "no-such-op" });
        Assert.IsFalse((bool)unknown["ok"]!);
        Assert.AreEqual("unknown op", (string?)unknown["error"]);

        Assert.AreEqual("unknown op", (string?)LiteControl.Dispatch(new JsonObject())["error"]);
        Assert.AreEqual("unknown op", (string?)LiteControl.Dispatch(new JsonObject { ["op"] = 5 })["error"]);

        LiteControl.RegisterHandler("test-throws", _ => throw new InvalidOperationException("boom"));
        JsonObject failed = LiteControl.Dispatch(new JsonObject { ["op"] = "test-throws" });
        Assert.IsFalse((bool)failed["ok"]!);
        Assert.AreEqual("boom", (string?)failed["error"]);

        LiteControl.RegisterHandler("test-bare", _ => null!);
        Assert.AreEqual("{\"ok\":true}", LiteControl.Dispatch(new JsonObject { ["op"] = "test-bare" }).ToJsonString());
    }

    // ---- over a real connection ----------------------------------------------------------------------------

    [TestMethod]
    public void RequestAndReplyOverTheSecureLoopbackConnection()
    {
        LiteControl.RegisterHandler("test-echo", request => new JsonObject { ["got"] = (string?)request["value"], ["ok"] = false /* ignored: the framework owns "ok" */ });
        StartListening();

        JsonObject? reply = Run(LiteControl.SendAsync("127.0.0.1", "test-echo", new JsonObject { ["value"] = "abc" }, CancellationToken.None));

        Assert.IsNotNull(reply);
        Assert.IsTrue((bool)reply["ok"]!);
        Assert.AreEqual("abc", (string?)reply["got"]);

        // Another request on a fresh connection, and an unknown op.
        JsonObject? unknown = Run(LiteControl.SendAsync("127.0.0.1", "nope", null, CancellationToken.None));
        Assert.IsNotNull(unknown);
        Assert.IsFalse((bool)unknown["ok"]!);
        Assert.AreEqual("unknown op", (string?)unknown["error"]);
    }

    [TestMethod]
    public void WrongSecurityKeyGetsNoAnswer()
    {
        StartListening();
        ClientKey.Value = OtherKey;

        Assert.IsNull(Run(LiteControl.SendAsync("127.0.0.1", "hello", null, CancellationToken.None)));
    }

    [TestMethod]
    public void NoListenerGetsNull()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        LiteControl.PortProvider = () => closed;

        Assert.IsNull(Run(LiteControl.SendAsync("127.0.0.1", "hello", null, CancellationToken.None)));
    }

    [TestMethod]
    public void NonTlsClientIsDroppedAndTheListenerKeepsWorking()
    {
        int port = StartListening();

        using (var plain = new TcpClient())
        {
            plain.Connect(IPAddress.Loopback, port);
            NetworkStream s = plain.GetStream();
            byte[] junk = Encoding.ASCII.GetBytes("this is not TLS\r\n");
            s.Write(junk);
            s.ReadTimeout = 15000;
            try
            {
                _ = s.Read(new byte[16]); // the server closes (or sends a TLS alert); it never answers
            }
            catch (IOException)
            {
            }
        }

        Assert.IsNotNull(Run(LiteControl.SendAsync("127.0.0.1", "hello", null, CancellationToken.None)));
    }

    [TestMethod]
    public void HelloAnswersWithThisPcsDetailsAndRaisesPeerHello()
    {
        StartListening();
        var seen = new TaskCompletionSource<(string Peer, PeerInfo Info)>();
        void OnHello(string peer, PeerInfo info) => seen.TrySetResult((peer, info));
        LiteControl.PeerHello += OnHello;
        try
        {
            PeerInfo? info = Run(LiteControl.HelloAsync("127.0.0.1", CancellationToken.None));

            Assert.IsNotNull(info);
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Name));
            Assert.AreEqual(LiteUpdate.CurrentText, info.Version);
            foreach (string mac in info.Macs)
            {
                Assert.IsTrue(LiteWake.TryParseMac(mac, out byte[]? bytes) && LiteWake.FormatMac(bytes!) == mac, mac);
            }

            Assert.IsTrue(info.ScreenWidth >= 0 && info.ScreenHeight >= 0 && info.ScreenHeightMm >= 0);
            (string Peer, PeerInfo Info) raised = Run(seen.Task);
            Assert.AreEqual("127.0.0.1", raised.Peer);
            Assert.AreEqual(info, raised.Info);
        }
        finally
        {
            LiteControl.PeerHello -= OnHello;
        }
    }

    [TestMethod]
    public void HelloRemembersTheMacsForWake()
    {
        LiteControl.RegisterHandler("hello", _ => new JsonObject
        {
            ["name"] = "OFFICE-PC",
            ["version"] = "9.9.9",
            ["macs"] = new JsonArray("aa:bb:cc:dd:ee:01", "not a mac", "AA-BB-CC-DD-EE-02"),
            ["screenWidth"] = 2560,
            ["screenHeight"] = 1440,
            ["screenHeightMm"] = 335,
        });
        try
        {
            StartListening();
            PeerInfo? info = Run(LiteControl.HelloAsync("127.0.0.1", CancellationToken.None));

            Assert.IsNotNull(info);
            Assert.AreEqual("OFFICE-PC", info.Name);
            Assert.AreEqual("9.9.9", info.Version);
            CollectionAssert.AreEqual(new[] { "AA-BB-CC-DD-EE-01", "AA-BB-CC-DD-EE-02" }, info.Macs.ToArray());
            Assert.AreEqual((2560, 1440, 335), (info.ScreenWidth, info.ScreenHeight, info.ScreenHeightMm));
            // The newest MACs come first (an earlier test may have stored others under the same peer).
            CollectionAssert.AreEqual(new[] { "AA-BB-CC-DD-EE-01", "AA-BB-CC-DD-EE-02" }, LiteWake.KnownMacs("127.0.0.1").Take(2).ToArray());
        }
        finally
        {
            LiteControl.RegisterHandler("hello", _ => LiteControl.HelloReply());
        }
    }

    [TestMethod]
    public void HelloToAnOlderOrSilentPcIsNull()
    {
        Assert.IsNull(LiteControl.ParseHello("PC", null));
        Assert.IsNull(LiteControl.ParseHello("PC", new JsonObject { ["ok"] = false, ["error"] = "unknown op" }));

        // A reply with missing or odd fields still parses, with the PC's layout name as the fallback.
        PeerInfo? sparse = LiteControl.ParseHello("PC", new JsonObject { ["ok"] = true, ["macs"] = "oops", ["screenWidth"] = "wide" });
        Assert.IsNotNull(sparse);
        Assert.AreEqual("PC", sparse.Name);
        Assert.AreEqual(0, sparse.Macs.Count);
        Assert.AreEqual(0, sparse.ScreenWidth);
    }

    [TestMethod]
    public void StartTwiceAndStopAreHarmless()
    {
        int port = StartListening();
        LiteControl.Start();
        Assert.AreEqual(port, LiteControl.ListeningPort);

        LiteControl.Stop();
        LiteControl.Stop();
        Assert.IsFalse(LiteControl.IsListening);
        Assert.IsNull(Run(LiteControl.SendAsync("127.0.0.1", "hello", null, CancellationToken.None)));
    }

    [TestMethod]
    public void StrictChannelRefusesAPeerThatSpeaksNoTls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndpoint);
        using Socket server = listener.AcceptSocket();
        server.Send(Encoding.ASCII.GetBytes("MWB plain protocol\r\n"));

        // No fallback to a plain protocol: the handshake simply fails.
        Exception? failure = null;
        try
        {
            LiteSecureChannel.EstablishStrict(client, isClient: true, ServerKey).Dispose();
        }
        catch (Exception e) when (e is System.Security.Authentication.AuthenticationException or IOException)
        {
            failure = e;
        }

        Assert.IsNotNull(failure, "a peer without TLS must be refused");
    }
}
