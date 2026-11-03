// MwbLite: tests for Wake-on-LAN (LiteWake): MAC parsing and formatting, the magic packet, and the address book's
// storage of MACs next to addresses (including reading files written by older versions).
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class WakeTests
{
    private string dir = null!;
    private string file = null!;
    private bool savedSaveToDisk;

    [TestInitialize]
    public void TestInitialize()
    {
        dir = Path.Combine(Path.GetTempPath(), "MwbLiteWakeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        file = Path.Combine(dir, "addresses.json");
        savedSaveToDisk = LiteAddressBook.SaveToDisk;
        LiteAddressBook.SaveToDisk = true; // another test turns it off
        LiteAddressBook.UseFileForTests(file);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        LiteAddressBook.SaveToDisk = savedSaveToDisk;
        LiteAddressBook.UseFileForTests(null);
        Directory.Delete(dir, recursive: true);
    }

    // ---- MACs ----------------------------------------------------------------------------------------------

    [TestMethod]
    public void FormatsMacsWithDashesAndCapitals()
    {
        Assert.AreEqual("AA-BB-CC-0D-0E-0F", LiteWake.FormatMac(new byte[] { 0xAA, 0xBB, 0xCC, 0x0D, 0x0E, 0x0F }));
    }

    [DataTestMethod]
    [DataRow("AA-BB-CC-DD-EE-FF")]
    [DataRow("aa:bb:cc:dd:ee:ff")]
    [DataRow("aabb.ccdd.eeff")]
    [DataRow("aabbccddeeff")]
    [DataRow("  Aa-bB-Cc-dD-eE-fF  ")]
    public void ParsesCommonMacSpellings(string text)
    {
        Assert.IsTrue(LiteWake.TryParseMac(text, out byte[]? mac));
        CollectionAssert.AreEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, mac);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("AA-BB-CC-DD-EE")]
    [DataRow("AA-BB-CC-DD-EE-FF-00")]
    [DataRow("GG-BB-CC-DD-EE-FF")]
    [DataRow("00-00-00-00-00-00")]
    [DataRow("FF-FF-FF-FF-FF-FF")]
    [DataRow("not a mac")]
    public void RejectsInvalidMacs(string text)
    {
        Assert.IsFalse(LiteWake.TryParseMac(text, out _));
    }

    [TestMethod]
    public void MacRoundTripsThroughTextAndBack()
    {
        byte[] original = { 0x02, 0x00, 0x5E, 0x10, 0x00, 0x01 };
        Assert.IsTrue(LiteWake.TryParseMac(LiteWake.FormatMac(original), out byte[]? parsed));
        CollectionAssert.AreEqual(original, parsed);
    }

    [TestMethod]
    public void OnlyRealNetworkCardsAreReported()
    {
        Assert.IsTrue(LiteWake.IsPhysicalAdapter(NetworkInterfaceType.Ethernet, "Ethernet", "Realtek PCIe GbE Family Controller"));
        Assert.IsTrue(LiteWake.IsPhysicalAdapter(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz"));
        Assert.IsFalse(LiteWake.IsPhysicalAdapter(NetworkInterfaceType.Ethernet, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter"));
        Assert.IsFalse(LiteWake.IsPhysicalAdapter(NetworkInterfaceType.Ethernet, "VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8"));
        Assert.IsFalse(LiteWake.IsPhysicalAdapter(NetworkInterfaceType.Ethernet, "Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)"));
        Assert.IsFalse(LiteWake.IsPhysicalAdapter(NetworkInterfaceType.Wireless80211, "Local Area Connection* 1", "Microsoft Wi-Fi Direct Virtual Adapter"));
        Assert.IsFalse(LiteWake.IsPhysicalAdapter(NetworkInterfaceType.Loopback, "Loopback", "Software Loopback Interface 1"));
        Assert.IsFalse(LiteWake.IsPhysicalAdapter(NetworkInterfaceType.Tunnel, "Teredo", "Teredo Tunneling Pseudo-Interface"));

        foreach (string mac in LiteWake.LocalMacs())
        {
            Assert.IsTrue(LiteWake.TryParseMac(mac, out _), mac);
        }
    }

    // ---- the magic packet ----------------------------------------------------------------------------------

    [TestMethod]
    public void MagicPacketIsSixFFsThenTheMacSixteenTimes()
    {
        byte[] mac = { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB };
        byte[] packet = LiteWake.MagicPacket(mac);

        Assert.AreEqual(102, packet.Length);
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)0xFF, 6).ToArray(), packet.Take(6).ToArray());
        for (int i = 0; i < 16; i++)
        {
            CollectionAssert.AreEqual(mac, packet.Skip(6 + (i * 6)).Take(6).ToArray(), "repetition " + i);
        }
    }

    [TestMethod]
    public void MagicPacketNeedsASixByteMac()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LiteWake.MagicPacket(new byte[5]));
        Assert.ThrowsExactly<ArgumentException>(() => LiteWake.MagicPacket(null!));
    }

    [TestMethod]
    public void PacketIsSentAsUdpFromTheGivenAddress()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Client.ReceiveTimeout = 10000;
        var target = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
        byte[] packet = LiteWake.MagicPacket(new byte[] { 1, 2, 3, 4, 5, 6 });

        Assert.IsTrue(LiteWake.SendFrom(packet, IPAddress.Loopback, target));

        IPEndPoint? from = null;
        CollectionAssert.AreEqual(packet, receiver.Receive(ref from));
        Assert.AreEqual(IPAddress.Loopback, from!.Address);
    }

    [TestMethod]
    public void DirectedBroadcastIsTheAddressWithTheHostBitsSet()
    {
        Assert.AreEqual(IPAddress.Parse("192.168.1.255"), LiteWake.DirectedBroadcast(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")));
        Assert.AreEqual(IPAddress.Parse("10.1.255.255"), LiteWake.DirectedBroadcast(IPAddress.Parse("10.1.4.5"), IPAddress.Parse("255.255.0.0")));
        Assert.AreEqual(IPAddress.Parse("172.16.7.131"), LiteWake.DirectedBroadcast(IPAddress.Parse("172.16.7.130"), IPAddress.Parse("255.255.255.252")));
    }

    [TestMethod]
    public void WakeDoesNothingForAPcWithNoKnownMac()
    {
        Assert.IsFalse(LiteWake.CanWake("NEVER-SEEN"));
        Assert.IsFalse(LiteWake.Wake("NEVER-SEEN"));
    }

    // ---- the address book ----------------------------------------------------------------------------------

    [TestMethod]
    public void MacsAreRememberedWithoutDisturbingAddresses()
    {
        LiteAddressBook.Remember("Laptop", "192.168.1.20");
        LiteWake.RememberMacs("laptop", new[] { "aa:bb:cc:dd:ee:01", "garbage", "AA-BB-CC-DD-EE-02" });

        CollectionAssert.AreEqual(new[] { "AA-BB-CC-DD-EE-01", "AA-BB-CC-DD-EE-02" }, LiteWake.KnownMacs("LAPTOP").ToArray());
        CollectionAssert.AreEqual(new[] { IPAddress.Parse("192.168.1.20") }, LiteAddressBook.Get("LAPTOP"));
        Assert.IsTrue(LiteWake.CanWake("Laptop"));
        Assert.IsFalse(LiteWake.CanWake("Desktop"));

        // Newer MACs go first, older ones stay, repeats are not duplicated.
        LiteWake.RememberMacs("Laptop", new[] { "AA-BB-CC-DD-EE-03", "AA-BB-CC-DD-EE-02" });
        CollectionAssert.AreEqual(new[] { "AA-BB-CC-DD-EE-03", "AA-BB-CC-DD-EE-02", "AA-BB-CC-DD-EE-01" }, LiteWake.KnownMacs("Laptop").ToArray());
    }

    [TestMethod]
    public void MacsSurviveARestart()
    {
        LiteWake.RememberMacs("Desk", new[] { "AA-BB-CC-DD-EE-01" });
        LiteAddressBook.Remember("Desk", "10.0.0.7");

        LiteAddressBook.UseFileForTests(file); // forget memory, read the file again

        CollectionAssert.AreEqual(new[] { "AA-BB-CC-DD-EE-01" }, LiteWake.KnownMacs("Desk").ToArray());
        CollectionAssert.AreEqual(new[] { IPAddress.Parse("10.0.0.7") }, LiteAddressBook.Get("Desk"));
    }

    [TestMethod]
    public void AnAddressesFileFromAnOlderVersionStillLoads()
    {
        File.WriteAllText(file, "{\"OFFICE\":[\"192.168.1.5\",\"192.168.1.6\"],\"LAPTOP\":[\"fe80::1\"]}");
        LiteAddressBook.UseFileForTests(file);

        CollectionAssert.AreEqual(new[] { IPAddress.Parse("192.168.1.5"), IPAddress.Parse("192.168.1.6") }, LiteAddressBook.Get("office"));
        Assert.AreEqual(0, LiteAddressBook.GetMacs("office").Length);

        // Adding a MAC keeps the old entries.
        LiteWake.RememberMacs("office", new[] { "AA-BB-CC-DD-EE-01" });
        LiteAddressBook.UseFileForTests(file);
        CollectionAssert.AreEqual(new[] { IPAddress.Parse("192.168.1.5"), IPAddress.Parse("192.168.1.6") }, LiteAddressBook.Get("OFFICE"));
        CollectionAssert.AreEqual(new[] { "AA-BB-CC-DD-EE-01" }, LiteAddressBook.GetMacs("OFFICE"));
    }

    [TestMethod]
    public void TheFileStaysReadableByAnOlderVersion()
    {
        LiteAddressBook.Remember("Office", "192.168.1.5");
        LiteWake.RememberMacs("Office", new[] { "AA-BB-CC-DD-EE-01" });

        // What the previous version did with the file: a flat name -> list-of-strings map, reading addresses by name.
        var oldReader = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>>(File.ReadAllText(file))!;

        CollectionAssert.AreEqual(new[] { "192.168.1.5" }, oldReader["OFFICE"]);
        Assert.IsTrue(oldReader.Keys.Where(k => !k.Equals("OFFICE", StringComparison.Ordinal)).All(k => k.StartsWith("mac:", StringComparison.Ordinal)), "MACs live under mac: keys only");
        Assert.IsFalse(oldReader.Values.SelectMany(v => v).Where(v => IPAddress.TryParse(v, out _)).Except(new[] { "192.168.1.5" }).Any(), "no MAC looks like an IP address");
    }

    [TestMethod]
    public void ACorruptFileIsIgnoredNotFatal()
    {
        File.WriteAllText(file, "{ this is not json");
        LiteAddressBook.UseFileForTests(file);

        Assert.AreEqual(0, LiteAddressBook.GetMacs("X").Length);
        LiteWake.RememberMacs("X", new[] { "AA-BB-CC-DD-EE-01" });
        CollectionAssert.AreEqual(new[] { "AA-BB-CC-DD-EE-01" }, LiteAddressBook.GetMacs("X"));
    }
}
