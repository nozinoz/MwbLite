// MwbLite: a small control channel between MwbLite PCs, separate from MWB's own protocol.
//
// MWB's ports carry input, clipboard and files unchanged. MwbLite's own requests (who are you, lock, sleep, update,
// screen alignment) go over their own TCP connection on tcpPort+3 (default 15103; 15102 is pairing), secured exactly
// like MWB's connections: TLS with the PC's own certificate, then the security-key proof (LiteSecureChannel), with
// no fallback to an unprotected protocol. Older MwbLite versions don't listen there; callers treat that as "no answer".
//
// Wire format inside TLS: each message is a 4-byte little-endian length followed by that many bytes of UTF-8 JSON
// (at most 64 KB). The client sends one request {"op": "...", ...}, the server answers one reply {"ok": true|false, ...}
// and closes. An unknown op gets {"ok": false, "error": "unknown op"}.
//
// Which engine instance listens: the one that owns MWB's sockets, i.e. the instance whose desktop is the active one
// (in service mode there are several SYSTEM instances, one per desktop, and MWB moves its sockets between them).
// LiteTray.OnTimer calls Maintain() every second: it binds when this instance should listen, retries a failed bind
// every 30 s (quietly: one log line per streak), and stops when the desktop goes away. The Settings window and the
// helper never start it. Binding mirrors MWB's own listener (TcpListener.Create: dual-stack IPv6/IPv4 where available),
// but exclusively, so no other program on this PC can bind the same port on a more specific address and take over.
//
// A connection costs the listener little before it has proven the security key: at most 16 at once, at most 2 from one
// address, 3 s for the TLS handshake and key proof (the request and reply then get 10 s), and an address that fails the
// proof is refused for 30 s (LiteConnectionLimiter). Failures of single connections are routine and logged sparingly.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    /// <summary>What a PC says about itself in answer to "hello".</summary>
    internal sealed record PeerInfo(
        string Name,
        string Version,
        IReadOnlyList<string> Macs,
        int ScreenWidth,
        int ScreenHeight,
        int ScreenHeightMm);

    internal static class LiteControl
    {
        internal const int PortOffset = 3;

        /// <summary>The largest request or reply, in bytes of JSON.</summary>
        internal const int MaxMessageBytes = 64 * 1024;

        private static readonly TimeSpan Overall = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan BindRetry = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan AcceptRestart = TimeSpan.FromSeconds(5);
        private const int MaxConcurrent = 16;
        private const int MaxAcceptFailures = 20;
        internal const int MaxLogged = 256;

        private static readonly Lock Gate = new();
        private static readonly object LogGate = new();
        private static readonly ConcurrentDictionary<string, Func<JsonObject, JsonObject>> Handlers = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> LastLogged = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim Slots = new(MaxConcurrent, MaxConcurrent);
        private static readonly LiteConnectionLimiter Limiter = new();

        private static TcpListener listener;
        private static CancellationTokenSource stopListening;
        private static DateTime nextBind = DateTime.MinValue;
        private static bool bindFailureLogged;

        static LiteControl()
        {
            RegisterHandler("hello", _ => HelloReply());
            LitePower.RegisterHandlers();
        }

        internal static int PortFor(int tcpPort) => tcpPort + PortOffset;

        /// <summary>How long a new connection has for the TLS handshake and key proof before it is closed (tests shorten it).</summary>
        internal static TimeSpan PreAuthTimeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>True while connections from the address are refused (it recently failed the key proof). For tests.</summary>
        internal static bool IsRefused(IPAddress address) => Limiter.IsRefused(address);

        /// <summary>How many connections from the address are being served right now. For tests.</summary>
        internal static int OpenConnections(IPAddress address) => Limiter.OpenCount(address);

        /// <summary>Forgets the per-address counts and refusals. For tests.</summary>
        internal static void ResetLimits() => Limiter.Clear();

        /// <summary>The port to listen on and to dial (tests point it at a loopback listener).</summary>
        internal static Func<int> PortProvider { get; set; } = () => PortFor(Common.tcpPort > 0 ? Common.tcpPort : Setting.Values.TcpPort);

        /// <summary>The port actually bound, or 0 when not listening.</summary>
        internal static int ListeningPort => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

        internal static bool IsListening => listener != null;

        // ---- listening ------------------------------------------------------------------------------------------

        /// <summary>
        /// Called every second by the engine (LiteTray.OnTimer): listens while this instance owns MWB's sockets,
        /// stops when it doesn't, and retries a failed bind later.
        /// </summary>
        internal static void Maintain(bool shouldListen)
        {
            if (shouldListen)
            {
                if (!IsListening && DateTime.UtcNow >= nextBind)
                {
                    Start();
                }
            }
            else if (IsListening)
            {
                Stop();
            }
        }

        /// <summary>Starts listening (idempotent). A failed bind is logged and retried by <see cref="Maintain"/>; it never throws.</summary>
        internal static void Start()
        {
            lock (Gate)
            {
                if (listener != null)
                {
                    return;
                }

                int port = 0;
                TcpListener l = null;
                try
                {
                    port = PortProvider();
                    l = TcpListener.Create(port); // dual-stack IPv6/IPv4 where available, like MWB's own listener
                    l.ExclusiveAddressUse = true; // another program on this PC can't bind the same port on a specific address
                    l.Start();
                    var cts = new CancellationTokenSource();
                    CancellationToken token = cts.Token; // read now: the loop must not look at the static field, which Stop() clears
                    listener = l;
                    stopListening = cts;
                    _ = Task.Run(() => AcceptLoop(l, token));
                    bindFailureLogged = false;
                    LiteLog.Write(nameof(LiteControl), "Control channel listening on port " + ListeningPort + ".");
                }
                catch (Exception e) when (e is SocketException or InvalidOperationException or ArgumentOutOfRangeException or ObjectDisposedException)
                {
                    if (listener == null)
                    {
                        l?.Stop(); // the socket TcpListener.Create made
                    }

                    nextBind = DateTime.UtcNow + BindRetry;
                    if (!bindFailureLogged)
                    {
                        bindFailureLogged = true;
                        LiteLog.Write(nameof(LiteControl), $"Could not listen on port {port}: {e.Message}. Will retry every {BindRetry.TotalSeconds:0} s.");
                    }
                }
            }
        }

        /// <summary>Stops listening (engine exit, or this instance's desktop went away).</summary>
        internal static void Stop()
        {
            lock (Gate)
            {
                StopLocked("Control channel stopped.");
            }
        }

        // Caller holds Gate.
        private static void StopLocked(string message)
        {
            if (listener == null)
            {
                return;
            }

            try
            {
                stopListening?.Cancel();
                listener.Stop();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }

            listener = null;
            stopListening = null;
            LiteLog.Write(nameof(LiteControl), message);
        }

        // The accept loop died of something unexpected: mark the listener stopped so Maintain() binds a new one shortly.
        private static void ListenerFailed(TcpListener failed)
        {
            lock (Gate)
            {
                if (listener != failed)
                {
                    return; // already stopped, or replaced
                }

                StopLocked("Control channel stopped after an error; it will listen again shortly.");
                nextBind = DateTime.UtcNow + AcceptRestart;
            }
        }

        // Nothing leaves this method as an exception: it runs as a task nobody awaits.
        private static async Task AcceptLoop(TcpListener l, CancellationToken stop)
        {
            try
            {
                int failures = 0;
                while (!stop.IsCancellationRequested)
                {
                    Socket socket;
                    try
                    {
                        socket = await l.AcceptSocketAsync(stop).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException e)
                    {
                        if (stop.IsCancellationRequested)
                        {
                            return;
                        }

                        if (++failures > MaxAcceptFailures)
                        {
                            throw; // the listener is broken, not just one connection: handled below
                        }

                        LogQuietly("accept-" + e.SocketErrorCode, "Control channel: accepting a connection failed: " + e.Message);
                        await Task.Delay(500, stop).ConfigureAwait(false); // e.g. a connection reset before accept: just go on
                        continue;
                    }

                    failures = 0;
                    Admit(socket);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                LogQuietly("accept-loop-" + e.GetType().Name, "Control channel: the accept loop failed: " + e);
                ListenerFailed(l);
            }
        }

        // Applies the limits to a new connection and, if it passes, serves it on a worker thread.
        private static void Admit(Socket socket)
        {
            IPAddress address = null;
            try
            {
                address = LiteConnectionLimiter.Normalize((socket.RemoteEndPoint as IPEndPoint)?.Address);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // already gone
            }

            if (address == null || !Slots.Wait(0))
            {
                socket.Dispose(); // too many at once, or unusable: drop
                return;
            }

            if (!Limiter.TryEnter(address))
            {
                _ = Slots.Release();
                socket.Dispose(); // this address has its share open already, or just failed the key proof
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    Serve(socket, address);
                }
                catch (Exception e)
                {
                    // Serve handles its own failures; this is for anything it didn't expect. No stack trace unless it is new.
                    LogQuietly("serve-fatal-" + e.GetType().Name, "Control connection failed unexpectedly: " + e);
                }
                finally
                {
                    Limiter.Exit(address);
                    _ = Slots.Release();
                }
            });
        }

        // One connection: secure it (PreAuthTimeout for the handshake and key proof), read one request, answer, close.
        // The rest is bounded by Overall.
        private static void Serve(Socket socket, IPAddress address)
        {
            using (socket)
            {
                using var deadline = new CancellationTokenSource(PreAuthTimeout);
                using CancellationTokenRegistration reg = deadline.Token.Register(() => CloseQuietly(socket));
                string remote = LiteStatus.Display(address);
                try
                {
                    string key = LiteSecureChannel.SecurityKey();
                    if (string.IsNullOrEmpty(key))
                    {
                        return; // not paired yet: nobody can prove a key we don't have
                    }

                    socket.NoDelay = true;
                    using Stream stream = LiteSecureChannel.EstablishStrict(socket, isClient: false, key);
                    deadline.CancelAfter(Overall); // proven: the request and reply get the whole time from here
                    socket.ReceiveTimeout = socket.SendTimeout = (int)Overall.TotalMilliseconds;
                    JsonObject request = ReadMessage(stream);
                    WriteMessage(stream, Dispatch(request));
                }
                catch (KeyMismatchException e)
                {
                    Limiter.RecordKeyFailure(address);
                    LogQuietly("serve-key", $"Control connection from {remote} ended: {e.Message} It is refused for {LiteConnectionLimiter.DefaultRefuseFor.TotalSeconds:0} s.");
                }
                catch (Exception e) when (e is IOException or InvalidDataException or SocketException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
                {
                    // A port scan, a timeout, a client that hung up: nothing to tell the other side. Logged rarely, by kind.
                    LogQuietly("serve-" + e.GetType().Name, $"Control connection from {remote} ended: {e.Message}");
                }
                catch (Exception e)
                {
                    // Unexpected: the details (once per kind per LogEvery) are what is needed to find the cause.
                    LogQuietly("serve-unexpected-" + e.GetType().Name, $"Control connection from {remote} failed: {e}");
                }
            }
        }

        /// <summary>Runs the handler for a request and builds the reply. Never throws.</summary>
        internal static JsonObject Dispatch(JsonObject request)
        {
            string op = null;
            try
            {
                op = request["op"]?.GetValue<string>();
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
            }

            if (op == null || !Handlers.TryGetValue(op, out Func<JsonObject, JsonObject> handler))
            {
                return new JsonObject { ["ok"] = false, ["error"] = "unknown op" };
            }

            try
            {
                var reply = new JsonObject { ["ok"] = true };
                JsonObject extra = handler(request);
                if (extra != null)
                {
                    foreach (KeyValuePair<string, JsonNode> field in extra.Where(f => f.Key != "ok"))
                    {
                        reply[field.Key] = field.Value?.DeepClone();
                    }
                }

                return reply;
            }
            catch (Exception e)
            {
                return new JsonObject { ["ok"] = false, ["error"] = e.Message };
            }
        }

        /// <summary>
        /// Registers the handler for one request type. The handler gets the request and returns the reply's extra
        /// fields (null for a bare {"ok": true}); throwing gives {"ok": false, "error": message}.
        /// Handlers run on a worker thread.
        /// </summary>
        internal static void RegisterHandler(string op, Func<JsonObject, JsonObject> handler) => Handlers[op] = handler;

        // ---- framing --------------------------------------------------------------------------------------------

        internal static void WriteMessage(Stream stream, JsonObject message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
            if (body.Length > MaxMessageBytes)
            {
                throw new InvalidDataException($"The message is {body.Length} bytes; the limit is {MaxMessageBytes}.");
            }

            byte[] frame = new byte[4 + body.Length]; // one write, so it goes out as one TLS record
            BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
            body.CopyTo(frame, 4);
            stream.Write(frame);
            stream.Flush();
        }

        /// <summary>Reads one message. Throws InvalidDataException for a length over the limit or text that isn't a JSON object.</summary>
        internal static JsonObject ReadMessage(Stream stream)
        {
            byte[] head = new byte[4];
            stream.ReadExactly(head);
            int length = BinaryPrimitives.ReadInt32LittleEndian(head);
            if (length is < 0 or > MaxMessageBytes)
            {
                throw new InvalidDataException($"The message length {length} is outside 0..{MaxMessageBytes}.");
            }

            byte[] body = new byte[length];
            stream.ReadExactly(body);
            try
            {
                return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("The message is not a JSON object.");
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("The message is not valid JSON: " + e.Message, e);
            }
        }

        // ---- sending --------------------------------------------------------------------------------------------

        /// <summary>
        /// Sends one request to a PC by name (resolved like MWB does, with LiteAddressBook as fallback) and returns the
        /// reply, or null if the PC didn't answer (offline, older MwbLite, wrong key, timeout).
        /// </summary>
        internal static async Task<JsonObject> SendAsync(string peer, string op, JsonObject args, CancellationToken ct)
        {
            try
            {
                string key = LiteSecureChannel.SecurityKey();
                if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(peer))
                {
                    return null;
                }

                JsonObject request = args == null ? new JsonObject() : (JsonObject)args.DeepClone();
                request["op"] = op;

                IPAddress[] candidates = await ResolveAsync(peer.Trim(), ct).ConfigureAwait(false);
                if (candidates.Length == 0)
                {
                    LogQuietly("resolve-" + peer, $"Control channel: can't find {peer} on the network.");
                    return null;
                }

                using Socket socket = await ConnectAsync(candidates, PortProvider(), ct).ConfigureAwait(false);
                if (socket == null)
                {
                    LogQuietly("connect-" + peer, $"Control channel: {peer} ({string.Join(", ", candidates.Select(a => a.ToString()))}) didn't answer on port {PortProvider()}; it may be off or run an older MwbLite.");
                    return null;
                }

                return await Task.Run(() => Exchange(socket, key, request, ct), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                LogQuietly("send-" + peer, $"Control channel to {peer} failed: {e.Message}");
                return null;
            }
        }

        // The client's whole conversation on a connected socket, bounded by Overall (and the caller's token).
        private static JsonObject Exchange(Socket socket, string key, JsonObject request, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Overall);
            using CancellationTokenRegistration reg = deadline.Token.Register(() => CloseQuietly(socket));
            string peer = LiteStatus.Display((socket.RemoteEndPoint as IPEndPoint)?.Address);
            try
            {
                socket.NoDelay = true;
                using Stream stream = LiteSecureChannel.EstablishStrict(socket, isClient: true, key);
                socket.ReceiveTimeout = socket.SendTimeout = (int)Overall.TotalMilliseconds;
                WriteMessage(stream, request);
                return ReadMessage(stream);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or SocketException or ObjectDisposedException or System.Security.Authentication.AuthenticationException or KeyMismatchException)
            {
                if (!ct.IsCancellationRequested)
                {
                    LogQuietly("exchange-" + peer + "-" + e.GetType().Name, $"Control channel to {peer} failed: {e.Message}");
                }

                return null;
            }
        }

        /// <summary>Where to try a PC, best first: its live MWB connection, the Name → IP setting, DNS, the last known addresses.</summary>
        internal static async Task<IPAddress[]> ResolveAsync(string peer, CancellationToken ct)
        {
            var found = new List<IPAddress>();
            void Add(IPAddress a)
            {
                if (a == null)
                {
                    return;
                }

                if (a.IsIPv4MappedToIPv6)
                {
                    a = a.MapToIPv4();
                }

                if (!found.Contains(a))
                {
                    found.Add(a);
                }
            }

            if (IPAddress.TryParse(peer, out IPAddress literal))
            {
                Add(literal);
                return found.ToArray();
            }

            foreach (PcStatus pc in LiteStatus.Current.Pcs.Where(p => p.State == PcState.Connected && p.Address != null && p.Name.Equals(peer, StringComparison.OrdinalIgnoreCase)))
            {
                if (IPAddress.TryParse(pc.Address, out IPAddress live))
                {
                    Add(live);
                }
            }

            try
            {
                string[] lines = (Setting.Values.Name2IP ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (LiteDiagnostics.ParseName2Ip(lines).TryGetValue(peer, out string fixedIp) && IPAddress.TryParse(fixedIp, out IPAddress mapped))
                {
                    Add(mapped);
                }
            }
            catch (Exception e) when (e is NullReferenceException or TypeInitializationException or IOException)
            {
                // settings not loaded (tests): skip
            }

            try
            {
                IPAddress[] dns = await Dns.GetHostAddressesAsync(peer, ct).WaitAsync(DnsTimeout, ct).ConfigureAwait(false);
                foreach (IPAddress a in dns.Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
                {
                    Add(a);
                }
            }
            catch (Exception e) when (e is SocketException or TimeoutException)
            {
                // right after waking, names often don't resolve yet: fall through to the address book
            }

            foreach (IPAddress a in LiteAddressBook.Get(peer))
            {
                Add(a);
            }

            return found.ToArray();
        }

        // Tries each address in turn within ConnectTimeout in total (a refusal moves on at once).
        private static async Task<Socket> ConnectAsync(IPAddress[] candidates, int port, CancellationToken ct)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ConnectTimeout);
            foreach (IPAddress address in candidates)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, port, budget.Token).ConfigureAwait(false);
                    return socket;
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    ct.ThrowIfCancellationRequested();
                    if (budget.IsCancellationRequested)
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        // ---- hello ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Raised (on a worker thread) after a PC answers "hello": the engine says hello to each PC when it connects,
        /// so subscribers learn every connected PC's version, MACs and screen. Arguments: the PC's layout name, its info.
        /// </summary>
        internal static event Action<string, PeerInfo> PeerHello;

        private static void OnPeerHello(string peer, PeerInfo info)
        {
            Action<string, PeerInfo> handlers = PeerHello;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<string, PeerInfo> subscriber in handlers.GetInvocationList().Cast<Action<string, PeerInfo>>())
            {
                try
                {
                    subscriber(peer, info);
                }
                catch (Exception e)
                {
                    LiteLog.Write(nameof(LiteControl), "A hello subscriber failed: " + e.Message);
                }
            }
        }

        /// <summary>Asks a PC who it is. Null if it didn't answer. Also records its MAC addresses for Wake.</summary>
        internal static async Task<PeerInfo> HelloAsync(string peer, CancellationToken ct)
        {
            PeerInfo info = ParseHello(peer, await SendAsync(peer, "hello", null, ct).ConfigureAwait(false));
            if (info == null)
            {
                return null;
            }

            LiteWake.RememberMacs(peer, info.Macs);
            OnPeerHello(peer, info);
            return info;
        }

        /// <summary>What this PC tells others about itself.</summary>
        internal static JsonObject HelloReply()
        {
            (int width, int height, int heightMm) = PrimaryScreen();
            return new JsonObject
            {
                ["name"] = (Common.MachineName ?? Environment.MachineName).Trim(),
                ["version"] = LiteUpdate.CurrentText,
                ["macs"] = new JsonArray(LiteWake.LocalMacs().Select(m => (JsonNode)JsonValue.Create(m)).ToArray()),
                ["screenWidth"] = width,
                ["screenHeight"] = height,
                ["screenHeightMm"] = heightMm,
            };
        }

        /// <summary>Reads a "hello" reply into a <see cref="PeerInfo"/>; null if the PC didn't answer or said no.</summary>
        internal static PeerInfo ParseHello(string peer, JsonObject reply)
        {
            if (reply == null || !Field(reply, "ok", false))
            {
                return null;
            }

            var macs = new List<string>();
            if (reply["macs"] is JsonArray array)
            {
                foreach (JsonNode node in array)
                {
                    if (node is JsonValue v && v.TryGetValue(out string text) && LiteWake.TryParseMac(text, out byte[] bytes))
                    {
                        macs.Add(LiteWake.FormatMac(bytes));
                    }
                }
            }

            string name = Field(reply, "name", string.Empty).Trim();
            return new PeerInfo(
                name.Length > 0 ? name : peer,
                Field(reply, "version", string.Empty),
                macs.Distinct().ToList(),
                Field(reply, "screenWidth", 0),
                Field(reply, "screenHeight", 0),
                Field(reply, "screenHeightMm", 0));
        }

        // A field of the expected type, or the fallback (a reply from another version may omit or change anything).
        private static T Field<T>(JsonObject obj, string name, T fallback)
        {
            try
            {
                return obj[name] is JsonValue v && v.TryGetValue(out T value) && value != null ? value : fallback;
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
                return fallback;
            }
        }

        // ---- this PC's screen -----------------------------------------------------------------------------------

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr dc, int index);

        private const int SmCxScreen = 0, SmCyScreen = 1, VertSize = 6;

        /// <summary>Primary screen in pixels, and its physical height in millimetres as Windows reports it (0 if unknown).</summary>
        internal static (int Width, int Height, int HeightMm) PrimaryScreen()
        {
            try
            {
                int mm = 0;
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    try
                    {
                        mm = Math.Max(0, GetDeviceCaps(dc, VertSize));
                    }
                    finally
                    {
                        _ = ReleaseDC(IntPtr.Zero, dc);
                    }
                }

                return (GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen), mm);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return (0, 0, 0);
            }
        }

        // ---- helpers --------------------------------------------------------------------------------------------

        private static void CloseQuietly(Socket socket)
        {
            try
            {
                socket.Close();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }

        // Failures are routine (a PC that is off, an older version, a port scan): one log line per kind of failure per
        // 10 minutes. The list of kinds is bounded, so a flood of different ones can't grow it.
        private static void LogQuietly(string what, string message)
        {
            DateTime now = DateTime.UtcNow;
            lock (LogGate)
            {
                if (LastLogged.TryGetValue(what, out DateTime last) && now - last < LogEvery)
                {
                    return;
                }

                if (LastLogged.Count >= MaxLogged)
                {
                    PruneLogged(LastLogged, now, LogEvery, MaxLogged);
                }

                LastLogged[what] = now;
            }

            LiteLog.Write(nameof(LiteControl), message);
        }

        /// <summary>Drops the entries older than <paramref name="every"/>; if the map is still full, the oldest quarter.</summary>
        internal static void PruneLogged(Dictionary<string, DateTime> map, DateTime now, TimeSpan every, int max)
        {
            foreach (string old in map.Where(kv => now - kv.Value >= every).Select(kv => kv.Key).ToList())
            {
                _ = map.Remove(old);
            }

            if (map.Count >= max)
            {
                foreach (string oldest in map.OrderBy(kv => kv.Value).Take(Math.Max(1, max / 4)).Select(kv => kv.Key).ToList())
                {
                    _ = map.Remove(oldest);
                }
            }
        }
    }
}
