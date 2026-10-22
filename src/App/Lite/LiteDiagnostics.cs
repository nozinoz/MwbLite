// MwbLite: "Check connection" in Settings. Runs as the signed-in user, reads only (the firewall through its COM
// API, which needs no admin), and explains each problem in plain words with what to do about it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal enum CheckResult
    {
        Ok,
        Warning,
        Problem,
        Info,
    }

    internal sealed record CheckItem(CheckResult Result, string Title, string Detail, bool FirewallFixable = false);

    internal static class LiteDiagnostics
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan StatusMaxAge = TimeSpan.FromSeconds(20);

        /// <summary>Runs every check, reporting each result as soon as it is known.</summary>
        internal static async Task RunAsync(LiteConfig cfg, Action<CheckItem> report, CancellationToken ct)
        {
            string me = LiteConfig.NormalizeName(Environment.MachineName);
            List<string> others = cfg.MatrixSlots().Where(n => n.Length > 0 && !n.Equals(me, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // 1. Settings
            var errors = cfg.Validate(out _);
            report(errors.Count == 0
                ? new CheckItem(CheckResult.Ok, "Settings", "settings.json is valid and has a security key.")
                : new CheckItem(CheckResult.Problem, "Settings", string.Join(" ", errors)));
            report(others.Count == 0
                ? new CheckItem(CheckResult.Problem, "Other PCs", "No other PC is in the layout yet. Use Add a PC… here and Join… on the other PC.")
                : new CheckItem(CheckResult.Ok, "Other PCs", string.Join(", ", others)));

            // 2. Running?
            StatusSnapshot status = LiteStatus.ReadLatest(StatusMaxAge);
            report(status == null
                ? new CheckItem(CheckResult.Problem, "MwbLite running", "MwbLite doesn't seem to be running on this PC. Start it from the Start menu (MwbLite).")
                : new CheckItem(CheckResult.Ok, "MwbLite running", status.ServiceMode ? "Running as a service (it can control the lock screen and UAC prompts)." : "Running as " + Environment.UserName + "."));
            if (cfg.UseService && status != null && !status.ServiceMode)
            {
                report(new CheckItem(CheckResult.Warning, "Service mode", "Service mode is on in Settings but MwbLite is running without it. Save the settings again, or restart MwbLite."));
            }

            // 3. Firewall
            foreach (CheckItem item in CheckFirewall(cfg.Advanced.TcpPort))
            {
                report(item);
            }

            ct.ThrowIfCancellationRequested();

            // 4. Each PC
            Dictionary<string, string> name2Ip = ParseName2Ip(cfg.Advanced.Name2Ip);
            await Task.WhenAll(others.Select(async pc => report(await CheckPcAsync(pc, cfg.Advanced.TcpPort, name2Ip, status, ct).ConfigureAwait(false)))).ConfigureAwait(false);
        }

        private static async Task<CheckItem> CheckPcAsync(string pc, int tcpPort, Dictionary<string, string> name2Ip, StatusSnapshot status, CancellationToken ct)
        {
            PcStatus live = status?.Pcs.FirstOrDefault(p => p.Name.Equals(pc, StringComparison.OrdinalIgnoreCase));
            if (live?.State == PcState.Connected)
            {
                return new CheckItem(CheckResult.Ok, pc, "Connected" + (live.Address != null ? " (" + live.Address + ")" : string.Empty) + ".");
            }

            IPAddress[] addresses;
            try
            {
                if (name2Ip.TryGetValue(pc, out string fixedIp) && IPAddress.TryParse(fixedIp, out IPAddress ip))
                {
                    addresses = new[] { ip };
                }
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ResolveTimeout);
                    addresses = (await Dns.GetHostAddressesAsync(pc, timeout.Token).ConfigureAwait(false))
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
                        .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                        .ToArray();
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                addresses = Array.Empty<IPAddress>();
            }

            if (addresses.Length == 0)
            {
                return new CheckItem(CheckResult.Problem, pc, $"Can't find {pc} on the network. Check it is switched on and on the same network as this PC. If it is, add its IP address under Advanced > Name → IP.");
            }

            IPAddress target = addresses[0];
            string where = $"{pc} ({target})";
            bool sameSubnet = IsOnLocalSubnet(target);

            bool mainPort = await CanConnectAsync(target, tcpPort + 1, ct).ConfigureAwait(false);
            bool filePort = await CanConnectAsync(target, tcpPort, ct).ConfigureAwait(false);

            if (live?.State == PcState.KeyMismatch)
            {
                return new CheckItem(CheckResult.Problem, pc, $"{where} answers, but its security key is different. Pair the PCs again: Add a PC… here, Join… there.");
            }

            if (!mainPort || !filePort)
            {
                string ports = !mainPort && !filePort ? $"ports {tcpPort + 1} and {tcpPort}" : $"port {(!mainPort ? tcpPort + 1 : tcpPort)}";
                string subnetNote = sameSubnet ? string.Empty : " It is also on a different subnet, and MwbLite's firewall rule only allows the local subnet.";
                return new CheckItem(CheckResult.Problem, pc, $"Found {where}, but MwbLite isn't answering on {ports}. Make sure MwbLite is running there, and run Check connection on that PC to test its firewall.{subnetNote}");
            }

            if (!sameSubnet)
            {
                return new CheckItem(CheckResult.Warning, pc, $"{where} answers but is on a different subnet. That works only if \"Only connect within my subnet\" is off and the firewall rule on {pc} allows this PC.");
            }

            return live?.State == PcState.Connecting
                ? new CheckItem(CheckResult.Info, pc, $"{where} answers; MwbLite is connecting to it now.")
                : new CheckItem(CheckResult.Warning, pc, $"{where} answers, but MwbLite isn't connected to it yet. If this lasts, use Reconnect (Win+Ctrl+Alt+R) or restart MwbLite on both PCs.");
        }

        private static async Task<bool> CanConnectAsync(IPAddress address, int port, CancellationToken ct)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                socket.Shutdown(SocketShutdown.Both);
                return true;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                return false;
            }
        }

        internal static bool IsOnLocalSubnet(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return true; // IPv6: not checked
            }

            byte[] target = address.GetAddressBytes();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    if (SameSubnet(u.Address.GetAddressBytes(), target, u.IPv4Mask.GetAddressBytes()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool SameSubnet(byte[] a, byte[] b, byte[] mask)
        {
            for (int i = 0; i < 4; i++)
            {
                if ((a[i] & mask[i]) != (b[i] & mask[i]))
                {
                    return false;
                }
            }

            return true;
        }

        internal static Dictionary<string, string> ParseName2Ip(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines ?? Enumerable.Empty<string>())
            {
                string[] parts = (line ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    map[parts[0]] = parts[1];
                }
            }

            return map;
        }

        // ---- firewall (read-only, through the Windows Firewall COM API) -------------------------------------

        private const int NetFwRuleDirIn = 1;
        private const int NetFwActionBlock = 0;
        private const int NetFwActionAllow = 1;
        private const int NetFwIpProtocolTcp = 6;
        private const int NetFwIpProtocolUdp = 17;
        private const int NetFwIpProtocolAny = 256;

        internal static List<CheckItem> CheckFirewall(int tcpPort)
        {
            var items = new List<CheckItem>();
            string exe = System.IO.Path.Combine(LitePaths.InstallDir, "MwbLite.exe");
            if (!System.IO.File.Exists(exe))
            {
                exe = LitePaths.ExePath;
            }

            try
            {
                Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true);
                dynamic policy = Activator.CreateInstance(type);
                int currentProfiles = policy.CurrentProfileTypes;
                string profileName = ProfileNames(currentProfiles);

                bool firewallOn = false;
                foreach (int p in new[] { 1, 2, 4 })
                {
                    if ((currentProfiles & p) != 0)
                    {
                        firewallOn |= (bool)policy.FirewallEnabled[p];
                    }
                }

                if (!firewallOn)
                {
                    items.Add(new CheckItem(CheckResult.Info, "Firewall", $"Windows Firewall is off for this network ({profileName}), so it isn't blocking MwbLite."));
                    return items;
                }

                bool tcpAllowed = false, udpAllowed = false;
                var blocks = new List<string>();
                foreach (dynamic rule in policy.Rules)
                {
                    string app = rule.ApplicationName as string;
                    if (string.IsNullOrEmpty(app) || !string.Equals(System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)), exe, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if ((int)rule.Direction != NetFwRuleDirIn || !(bool)rule.Enabled || ((int)rule.Profiles & currentProfiles) == 0)
                    {
                        continue;
                    }

                    int protocol = rule.Protocol;
                    if ((int)rule.Action == NetFwActionBlock)
                    {
                        blocks.Add((string)rule.Name);
                    }
                    else if ((int)rule.Action == NetFwActionAllow)
                    {
                        tcpAllowed |= protocol is NetFwIpProtocolTcp or NetFwIpProtocolAny;
                        udpAllowed |= protocol is NetFwIpProtocolUdp or NetFwIpProtocolAny;
                    }
                }

                if (blocks.Count > 0)
                {
                    items.Add(new CheckItem(CheckResult.Problem, "Firewall", $"A firewall rule blocks MwbLite on this network ({profileName}): \"{string.Join("\", \"", blocks.Distinct())}\". Windows adds one when its firewall prompt is dismissed. Press Fix firewall.", FirewallFixable: true));
                }
                else if (!tcpAllowed)
                {
                    items.Add(new CheckItem(CheckResult.Problem, "Firewall", $"No firewall rule lets other PCs connect to MwbLite on this network ({profileName}). Press Fix firewall.", FirewallFixable: true));
                }
                else
                {
                    items.Add(new CheckItem(CheckResult.Ok, "Firewall", $"MwbLite is allowed through Windows Firewall on this network ({profileName})."));
                    if (!udpAllowed)
                    {
                        items.Add(new CheckItem(CheckResult.Warning, "Firewall (pairing)", "Pairing discovery (UDP 15102) isn't allowed, so other PCs may not find this one when you press Join…. Press Fix firewall.", FirewallFixable: true));
                    }
                }

                if ((currentProfiles & 4) != 0)
                {
                    items.Add(new CheckItem(CheckResult.Info, "Network type", "This network is set to Public. MwbLite's rules cover it, but if other apps can't see this PC either, set the network to Private in Windows Settings > Network."));
                }
            }
            catch (Exception e)
            {
                items.Add(new CheckItem(CheckResult.Warning, "Firewall", "Couldn't read the firewall settings: " + e.Message));
            }

            return items;
        }

        private static string ProfileNames(int profiles)
        {
            var names = new List<string>();
            if ((profiles & 1) != 0)
            {
                names.Add("Domain");
            }

            if ((profiles & 2) != 0)
            {
                names.Add("Private");
            }

            if ((profiles & 4) != 0)
            {
                names.Add("Public");
            }

            return names.Count == 0 ? "unknown" : string.Join("/", names);
        }
    }
}
