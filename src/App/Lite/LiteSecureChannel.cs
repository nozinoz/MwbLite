// MwbLite: TLS around every MWB connection, with a security-key proof.
//
// Upstream MWB encrypts with AES-CBC (key from PBKDF2 of the security key) but nothing detects tampering, and the
// key check is MWB's own handshake on top. MwbLite now first runs TLS 1.3 (1.2 allowed) on each socket, main and
// clipboard/file channel alike, and only then lets MWB speak, unchanged, inside the tunnel:
//
//  1. TLS with mutual certificates. Each PC uses a self-signed ECDSA P-256 certificate it made itself (kept in the
//     running account's certificate store, "MwbLite secure channel"). Certificates are not trusted on their own.
//  2. Key proof inside TLS: each side sends "MWBL" | version | HMAC-SHA256(K, role | SHA256(client cert) |
//     SHA256(server cert)), with K = HKDF-SHA256(security key, salt "MwbLite channel v1"). Each checks the other's
//     (constant time) and answers ok / mismatch. Binding both certificates means a relay in the middle, which has
//     to present its own certificates to each side, can't produce a valid proof without the key. The key itself
//     never crosses the network.
//
// Older peers (MwbLite before 0.6, stock PowerToys MWB) don't speak TLS. A server tells them apart by the first
// byte (0x16 starts a TLS handshake) and accepts them only when advanced.allowInsecureConnections is on; a client
// with that option on falls back to the old protocol for a PC whose TLS attempt failed. Otherwise they are
// refused and the PC shows "older version" in the tray and Check connection.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MouseWithoutBorders.Lite
{
    /// <summary>The other PC proved a different security key (or rejected ours).</summary>
    internal sealed class KeyMismatchException : Exception
    {
        internal KeyMismatchException(string message)
            : base(message)
        {
        }
    }

    /// <summary>The other PC doesn't speak MwbLite's secure protocol (older MwbLite or PowerToys MWB).</summary>
    internal sealed class InsecurePeerException : Exception
    {
        internal InsecurePeerException(string message)
            : base(message)
        {
        }
    }

    internal static class LiteSecureChannel
    {
        internal const byte TlsHandshakeRecord = 0x16;
        internal const string CertificateName = "MwbLite secure channel";

        private const byte Version = 1;
        private const byte VerdictOk = 1;
        private const byte VerdictMismatch = 0;
        private const int ProofLength = 4 + 1 + 32;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MWBL");
        private static readonly byte[] KdfSalt = Encoding.UTF8.GetBytes("MwbLite channel v1");
        /// <summary>How long the TLS handshake and key proof may take (tests shorten it).</summary>
        internal static TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan LegacyMemory = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan EventMemory = TimeSpan.FromMinutes(2);

        private static readonly Lazy<X509Certificate2> StoreCertificate = new(LoadOrCreateStoreCertificate);
        private static readonly ConcurrentDictionary<string, DateTime> LegacyPeers = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> KeyMismatches = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> InsecureRejected = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The certificate this PC presents. Tests replace it.</summary>
        internal static Func<X509Certificate2> Certificate { get; set; } = () => StoreCertificate.Value;

        /// <summary>Whether older, unencrypted-handshake peers may connect (advanced.allowInsecureConnections).</summary>
        internal static Func<bool> AllowInsecure { get; set; } = () => Class.Setting.Values.AllowInsecureConnections;

        /// <summary>The security key (plain text, from settings).</summary>
        internal static Func<string> SecurityKey { get; set; } = () => Core.Encryption.MyKey;

        // ---- status for the tray / Check connection -------------------------------------------------------------

        internal static bool RecentKeyMismatch(string peer) => Recent(KeyMismatches, peer);

        internal static bool RecentInsecureRejected(string peer) => Recent(InsecureRejected, peer);

        private static bool Recent(ConcurrentDictionary<string, DateTime> map, string peer) =>
            !string.IsNullOrEmpty(peer) && map.TryGetValue(peer.Trim(), out DateTime when) && DateTime.UtcNow - when < EventMemory;

        private static void Remember(ConcurrentDictionary<string, DateTime> map, params string[] names)
        {
            foreach (string n in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                map[n.Trim()] = DateTime.UtcNow;
            }
        }

        private static void Forget(params string[] names)
        {
            foreach (string n in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                KeyMismatches.TryRemove(n.Trim(), out _);
                InsecureRejected.TryRemove(n.Trim(), out _);
            }
        }

        // ---- the handshake --------------------------------------------------------------------------------------

        /// <summary>
        /// Secures a connected socket. Returns the stream MWB should use: a TLS stream, or (older peer, allowed by
        /// settings) a plain NetworkStream. Throws KeyMismatchException, InsecurePeerException, or an IO/auth error.
        /// </summary>
        internal static Stream Establish(Socket socket, bool isClient, string peer) => Establish(socket, isClient, peer, SecurityKey());

        internal static Stream Establish(Socket socket, bool isClient, string peer, string securityKey)
        {
            string address = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString();
            int oldReceive = socket.ReceiveTimeout, oldSend = socket.SendTimeout;
            socket.ReceiveTimeout = socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            try
            {
                if (!isClient && !StartsWithTls(socket))
                {
                    return Legacy(socket, peer, address, "it opened the connection without TLS");
                }

                if (isClient && AllowInsecure() && IsKnownLegacy(peer, address))
                {
                    return Legacy(socket, peer, address, "it didn't answer TLS recently");
                }

                var ssl = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
                try
                {
                    Authenticate(ssl, isClient);
                }
                catch (AuthenticationException e) when (isClient)
                {
                    // An older peer answers a TLS hello with its own (non-TLS) bytes, which fails here. A dropped
                    // connection is an IOException instead and is just a failed attempt.
                    ssl.Dispose();
                    Remember(LegacyPeers, peer, address);
                    Remember(InsecureRejected, peer, address);
                    throw new InsecurePeerException($"{peer} ({address}) didn't complete a secure connection; it may run an older MwbLite or PowerToys: {e.Message}");
                }
                catch (Exception)
                {
                    ssl.Dispose();
                    throw;
                }

                try
                {
                    ProveKey(ssl, isClient, securityKey);
                }
                catch (KeyMismatchException)
                {
                    Remember(KeyMismatches, peer, address);
                    ssl.Dispose();
                    throw;
                }
                catch (Exception)
                {
                    ssl.Dispose();
                    throw;
                }

                Forget(peer, address);
                LegacyPeers.TryRemove(peer ?? string.Empty, out _);
                return ssl;
            }
            finally
            {
                socket.ReceiveTimeout = oldReceive;
                socket.SendTimeout = oldSend;
            }
        }

        private static Stream Legacy(Socket socket, string peer, string address, string why)
        {
            if (!AllowInsecure())
            {
                Remember(InsecureRejected, peer, address);
                throw new InsecurePeerException($"Refused {peer} ({address}): {why}. It runs an older MwbLite or PowerToys; update it, or allow older versions in Settings > Advanced.");
            }

            LiteLog.Write(nameof(LiteSecureChannel), $"Using the old, unprotected protocol with {peer} ({address}): {why}.");
            return new NetworkStream(socket, ownsSocket: false);
        }

        private static bool IsKnownLegacy(string peer, string address) =>
            new[] { peer, address }.Any(n => !string.IsNullOrEmpty(n) && LegacyPeers.TryGetValue(n.Trim(), out DateTime when) && DateTime.UtcNow - when < LegacyMemory);

        /// <summary>Waits for the client's first byte without consuming it.</summary>
        private static bool StartsWithTls(Socket socket)
        {
            byte[] first = new byte[1];
            int n = socket.Receive(first, 0, 1, SocketFlags.Peek);
            if (n == 0)
            {
                throw new IOException("The connection was closed before it started.");
            }

            return first[0] == TlsHandshakeRecord;
        }

        private static void Authenticate(SslStream ssl, bool isClient)
        {
            X509Certificate2 cert = Certificate();
            const SslProtocols protocols = SslProtocols.Tls13 | SslProtocols.Tls12;

            // Peer certificates are accepted as they are: trust comes from the key proof bound to them.
            static bool AcceptAny(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) => certificate != null;

            if (isClient)
            {
                ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
                {
                    TargetHost = "MwbLite",
                    EnabledSslProtocols = protocols,
                    ClientCertificates = new X509CertificateCollection { cert },
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => cert,
                    RemoteCertificateValidationCallback = AcceptAny,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                });
            }
            else
            {
                ssl.AuthenticateAsServer(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = protocols,
                    RemoteCertificateValidationCallback = AcceptAny,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                });
            }

            if (ssl.RemoteCertificate == null)
            {
                throw new AuthenticationException("The other PC sent no certificate.");
            }
        }

        private static void ProveKey(SslStream ssl, bool isClient, string securityKey)
        {
            byte[] localCert = ssl.LocalCertificate?.GetRawCertData() ?? throw new AuthenticationException("No local certificate.");
            byte[] remoteCert = ssl.RemoteCertificate.GetRawCertData();
            byte[] clientCert = isClient ? localCert : remoteCert;
            byte[] serverCert = isClient ? remoteCert : localCert;

            byte[] mine = Proof(securityKey, isClient ? (byte)'C' : (byte)'S', clientCert, serverCert);
            byte[] expected = Proof(securityKey, isClient ? (byte)'S' : (byte)'C', clientCert, serverCert);

            ssl.Write(Magic.Concat(new[] { Version }).Concat(mine).ToArray());
            ssl.Flush();

            byte[] theirs = ReadExactly(ssl, ProofLength);
            if (!theirs.AsSpan(0, 4).SequenceEqual(Magic) || theirs[4] != Version)
            {
                throw new AuthenticationException("The other PC isn't speaking MwbLite's secure protocol (version " + theirs[4] + ").");
            }

            bool theyProved = CryptographicOperations.FixedTimeEquals(theirs.AsSpan(5), expected);
            ssl.WriteByte(theyProved ? VerdictOk : VerdictMismatch);
            ssl.Flush();
            int verdict = ssl.ReadByte();

            if (!theyProved || verdict != VerdictOk)
            {
                throw new KeyMismatchException("The security keys don't match.");
            }
        }

        internal enum ProbeResult
        {
            KeyMatches,
            KeyMismatch,
            OlderVersion,
            NoAnswer,
        }

        /// <summary>
        /// Check connection: opens a secure connection to a PC's MwbLite port and runs the key proof with the given
        /// key, then hangs up. Tells "key matches" from "different key" and "older version" for certain.
        /// </summary>
        internal static async System.Threading.Tasks.Task<ProbeResult> ProbeAsync(IPAddress address, int port, string securityKey, TimeSpan timeout, System.Threading.CancellationToken ct)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var connectTimeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(timeout);
            try
            {
                await socket.ConnectAsync(address, port, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                return ProbeResult.NoAnswer;
            }

            return await System.Threading.Tasks.Task.Run(
                () =>
                {
                    socket.ReceiveTimeout = socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
                    using var ssl = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
                    try
                    {
                        Authenticate(ssl, isClient: true);
                        ProveKey(ssl, isClient: true, securityKey);
                        return ProbeResult.KeyMatches;
                    }
                    catch (KeyMismatchException)
                    {
                        return ProbeResult.KeyMismatch;
                    }
                    catch (AuthenticationException)
                    {
                        return ProbeResult.OlderVersion;
                    }
                    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
                    {
                        return ProbeResult.NoAnswer;
                    }
                },
                ct).ConfigureAwait(false);
        }

        internal static byte[] Proof(string securityKey, byte role, byte[] clientCert, byte[] serverCert)
        {
            byte[] k = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(securityKey ?? string.Empty), 32, KdfSalt);
            byte[] message = new[] { role }.Concat(SHA256.HashData(clientCert)).Concat(SHA256.HashData(serverCert)).ToArray();
            return HMACSHA256.HashData(k, message);
        }

        private static byte[] ReadExactly(Stream s, int count)
        {
            byte[] buf = new byte[count];
            s.ReadExactly(buf, 0, count);
            return buf;
        }

        // ---- the certificate ------------------------------------------------------------------------------------

        // One certificate per Windows account (the user, or SYSTEM in service mode), kept in that account's "My"
        // store so the key isn't recreated on every start. Windows' TLS stack needs a stored (not ephemeral) key.
        private static X509Certificate2 LoadOrCreateStoreCertificate()
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            X509Certificate2 existing = store.Certificates
                .Where(c => c.FriendlyName == CertificateName && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();
            if (existing != null)
            {
                return existing;
            }

            foreach (X509Certificate2 old in store.Certificates.Where(c => c.FriendlyName == CertificateName).ToList())
            {
                store.Remove(old);
            }

            X509Certificate2 created = CreateCertificate(X509KeyStorageFlags.PersistKeySet);
            created.FriendlyName = CertificateName;
            store.Add(created);
            LiteLog.Write(nameof(LiteSecureChannel), "Created the TLS certificate " + created.Thumbprint);
            return created;
        }

        /// <summary>A new self-signed ECDSA P-256 certificate, imported so Windows' TLS stack can use its key.</summary>
        internal static X509Certificate2 CreateCertificate(X509KeyStorageFlags flags)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=MwbLite " + Environment.MachineName, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));
            using X509Certificate2 temp = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            return X509CertificateLoader.LoadPkcs12(temp.Export(X509ContentType.Pkcs12), null, flags);
        }
    }
}
