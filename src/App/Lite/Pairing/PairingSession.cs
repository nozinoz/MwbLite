// MwbLite pairing: the "Add a PC" (host) and "Join" (joiner) exchanges over TCP, plus LAN discovery.
// UI-agnostic; see PairingCrypto for the scheme and PairingForm for the windows.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite.Pairing
{
    internal static class PairingDefaults
    {
        /// <summary>UDP (discovery) and TCP (exchange) port. Firewall: Setup adds a local-subnet rule for both.</summary>
        internal const int Port = 15102;

        internal static readonly TimeSpan JoinWindow = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>What the joining PC receives once the code is confirmed on both PCs.</summary>
    internal sealed class PairingPayload
    {
        public string SecurityKey { get; set; }

        public List<string> Machines { get; set; } = new();

        public bool OneRow { get; set; } = true;

        public bool WrapMouse { get; set; } = true;

        public int TcpPort { get; set; } = 15100;
    }

    internal sealed class PairingMessage
    {
        public string Type { get; set; }

        public int Version { get; set; }

        public string Name { get; set; }

        public int Port { get; set; }

        public byte[] Commit { get; set; }

        public byte[] Pub { get; set; }

        public byte[] Nonce { get; set; }

        public byte[] Box { get; set; }

        public string Message { get; set; }
    }

    internal sealed class PairingException : Exception
    {
        internal PairingException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Callbacks into the UI.</summary>
    internal interface IPairingUi
    {
        void Status(string text);

        /// <summary>Shows the code; completes with true when the user confirms it matches, false on cancel.</summary>
        Task<bool> ConfirmCodeAsync(string peerName, string code, CancellationToken cancellationToken);
    }

    internal sealed record DiscoveredHost(string Name, IPAddress Address, int Port);

    /// <summary>Length-prefixed JSON frames over a stream (max 64 KB).</summary>
    internal sealed class PairingChannel
    {
        private const int MaxFrame = 64 * 1024;
        private readonly Stream stream;

        internal PairingChannel(Stream stream)
        {
            this.stream = stream;
        }

        internal async Task SendAsync(PairingMessage message, CancellationToken cancellationToken)
        {
            message.Version = PairingCrypto.Version;
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, LiteConfig.JsonOptions);
            byte[] frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
            body.CopyTo(frame, 4);
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async Task<PairingMessage> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                byte[] header = new byte[4];
                await stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length <= 0 || length > MaxFrame)
                {
                    throw new PairingException("Invalid pairing message.");
                }

                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, cts.Token).ConfigureAwait(false);
                var message = JsonSerializer.Deserialize<PairingMessage>(body, LiteConfig.JsonOptions);
                if (message?.Type == null || message.Version != PairingCrypto.Version)
                {
                    throw new PairingException("The other PC runs an incompatible MwbLite version.");
                }

                if (message.Type == "error")
                {
                    throw new PairingException(message.Message ?? "The other PC refused the pairing.");
                }

                if (message.Type == "cancel")
                {
                    throw new PairingException("Pairing was cancelled on the other PC.");
                }

                return message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PairingException("The other PC did not respond in time.");
            }
            catch (EndOfStreamException)
            {
                throw new PairingException("The other PC closed the connection.");
            }
            catch (JsonException)
            {
                throw new PairingException("Invalid pairing message.");
            }
        }

        internal static PairingMessage Expect(PairingMessage message, string type)
        {
            if (message.Type != type)
            {
                throw new PairingException($"Unexpected pairing message ({message.Type}).");
            }

            return message;
        }
    }

    internal static class PairingSession
    {
        /// <summary>
        /// "Add a PC": waits for one PC to join, then sends it the key and layout. <paramref name="applyHost"/>
        /// receives the new machine list (including the joiner) once the joiner has stored its settings.
        /// </summary>
        internal static async Task<string> HostAsync(LiteConfig hostConfig, string hostName, IPairingUi ui, Action<List<string>> applyHost, int port, CancellationToken cancellationToken)
        {
            if (!Encryption.IsKeyValid(hostConfig.SecurityKey, out _))
            {
                throw new PairingException("This PC has no valid security key yet. Set one in Settings and save first.");
            }

            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            windowCts.CancelAfter(PairingDefaults.JoinWindow);

            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start(1);
            }
            catch (SocketException e)
            {
                throw new PairingException($"Cannot listen on port {port} ({e.SocketErrorCode}). Is another pairing window open?");
            }

            TcpClient client;
            using (var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(windowCts.Token))
            {
                Task responder = PairingDiscovery.RespondAsync(hostName, ((IPEndPoint)listener.LocalEndpoint).Port, port, discoveryCts.Token);
                ui.Status($"Waiting for a PC to join (up to {PairingDefaults.JoinWindow.TotalMinutes:0} minutes).\r\nOn the new PC, open MwbLite Settings and press Join.");
                try
                {
                    client = await listener.AcceptTcpClientAsync(windowCts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new PairingException("No PC joined within " + PairingDefaults.JoinWindow.TotalMinutes + " minutes.");
                }
                finally
                {
                    listener.Stop(); // one join per "Add a PC" session
                    discoveryCts.Cancel();
                    await responder.ConfigureAwait(true);
                }
            }

            using (client)
            using (var crypto = new PairingCrypto())
            {
                var channel = new PairingChannel(client.GetStream());
                var hello = PairingChannel.Expect(await channel.ReceiveAsync(PairingDefaults.StepTimeout, cancellationToken).ConfigureAwait(true), "hello");
                string joinerName = LiteConfig.NormalizeName(hello.Name);
                string me = LiteConfig.NormalizeName(hostName);

                string refusal = ValidateName(joinerName) ?? (joinerName == me ? "Both PCs have the same name." : null);
                List<string> machines = refusal == null ? AddMachines(hostConfig, me, joinerName) : null;
                refusal ??= machines == null ? $"{me} already has {LiteConfig.MaxMachines} PCs. Remove one in its Settings first." : null;
                if (refusal != null)
                {
                    await channel.SendAsync(new PairingMessage { Type = "error", Message = refusal }, cancellationToken).ConfigureAwait(true);
                    throw new PairingException($"{joinerName}: {refusal}");
                }

                await channel.SendAsync(new PairingMessage { Type = "hello-ack", Name = me, Pub = crypto.PublicKey, Nonce = crypto.Nonce }, cancellationToken).ConfigureAwait(true);
                var reveal = PairingChannel.Expect(await channel.ReceiveAsync(PairingDefaults.StepTimeout, cancellationToken).ConfigureAwait(true), "reveal");
                if (!PairingCrypto.VerifyCommitment(hello.Commit, reveal.Pub, reveal.Nonce))
                {
                    throw new PairingException("The joining PC's key did not match its commitment. Pairing stopped.");
                }

                crypto.Complete(reveal.Pub, joinerName, me, reveal.Pub, crypto.PublicKey, reveal.Nonce, crypto.Nonce);

                if (!await ConfirmBothAsync(channel, crypto, ui, joinerName, "host", cancellationToken).ConfigureAwait(true))
                {
                    return null;
                }

                var payload = new PairingPayload
                {
                    SecurityKey = hostConfig.SecurityKey,
                    Machines = machines,
                    OneRow = hostConfig.Layout.OneRow,
                    WrapMouse = hostConfig.Layout.WrapMouse,
                    TcpPort = hostConfig.Advanced.TcpPort,
                };
                ui.Status($"Sending settings to {joinerName}…");
                byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, LiteConfig.JsonOptions);
                try
                {
                    await channel.SendAsync(new PairingMessage { Type = "config", Box = crypto.Seal("config", payloadBytes) }, cancellationToken).ConfigureAwait(true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payloadBytes);
                }

                var done = PairingChannel.Expect(await channel.ReceiveAsync(PairingDefaults.StepTimeout, cancellationToken).ConfigureAwait(true), "done");
                _ = crypto.Open("done", done.Box);

                applyHost(machines);
                return joinerName;
            }
        }

        /// <summary>"Join": connects to a PC in "Add a PC" mode and receives its key and layout.</summary>
        internal static async Task<string> JoinAsync(string hostAddress, int port, string joinerName, IPairingUi ui, Action<PairingPayload> applyJoiner, CancellationToken cancellationToken)
        {
            string me = LiteConfig.NormalizeName(joinerName);
            using var client = new TcpClient();
            ui.Status($"Connecting to {hostAddress}…");
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await client.ConnectAsync(hostAddress, port, connectCts.Token).ConfigureAwait(true);
                }
                catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    throw new PairingException($"Cannot reach {hostAddress}. Is \"Add a PC\" open on it, and are both PCs on the same network?");
                }
            }

            using var crypto = new PairingCrypto();
            var channel = new PairingChannel(client.GetStream());
            await channel.SendAsync(new PairingMessage { Type = "hello", Name = me, Commit = PairingCrypto.Commitment(crypto.PublicKey, crypto.Nonce) }, cancellationToken).ConfigureAwait(true);
            var ack = PairingChannel.Expect(await channel.ReceiveAsync(PairingDefaults.StepTimeout, cancellationToken).ConfigureAwait(true), "hello-ack");
            string hostName = LiteConfig.NormalizeName(ack.Name);
            if (ValidateName(hostName) != null)
            {
                throw new PairingException("The other PC sent an invalid name.");
            }

            await channel.SendAsync(new PairingMessage { Type = "reveal", Pub = crypto.PublicKey, Nonce = crypto.Nonce }, cancellationToken).ConfigureAwait(true);
            crypto.Complete(ack.Pub, me, hostName, crypto.PublicKey, ack.Pub, crypto.Nonce, ack.Nonce);

            if (!await ConfirmBothAsync(channel, crypto, ui, hostName, "joiner", cancellationToken).ConfigureAwait(true))
            {
                return null;
            }

            ui.Status($"Receiving settings from {hostName}…");
            var configMessage = PairingChannel.Expect(await channel.ReceiveAsync(PairingDefaults.StepTimeout, cancellationToken).ConfigureAwait(true), "config");
            byte[] plain = crypto.Open("config", configMessage.Box);
            PairingPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<PairingPayload>(plain, LiteConfig.JsonOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            if (payload == null || !Encryption.IsKeyValid(payload.SecurityKey, out _) || payload.Machines == null || payload.Machines.Count > LiteConfig.MaxMachines ||
                !payload.Machines.Contains(me, StringComparer.OrdinalIgnoreCase))
            {
                await channel.SendAsync(new PairingMessage { Type = "error", Message = "Invalid settings received." }, cancellationToken).ConfigureAwait(true);
                throw new PairingException("Invalid settings received from " + hostName + ".");
            }

            try
            {
                applyJoiner(payload);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PairingException)
            {
                await channel.SendAsync(new PairingMessage { Type = "error", Message = "The joining PC could not save its settings." }, cancellationToken).ConfigureAwait(true);
                throw new PairingException("Could not save settings: " + e.Message);
            }

            await channel.SendAsync(new PairingMessage { Type = "done", Box = crypto.Seal("done", Encoding.UTF8.GetBytes("ok")) }, cancellationToken).ConfigureAwait(true);
            return hostName;
        }

        /// <summary>Both users must confirm the code; each side also needs the other's (encrypted) confirmation.</summary>
        private static async Task<bool> ConfirmBothAsync(PairingChannel channel, PairingCrypto crypto, IPairingUi ui, string peerName, string myRole, CancellationToken cancellationToken)
        {
            string peerRole = myRole == "host" ? "joiner" : "host";
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool> local = ui.ConfirmCodeAsync(peerName, crypto.Code, localCts.Token);
            Task<PairingMessage> remote = channel.ReceiveAsync(PairingDefaults.ConfirmTimeout, cancellationToken);

            if (await Task.WhenAny(local, remote).ConfigureAwait(true) == remote)
            {
                try
                {
                    CheckConfirm(crypto, await remote.ConfigureAwait(true), peerRole);
                }
                catch
                {
                    localCts.Cancel(); // close the code prompt
                    throw;
                }

                if (!await local.ConfigureAwait(true))
                {
                    await TrySendCancelAsync(channel).ConfigureAwait(true);
                    return false;
                }

                await SendConfirmAsync(channel, crypto, myRole, cancellationToken).ConfigureAwait(true);
                return true;
            }

            if (!await local.ConfigureAwait(true))
            {
                await TrySendCancelAsync(channel).ConfigureAwait(true);
                return false;
            }

            await SendConfirmAsync(channel, crypto, myRole, cancellationToken).ConfigureAwait(true);
            ui.Status($"Waiting for {peerName} to confirm the code…");
            CheckConfirm(crypto, await remote.ConfigureAwait(true), peerRole);
            return true;
        }

        private static Task SendConfirmAsync(PairingChannel channel, PairingCrypto crypto, string role, CancellationToken cancellationToken)
        {
            return channel.SendAsync(new PairingMessage { Type = "confirm", Box = crypto.Seal("confirm-" + role, Encoding.UTF8.GetBytes(crypto.Code)) }, cancellationToken);
        }

        private static void CheckConfirm(PairingCrypto crypto, PairingMessage message, string peerRole)
        {
            PairingChannel.Expect(message, "confirm");
            byte[] code;
            try
            {
                code = crypto.Open("confirm-" + peerRole, message.Box);
            }
            catch (CryptographicException)
            {
                throw new PairingException("The codes did not match. Pairing stopped; nothing was changed.");
            }

            if (Encoding.UTF8.GetString(code) != crypto.Code)
            {
                throw new PairingException("The codes did not match. Pairing stopped; nothing was changed.");
            }
        }

        private static async Task TrySendCancelAsync(PairingChannel channel)
        {
            try
            {
                await channel.SendAsync(new PairingMessage { Type = "cancel" }, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
            }
        }

        internal static string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 63 || name.Any(c => c is ',' or ':' || char.IsControl(c)))
            {
                return "Invalid computer name.";
            }

            return null;
        }

        /// <summary>The host's machine list with the host and the joiner in it (null if there's no free slot).</summary>
        internal static List<string> AddMachines(LiteConfig hostConfig, string hostName, string joinerName)
        {
            string[] slots = hostConfig.MatrixSlots();
            foreach (string name in new[] { hostName, joinerName })
            {
                if (slots.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                int free = Array.FindIndex(slots, s => s.Length == 0);
                if (free < 0)
                {
                    return null;
                }

                slots[free] = name;
            }

            var list = slots.ToList();
            while (list.Count > 0 && list[^1].Length == 0)
            {
                list.RemoveAt(list.Count - 1);
            }

            return list;
        }
    }

    /// <summary>Finds PCs in "Add a PC" mode with a UDP broadcast on the local network.</summary>
    internal static class PairingDiscovery
    {
        private static readonly byte[] DiscoverRequest = Encoding.UTF8.GetBytes("{\"type\":\"discover\",\"version\":1}");

        internal static async Task RespondAsync(string hostName, int tcpPort, int udpPort, CancellationToken cancellationToken)
        {
            UdpClient udp;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
            }
            catch (SocketException e)
            {
                LiteLog.Write(nameof(PairingDiscovery), "Discovery responder unavailable: " + e.Message);
                return; // joiners can still connect by name
            }

            using (udp)
            {
                byte[] reply = JsonSerializer.SerializeToUtf8Bytes(
                    new PairingMessage { Type = "here", Version = PairingCrypto.Version, Name = LiteConfig.NormalizeName(hostName), Port = tcpPort },
                    LiteConfig.JsonOptions);
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        UdpReceiveResult request = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                        if (request.Buffer.Length < 256 && request.Buffer.AsSpan().SequenceEqual(DiscoverRequest))
                        {
                            await udp.SendAsync(reply, request.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException e)
                    {
                        LiteLog.Write(nameof(PairingDiscovery), "Discovery: " + e.Message);
                    }
                }
            }
        }

        internal static async Task SearchAsync(Action<DiscoveredHost> found, int udpPort, CancellationToken cancellationToken)
        {
            using var udp = new UdpClient(0) { EnableBroadcast = true };
            Task receiving = Task.Run(
                async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            UdpReceiveResult r = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                            if (r.Buffer.Length > 1024)
                            {
                                continue;
                            }

                            var msg = JsonSerializer.Deserialize<PairingMessage>(r.Buffer, LiteConfig.JsonOptions);
                            string name = LiteConfig.NormalizeName(msg?.Name);
                            if (msg?.Type == "here" && msg.Version == PairingCrypto.Version && PairingSession.ValidateName(name) == null && msg.Port is > 0 and < 65536)
                            {
                                found(new DiscoveredHost(name, r.RemoteEndPoint.Address, msg.Port));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception e) when (e is SocketException or JsonException)
                        {
                        }
                    }
                },
                cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (IPAddress target in BroadcastAddresses())
                {
                    try
                    {
                        await udp.SendAsync(DiscoverRequest, new IPEndPoint(target, udpPort), cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                await receiving.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // 255.255.255.255 plus each active IPv4 interface's subnet broadcast address.
        private static IEnumerable<IPAddress> BroadcastAddresses()
        {
            var result = new HashSet<IPAddress> { IPAddress.Broadcast };
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null || ua.IPv4Mask.Equals(IPAddress.Any))
                    {
                        continue;
                    }

                    byte[] ip = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++)
                    {
                        ip[i] = (byte)(ip[i] | ~mask[i]);
                    }

                    _ = result.Add(new IPAddress(ip));
                }
            }

            return result;
        }
    }
}
