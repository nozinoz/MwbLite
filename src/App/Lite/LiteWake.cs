// MwbLite: wake a sleeping or switched-off PC with a Wake-on-LAN "magic packet".
//
// A PC that is off can't be asked for its network card (MAC) address, so each PC's MACs are remembered while it is
// connected: from its "hello" answer on the control channel (LiteControl), or, for a PC that doesn't answer (an older
// MwbLite), from the ARP cache. They are kept in addresses.json (LiteAddressBook), which is per account, so the
// service-mode SYSTEM instance and the normal user instance each learn their own.
//
// The packet is 6 bytes of 0xFF followed by the MAC 16 times, sent as UDP broadcasts to ports 9 and 7, on every
// network interface that is up (to 255.255.255.255 and to the interface's directed broadcast address), so it leaves
// by the right card on a PC with several. After sending, the reconnect watchdog (LiteTray.Redial) is told to try that
// PC every few seconds for a couple of minutes, so it connects as soon as it has booted.
//
// Wake-on-LAN needs the sleeping PC's network card to be listening: usually wired Ethernet, with "Wake on Magic
// Packet" enabled in the card's driver settings and in the BIOS/UEFI. Wi-Fi laptops in Modern Standby usually
// can't be woken this way. Nothing here can tell whether the PC woke; the tray just shows it as connected if it did.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteWake
    {
        internal static readonly int[] Ports = { 9, 7 };

        private static readonly TimeSpan RedialFor = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan RedialEvery = TimeSpan.FromSeconds(5);

        // ---- MAC addresses --------------------------------------------------------------------------------------

        /// <summary>"AA-BB-CC-DD-EE-FF" (what MwbLite stores and sends).</summary>
        internal static string FormatMac(byte[] mac) => string.Join("-", mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

        /// <summary>
        /// Parses a MAC written "AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff", "aabb.ccdd.eeff" or "aabbccddeeff". False for
        /// anything else, and for the all-zero and all-ones addresses (never a real card).
        /// </summary>
        internal static bool TryParseMac(string text, out byte[] mac)
        {
            mac = null;
            string hex = new string((text ?? string.Empty).Trim().Where(c => c is not ('-' or ':' or '.')).ToArray());
            if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
            {
                return false;
            }

            byte[] bytes = Convert.FromHexString(hex);
            if (bytes.All(b => b == 0) || bytes.All(b => b == 0xFF))
            {
                return false;
            }

            mac = bytes;
            return true;
        }

        /// <summary>6 bytes of 0xFF, then the MAC 16 times (102 bytes).</summary>
        internal static byte[] MagicPacket(byte[] mac)
        {
            if (mac == null || mac.Length != 6)
            {
                throw new ArgumentException("A MAC address has 6 bytes.", nameof(mac));
            }

            byte[] packet = new byte[6 + (16 * 6)];
            Array.Fill(packet, (byte)0xFF, 0, 6);
            for (int i = 0; i < 16; i++)
            {
                Buffer.BlockCopy(mac, 0, packet, 6 + (i * 6), 6);
            }

            return packet;
        }

        /// <summary>Whether a network adapter is a real network card (not a VM, VPN, Bluetooth or Wi-Fi Direct adapter).</summary>
        internal static bool IsPhysicalAdapter(NetworkInterfaceType type, string name, string description)
        {
            if (type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx))
            {
                return false;
            }

            string text = (name + " " + description).ToLowerInvariant();
            string[] virtualMarkers = { "virtual", "vmware", "vmnet", "virtualbox", "hyper-v", "vethernet", "tap-", "tap windows", "wan miniport", "bluetooth", "loopback", "npcap", "wireguard", "tailscale", "zerotier", "vpn", "pseudo" };
            return !virtualMarkers.Any(text.Contains);
        }

        /// <summary>MACs of this PC's network cards that are up (what "hello" reports), in "AA-BB-CC-DD-EE-FF" form.</summary>
        internal static List<string> LocalMacs()
        {
            var macs = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !IsPhysicalAdapter(ni.NetworkInterfaceType, ni.Name, ni.Description))
                    {
                        continue;
                    }

                    byte[] bytes = ni.GetPhysicalAddress().GetAddressBytes();
                    if (bytes.Length == 6 && TryParseMac(FormatMac(bytes), out _))
                    {
                        macs.Add(FormatMac(bytes));
                    }
                }
            }
            catch (Exception e) when (e is NetworkInformationException or InvalidOperationException)
            {
                LiteLog.Write(nameof(LiteWake), "Could not read this PC's network cards: " + e.Message);
            }

            return macs.Distinct().ToList();
        }

        // ---- what is known --------------------------------------------------------------------------------------

        /// <summary>The MACs remembered for a PC (empty if it was never seen connected).</summary>
        internal static IReadOnlyList<string> KnownMacs(string pc) => LiteAddressBook.GetMacs(pc);

        /// <summary>Whether Wake can do anything for this PC.</summary>
        internal static bool CanWake(string pc) => KnownMacs(pc).Count > 0;

        /// <summary>Remembers MACs a PC reported about itself (invalid ones are dropped).</summary>
        internal static void RememberMacs(string pc, IEnumerable<string> macs) =>
            LiteAddressBook.RememberMacs(pc, (macs ?? Enumerable.Empty<string>()).Where(m => TryParseMac(m, out _)).Select(m => { _ = TryParseMac(m, out byte[] b); return FormatMac(b); }));

        // ---- ARP fallback ---------------------------------------------------------------------------------------

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddress, ref uint physicalAddressLength);

        /// <summary>
        /// For a connected PC that didn't say hello (an older MwbLite): asks the network for the MAC behind its IPv4
        /// address (the ARP cache, or one ARP request), if that address is on this PC's own subnet. An IPv6 connection
        /// falls back to the PC's IPv4 addresses from DNS and the address book. Never throws.
        /// </summary>
        internal static async Task LearnFromArpAsync(string pc, IPAddress connectedAddress)
        {
            try
            {
                var candidates = new List<IPAddress>();
                void Add(IPAddress a)
                {
                    if (a != null && (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a) is { AddressFamily: AddressFamily.InterNetwork } v4 && !candidates.Contains(v4))
                    {
                        candidates.Add(v4);
                    }
                }

                Add(connectedAddress);
                if (candidates.Count == 0)
                {
                    foreach (IPAddress a in LiteAddressBook.Get(pc))
                    {
                        Add(a);
                    }

                    try
                    {
                        foreach (IPAddress a in await Dns.GetHostAddressesAsync(pc).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
                        {
                            Add(a);
                        }
                    }
                    catch (Exception e) when (e is SocketException or TimeoutException)
                    {
                    }
                }

                foreach (IPAddress ip in candidates.Where(LiteDiagnostics.IsOnLocalSubnet))
                {
                    byte[] mac = await Task.Run(() => ArpLookup(ip)).ConfigureAwait(false);
                    if (mac != null)
                    {
                        RememberMacs(pc, new[] { FormatMac(mac) });
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteWake), $"ARP lookup for {pc} failed: {e.Message}");
            }
        }

        private static byte[] ArpLookup(IPAddress ip)
        {
            byte[] mac = new byte[8];
            uint length = (uint)mac.Length;
            int rc = SendARP(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), 0, mac, ref length);
            if (rc != 0 || length != 6)
            {
                return null;
            }

            byte[] six = mac.Take(6).ToArray();
            return TryParseMac(FormatMac(six), out _) ? six : null;
        }

        // ---- waking ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Sends the magic packet for every MAC known for <paramref name="pc"/> out of every network interface that
        /// is up, in the background, then asks the redial watchdog to try that PC soon. Returns false if no MAC is
        /// known (nothing was sent).
        /// </summary>
        internal static bool Wake(string pc)
        {
            string[] macs = KnownMacs(pc).ToArray();
            if (macs.Length == 0)
            {
                return false;
            }

            LiteLog.Write(nameof(LiteWake), $"Waking {pc} ({string.Join(", ", macs)}).");
            _ = Task.Run(() =>
            {
                try
                {
                    int sent = macs.Where(m => TryParseMac(m, out _)).Sum(m =>
                    {
                        _ = TryParseMac(m, out byte[] bytes);
                        return SendEverywhere(MagicPacket(bytes));
                    });
                    LiteLog.Write(nameof(LiteWake), $"Sent {sent} wake-up packets for {pc}.");
                }
                catch (Exception e)
                {
                    LiteLog.Write(nameof(LiteWake), $"Waking {pc} failed: {e.Message}");
                }
            });

            LiteTray.RedialSoon(RedialFor, RedialEvery);
            return true;
        }

        /// <summary>Broadcasts a packet from each up IPv4 interface (limited and directed broadcast, each port). Returns how many sends worked.</summary>
        internal static int SendEverywhere(byte[] packet)
        {
            int sent = 0;
            foreach ((IPAddress local, IPAddress directed) in BroadcastTargets())
            {
                foreach (IPAddress target in new[] { IPAddress.Broadcast, directed }.Distinct())
                {
                    foreach (int port in Ports)
                    {
                        if (SendFrom(packet, local, new IPEndPoint(target, port)))
                        {
                            sent++;
                        }
                    }
                }
            }

            return sent;
        }

        /// <summary>Sends one UDP datagram from a given local address (so it leaves by that interface).</summary>
        internal static bool SendFrom(byte[] packet, IPAddress local, IPEndPoint target)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                return udp.Send(packet, packet.Length, target) == packet.Length;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>Each up IPv4 interface address with its directed broadcast address (address OR NOT mask).</summary>
        internal static List<(IPAddress Local, IPAddress Directed)> BroadcastTargets()
        {
            var list = new List<(IPAddress, IPAddress)>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null))
                    {
                        list.Add((u.Address, DirectedBroadcast(u.Address, u.IPv4Mask)));
                    }
                }
            }
            catch (Exception e) when (e is NetworkInformationException or InvalidOperationException)
            {
                LiteLog.Write(nameof(LiteWake), "Could not list the network interfaces: " + e.Message);
            }

            return list;
        }

        internal static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
        {
            byte[] a = address.GetAddressBytes(), m = mask.GetAddressBytes();
            for (int i = 0; i < 4; i++)
            {
                a[i] = (byte)(a[i] | ~m[i]);
            }

            return new IPAddress(a);
        }
    }
}
