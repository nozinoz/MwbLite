// MwbLite: live connection status per PC in the layout, for the tray and the Settings window.
//
// Folded from the socket list (SocketStuff.TcpSockets) the same way upstream's matrix form did: a PC is connected
// when any socket to it has finished the handshake, "key mismatch" when one was rejected for the security key.
// Reading it never sends anything (unlike Common.IsConnectedTo). The main process also writes it to status.json
// (no secrets) so the Settings window can show it when MwbLite runs as a service.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal enum PcState
    {
        ThisPc,
        Connected,
        Connecting,
        KeyMismatch,
        OlderVersion,
        NotConnected,
    }

    internal sealed class PcStatus
    {
        public string Name { get; set; } = string.Empty;

        public PcState State { get; set; }

        public string Address { get; set; }
    }

    internal sealed class TransferStatus
    {
        public string Name { get; set; } = string.Empty;

        public string Detail { get; set; } = string.Empty;

        public int Percent { get; set; }
    }

    internal sealed class StatusSnapshot
    {
        public DateTime UpdatedUtc { get; set; }

        public string ThisPc { get; set; } = string.Empty;

        public bool ServiceMode { get; set; }

        public List<PcStatus> Pcs { get; set; } = new();

        public List<TransferStatus> Transfers { get; set; } = new();

        internal int OtherCount => Pcs.Count(p => p.State != PcState.ThisPc);

        internal int ConnectedCount => Pcs.Count(p => p.State == PcState.Connected);

        internal bool AnyMissing => Pcs.Any(p => p.State is PcState.NotConnected or PcState.Connecting or PcState.KeyMismatch or PcState.OlderVersion);

        /// <summary>Changes only when something the user would see changes (not the timestamp).</summary>
        internal string Signature => string.Join(";", Pcs.Select(p => p.Name + "=" + p.State)) + "|" + string.Join(";", Transfers.Select(t => t.Name + "=" + t.Percent));
    }

    internal static class LiteStatus
    {
        internal static StatusSnapshot Current { get; private set; } = new();

        internal static string StateText(PcState state) => state switch
        {
            PcState.ThisPc => "This PC",
            PcState.Connected => "Connected",
            PcState.Connecting => "Connecting…",
            PcState.KeyMismatch => "Security key doesn't match",
            PcState.OlderVersion => "Older version, not secure (refused)",
            _ => "Not connected",
        };

        /// <summary>Socket facts that matter for status (testable without real sockets).</summary>
        internal readonly record struct SocketFact(string MachineName, SocketStatus Status, string Address);

        internal static List<PcStatus> Fold(IEnumerable<string> layout, string thisPc, IEnumerable<SocketFact> sockets)
        {
            var facts = sockets.ToList();
            var result = new List<PcStatus>();
            foreach (string raw in layout)
            {
                string name = (raw ?? string.Empty).Trim();
                if (name.Length == 0 || result.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (name.Equals(thisPc, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new PcStatus { Name = name, State = PcState.ThisPc });
                    continue;
                }

                var mine = facts.Where(f => string.Equals(f.MachineName?.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
                SocketFact connected = mine.FirstOrDefault(f => f.Status == SocketStatus.Connected);
                PcState state = connected.MachineName != null ? PcState.Connected
                    : mine.Any(f => f.Status == SocketStatus.InvalidKey) ? PcState.KeyMismatch
                    : mine.Any(f => f.Status is SocketStatus.Resolving or SocketStatus.Connecting or SocketStatus.Handshaking) ? PcState.Connecting
                    : PcState.NotConnected;
                result.Add(new PcStatus { Name = name, State = state, Address = connected.MachineName != null ? connected.Address : null });
            }

            return result;
        }

        /// <summary>Rebuilds <see cref="Current"/> from MWB's live state. UI thread (or any thread; it only reads).</summary>
        internal static StatusSnapshot Refresh()
        {
            var facts = new List<SocketFact>();
            SocketStuff sk = Common.Sk;
            if (sk != null)
            {
                lock (sk.TcpSocketsLock)
                {
                    foreach (TcpSk t in sk.TcpSockets ?? new List<TcpSk>())
                    {
                        string address = Display(t.Address);
                        try
                        {
                            address ??= Display((t.BackingSocket?.RemoteEndPoint as IPEndPoint)?.Address);
                        }
                        catch (Exception e) when (e is ObjectDisposedException or System.Net.Sockets.SocketException)
                        {
                        }

                        facts.Add(new SocketFact(t.MachineName, t.Status, address));
                    }
                }
            }

            DateTime now = DateTime.UtcNow;
            var snapshot = new StatusSnapshot
            {
                UpdatedUtc = now,
                ThisPc = Common.MachineName?.Trim() ?? string.Empty,
                ServiceMode = LitePaths.IsSystem,
                Pcs = WithSecureChannelEvents(Fold(MachineStuff.MachineMatrix ?? Array.Empty<string>(), Common.MachineName?.Trim() ?? string.Empty, facts)),
                Transfers = LiteTransfers.Snapshot(now).Select(t => new TransferStatus
                {
                    Name = t.Name,
                    Detail = LiteTransfers.Describe(t, now),
                    Percent = t.Fraction < 0 ? -1 : (int)(t.Fraction * 100),
                }).ToList(),
            };
            Current = snapshot;
            return snapshot;
        }

        // Rejections by the secure channel close the socket at once, so they are remembered there for a while.
        private static List<PcStatus> WithSecureChannelEvents(List<PcStatus> pcs)
        {
            foreach (PcStatus p in pcs.Where(p => p.State is PcState.NotConnected or PcState.Connecting))
            {
                if (LiteSecureChannel.RecentKeyMismatch(p.Name))
                {
                    p.State = PcState.KeyMismatch;
                }
                else if (LiteSecureChannel.RecentInsecureRejected(p.Name))
                {
                    p.State = PcState.OlderVersion;
                }
            }

            return pcs;
        }

        /// <summary>"192.168.1.20" rather than "::ffff:192.168.1.20" (MWB's sockets are dual-mode IPv6).</summary>
        internal static string Display(IPAddress address) =>
            address == null ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

        /// <summary>Tray tooltip (Windows allows 127 characters).</summary>
        internal static string TrayText(StatusSnapshot s)
        {
            string text;
            TransferStatus busy = s.Transfers.FirstOrDefault();
            if (busy != null)
            {
                text = busy.Percent >= 0 ? $"{busy.Name} {busy.Percent}%\n{busy.Detail}" : $"{busy.Name}\n{busy.Detail}";
            }
            else if (s.OtherCount == 0)
            {
                text = "MwbLite – no other PCs set up yet";
            }
            else
            {
                var lines = new List<string> { $"MwbLite – {s.ConnectedCount} of {s.OtherCount} PCs connected" };
                lines.AddRange(s.Pcs.Where(p => p.State != PcState.ThisPc).Select(p => p.Name + ": " + StateText(p.State)));
                text = string.Join("\n", lines);
            }

            return text.Length <= 127 ? text : text[..126] + "…";
        }

        internal static void Write(StatusSnapshot s, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, LiteConfig.JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteStatus), "Could not write " + path + ": " + e.Message);
            }
        }

        /// <summary>Reads the newest status file (the service's or the user instance's); null if none is recent.</summary>
        internal static StatusSnapshot ReadLatest(TimeSpan maxAge)
        {
            StatusSnapshot best = null;
            foreach (string path in new[] { LitePaths.ServiceStatusFile, LitePaths.UserStatusFile })
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var s = JsonSerializer.Deserialize<StatusSnapshot>(File.ReadAllText(path), LiteConfig.JsonOptions);
                    if (s != null && DateTime.UtcNow - s.UpdatedUtc <= maxAge && (best == null || s.UpdatedUtc > best.UpdatedUtc))
                    {
                        best = s;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }

            return best;
        }
    }
}
