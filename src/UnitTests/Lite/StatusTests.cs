// MwbLite: tests for connection status folding, transfer progress and the connection-check helpers.
using System;
using System.Linq;
using System.Net;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Lite;

using Fact = MouseWithoutBorders.Lite.LiteStatus.SocketFact;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class StatusTests
{
    [TestMethod]
    public void SocketsFoldIntoOneStatePerLayoutPc()
    {
        string[] layout = { "LAPTOP1", "MINIPC", "laptop2", string.Empty };
        var sockets = new[]
        {
            new Fact("LAPTOP1", SocketStatus.Connecting, null),
            new Fact("LAPTOP1", SocketStatus.Connected, "192.168.1.21"),
            new Fact("LAPTOP2", SocketStatus.InvalidKey, "192.168.1.22"),
            new Fact("OTHER", SocketStatus.Connected, "192.168.1.99"),
        };

        var pcs = LiteStatus.Fold(layout, "MINIPC", sockets);

        CollectionAssert.AreEqual(new[] { "LAPTOP1", "MINIPC", "laptop2" }, pcs.Select(p => p.Name).ToArray());
        Assert.AreEqual(PcState.Connected, pcs[0].State);
        Assert.AreEqual("192.168.1.21", pcs[0].Address);
        Assert.AreEqual(PcState.ThisPc, pcs[1].State);
        Assert.AreEqual(PcState.KeyMismatch, pcs[2].State);

        var snapshot = new StatusSnapshot { Pcs = pcs };
        Assert.AreEqual(2, snapshot.OtherCount);
        Assert.AreEqual(1, snapshot.ConnectedCount);
        Assert.IsTrue(snapshot.AnyMissing);
    }

    [TestMethod]
    public void ConnectingAndNotConnectedAreDistinguished()
    {
        var pcs = LiteStatus.Fold(new[] { "A", "B" }, "ME", new[] { new Fact("A", SocketStatus.Handshaking, null), new Fact("B", SocketStatus.Error, null) });
        Assert.AreEqual(PcState.Connecting, pcs[0].State);
        Assert.AreEqual(PcState.NotConnected, pcs[1].State);
    }

    [TestMethod]
    public void TrayTextSummarisesAndFitsTheLimit()
    {
        var s = new StatusSnapshot
        {
            Pcs =
            {
                new PcStatus { Name = "MINIPC", State = PcState.ThisPc },
                new PcStatus { Name = "LAPTOP1", State = PcState.Connected },
                new PcStatus { Name = "LAPTOP2-WITH-A-VERY-LONG-NAME", State = PcState.NotConnected },
                new PcStatus { Name = "LAPTOP3-WITH-A-VERY-LONG-NAME", State = PcState.KeyMismatch },
            },
        };

        string text = LiteStatus.TrayText(s);
        Assert.IsTrue(text.StartsWith("MwbLite – 1 of 3 PCs connected", StringComparison.Ordinal), text);
        Assert.IsTrue(text.Length <= 127);

        s.Transfers.Add(new TransferStatus { Name = "Photos", Percent = 45, Detail = "Receiving from LAPTOP1" });
        StringAssert.StartsWith(LiteStatus.TrayText(s), "Photos 45%");
    }

    [TestMethod]
    public void TransferProgressAndCancel()
    {
        var t = LiteTransfers.Begin(@"C:\x\Photos and 1 more" + LiteFileBundle.ZipSuffix, "LAPTOP1", sending: true, total: 4L << 30, socket: null);
        Assert.AreEqual("Photos and 1 more", t.Name);
        Assert.IsTrue(t.IsActive);

        t.Report(3L << 30); // beyond int range
        Assert.AreEqual(0.75, t.Fraction, 1e-9);
        StringAssert.Contains(LiteTransfers.Describe(t, DateTime.UtcNow), "3.0 GB of 4.0 GB");

        LiteTransfers.Cancel(t.Id);
        Assert.IsTrue(t.Cancelled);
        Assert.IsTrue(LiteTransfers.Snapshot(DateTime.UtcNow).Any(x => x.Id == t.Id));

        t.EndedUtc = DateTime.UtcNow; // (Complete also notifies, which needs the app's settings)
        Assert.AreEqual("Cancelled", LiteTransfers.Describe(t, DateTime.UtcNow));
        Assert.IsFalse(LiteTransfers.Snapshot(DateTime.UtcNow + LiteTransfers.KeepFinished + TimeSpan.FromSeconds(1)).Any(x => x.Id == t.Id));
    }

    [TestMethod]
    public void UnknownSizeAndManifestNames()
    {
        var t = LiteTransfers.Begin(@"C:\x\Holiday" + LiteFileBundle.ManifestExtension, "PC", sending: true, total: 0, socket: null);
        t.Preparing = true;
        Assert.AreEqual("Holiday", t.Name);
        Assert.AreEqual(-1, t.Fraction);
        StringAssert.StartsWith(LiteTransfers.Describe(t, DateTime.UtcNow), "Preparing");
        t.EndedUtc = DateTime.UtcNow;
    }

    [TestMethod]
    public void MappedAddressesShowAsPlainIpv4()
    {
        Assert.AreEqual("192.168.1.20", LiteStatus.Display(IPAddress.Parse("::ffff:192.168.1.20")));
        Assert.AreEqual("192.168.1.20", LiteStatus.Display(IPAddress.Parse("192.168.1.20")));
        Assert.AreEqual("fe80::1", LiteStatus.Display(IPAddress.Parse("fe80::1")));
        Assert.IsNull(LiteStatus.Display(null));
    }

    [TestMethod]
    public void SubnetAndNameToIpHelpers()
    {
        byte[] mask = IPAddress.Parse("255.255.255.0").GetAddressBytes();
        Assert.IsTrue(LiteDiagnostics.SameSubnet(IPAddress.Parse("192.168.1.10").GetAddressBytes(), IPAddress.Parse("192.168.1.200").GetAddressBytes(), mask));
        Assert.IsFalse(LiteDiagnostics.SameSubnet(IPAddress.Parse("192.168.1.10").GetAddressBytes(), IPAddress.Parse("192.168.2.10").GetAddressBytes(), mask));

        var map = LiteDiagnostics.ParseName2Ip(new[] { "LAPTOP1 192.168.1.21", "  bad", "laptop2   10.0.0.2 " });
        Assert.AreEqual("192.168.1.21", map["laptop1"]);
        Assert.AreEqual("10.0.0.2", map["LAPTOP2"]);
        Assert.AreEqual(2, map.Count);
    }

    [TestMethod]
    public void AddressListIsStableAndSkipsLoopback()
    {
        string a = LiteReconnect.CurrentAddresses();
        Assert.AreEqual(a, LiteReconnect.CurrentAddresses());
        Assert.IsFalse(a.Split(',').Contains("127.0.0.1"));
    }
}
