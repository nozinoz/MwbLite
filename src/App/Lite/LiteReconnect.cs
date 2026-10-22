// MwbLite: reconnect quickly after sleep and network changes.
//
// Upstream reopens its sockets only when the network disappears entirely (NetworkAvailabilityChanged). A Wi-Fi
// switch, a new DHCP lease or a VPN coming up changes the PC's addresses without that event, and after sleep the
// sockets can look connected while the peer has long gone. This watches the set of local unicast addresses and
// asks InitAndCleanup to close and reopen the sockets when it changes (debounced, as one change fires many
// events). Power transitions are handled in InitAndCleanup.SystemEvents_PowerModeChanged.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteReconnect
    {
        private const int DebounceMs = 3000;

        private static readonly Lock Gate = new();
        private static Timer debounce;
        private static string lastAddresses;

        internal static void Start()
        {
            lock (Gate)
            {
                if (debounce != null)
                {
                    return;
                }

                lastAddresses = CurrentAddresses();
                debounce = new Timer(_ => CheckAddresses(), null, Timeout.Infinite, Timeout.Infinite);
            }

            NetworkChange.NetworkAddressChanged += (_, _) => debounce?.Change(DebounceMs, Timeout.Infinite);
        }

        private static void CheckAddresses()
        {
            try
            {
                string now = CurrentAddresses();
                lock (Gate)
                {
                    if (now == lastAddresses)
                    {
                        return;
                    }

                    LiteLog.Write(nameof(LiteReconnect), "Network addresses changed (" + lastAddresses + " -> " + now + "); reopening connections.");
                    lastAddresses = now;
                }

                InitAndCleanup.ScheduleReopenSocketsDueToNetworkChanges();
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteReconnect), "Address check failed: " + e.Message);
            }
        }

        /// <summary>
        /// TCP keepalive: probe after 10 s of silence, every 3 s, give up after 3 misses. A blocking read on a
        /// connection to a PC that slept, lost Wi-Fi or was unplugged then fails in about 20 s instead of never.
        /// </summary>
        internal static void EnableKeepAlive(Socket socket)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                LiteLog.Write(nameof(LiteReconnect), "Could not enable keepalive: " + e.Message);
            }
        }

        /// <summary>Sorted unicast addresses of interfaces that are up (loopback and tunnels excluded).</summary>
        internal static string CurrentAddresses()
        {
            var list = new List<string>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork ||
                        (a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal))
                    {
                        list.Add(a.Address.ToString());
                    }
                }
            }

            return string.Join(",", list.OrderBy(s => s, StringComparer.Ordinal));
        }
    }
}
