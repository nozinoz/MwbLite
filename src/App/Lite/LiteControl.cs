// MwbLite: a small control channel between MwbLite PCs, separate from MWB's own protocol.
//
// MWB's ports carry input, clipboard and files unchanged. MwbLite's own requests (who are you, lock, sleep, update,
// screen alignment) go over their own TCP connection on tcpPort+3 (default 15103; 15102 is pairing), secured exactly
// like MWB's connections: TLS with the PC's own certificate, then the security-key proof (LiteSecureChannel), with
// no fallback to an unprotected protocol. Older MwbLite versions don't listen there; callers treat that as "no answer".
//
// Wire format inside TLS: each message is a 4-byte little-endian length followed by that many bytes of UTF-8 JSON.
// The client sends one request {"op": "...", ...}, the server answers one reply {"ok": true|false, ...} and closes.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

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

        internal static int PortFor(int tcpPort) => tcpPort + PortOffset;

        /// <summary>Starts listening (idempotent). Called once by the engine at startup.</summary>
        internal static void Start() => throw new NotImplementedException();

        /// <summary>Stops listening (engine exit).</summary>
        internal static void Stop() => throw new NotImplementedException();

        /// <summary>
        /// Registers the handler for one request type. The handler gets the request and returns the reply's extra
        /// fields (null for a bare {"ok": true}); throwing gives {"ok": false, "error": message}.
        /// Handlers run on a worker thread.
        /// </summary>
        internal static void RegisterHandler(string op, Func<JsonObject, JsonObject> handler) => throw new NotImplementedException();

        /// <summary>
        /// Sends one request to a PC by name (resolved like MWB does, with LiteAddressBook as fallback) and returns the
        /// reply, or null if the PC didn't answer (offline, older MwbLite, wrong key, timeout).
        /// </summary>
        internal static Task<JsonObject> SendAsync(string peer, string op, JsonObject args, CancellationToken ct) => throw new NotImplementedException();

        /// <summary>
        /// Raised (on a worker thread) after a PC answers "hello": the engine says hello to each PC when it connects,
        /// so subscribers learn every connected PC's version, MACs and screen. Arguments: the PC's layout name, its info.
        /// </summary>
        internal static event Action<string, PeerInfo> PeerHello;

        /// <summary>Asks a PC who it is. Null if it didn't answer. Also records its MAC addresses for Wake.</summary>
        internal static Task<PeerInfo> HelloAsync(string peer, CancellationToken ct) => throw new NotImplementedException();
    }
}
