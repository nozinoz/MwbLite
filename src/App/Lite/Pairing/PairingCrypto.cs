// MwbLite pairing: key agreement and code derivation for "Add a PC" / "Join".
//
// This is a separate, short-lived exchange used only to hand the MWB security key (and the machine
// list) to a new PC. It does not change MWB's own connection protocol or encryption.
//
// Scheme (numeric comparison with a hash commitment, as in Bluetooth Secure Simple Pairing / ZRTP):
//   1. Joiner -> Host : name, C = SHA-256(label | joinerPub | joinerNonce)        (commitment)
//   2. Host -> Joiner : name, hostPub, hostNonce
//   3. Joiner -> Host : joinerPub, joinerNonce                                    (host checks C)
//   Both: shared = ECDH-P256(own, peer); TH = SHA-256(transcript)
//         sessionKey = HKDF-SHA256(shared, salt TH, "key"); code = HKDF(shared, TH, "code") mod 10^6
//   4. Both screens show the 6-digit code; the user confirms it matches on BOTH PCs.
//   5. Only then: Host -> Joiner : AES-256-GCM(sessionKey, security key + machine list + layout).
//
// Because the joiner commits to its key before seeing the host's, a man-in-the-middle cannot search
// for keys that make both screens show the same code; it gets one guess (1 in 1,000,000).
// Only .NET's built-in primitives are used (ECDiffieHellman, SHA256, HKDF, AesGcm).
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MouseWithoutBorders.Lite.Pairing
{
    internal sealed class PairingCrypto : IDisposable
    {
        internal const int Version = 1;
        private const string Label = "MwbLite pairing v1";
        private const int NonceSize = 32;
        private const int GcmNonceSize = 12;
        private const int GcmTagSize = 16;

        private readonly ECDiffieHellman ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        private byte[] sessionKey;

        internal PairingCrypto()
        {
            PublicKey = ecdh.ExportSubjectPublicKeyInfo();
            Nonce = RandomNumberGenerator.GetBytes(NonceSize);
        }

        internal byte[] PublicKey { get; }

        internal byte[] Nonce { get; }

        /// <summary>The 6-digit code both screens show (available after <see cref="Complete"/>).</summary>
        internal string Code { get; private set; }

        internal static byte[] Commitment(byte[] publicKey, byte[] nonce)
        {
            return SHA256.HashData(Concat(Encoding.UTF8.GetBytes(Label + " commit"), publicKey, nonce));
        }

        internal static bool VerifyCommitment(byte[] commitment, byte[] publicKey, byte[] nonce)
        {
            return commitment != null && CryptographicOperations.FixedTimeEquals(commitment, Commitment(publicKey, nonce));
        }

        /// <summary>Derives the session key and the code from the peer's public key and the full transcript.</summary>
        internal void Complete(byte[] peerPublicKey, string joinerName, string hostName, byte[] joinerPub, byte[] hostPub, byte[] joinerNonce, byte[] hostNonce)
        {
            if (joinerNonce?.Length != NonceSize || hostNonce?.Length != NonceSize)
            {
                throw new CryptographicException("Invalid pairing nonce.");
            }

            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
            if (peer.KeySize != 256)
            {
                throw new CryptographicException("Unexpected pairing key type.");
            }

            byte[] shared = ecdh.DeriveRawSecretAgreement(peer.PublicKey);
            try
            {
                byte[] transcript = SHA256.HashData(Concat(
                    Encoding.UTF8.GetBytes(Label),
                    Encoding.UTF8.GetBytes(joinerName),
                    Encoding.UTF8.GetBytes(hostName),
                    joinerPub,
                    hostPub,
                    joinerNonce,
                    hostNonce));

                sessionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, transcript, Encoding.UTF8.GetBytes(Label + " key"));
                byte[] codeBytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 4, transcript, Encoding.UTF8.GetBytes(Label + " code"));
                Code = (BinaryPrimitives.ReadUInt32BigEndian(codeBytes) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(shared);
            }
        }

        /// <summary>AES-256-GCM; <paramref name="purpose"/> is bound as associated data so messages cannot be swapped.</summary>
        internal byte[] Seal(string purpose, byte[] plaintext)
        {
            EnsureComplete();
            byte[] output = new byte[GcmNonceSize + GcmTagSize + plaintext.Length];
            Span<byte> nonce = output.AsSpan(0, GcmNonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var gcm = new AesGcm(sessionKey, GcmTagSize);
            gcm.Encrypt(nonce, plaintext, output.AsSpan(GcmNonceSize + GcmTagSize), output.AsSpan(GcmNonceSize, GcmTagSize), Encoding.UTF8.GetBytes(purpose));
            return output;
        }

        internal byte[] Open(string purpose, byte[] sealedData)
        {
            EnsureComplete();
            if (sealedData == null || sealedData.Length < GcmNonceSize + GcmTagSize)
            {
                throw new CryptographicException("Invalid pairing message.");
            }

            byte[] plaintext = new byte[sealedData.Length - GcmNonceSize - GcmTagSize];
            using var gcm = new AesGcm(sessionKey, GcmTagSize);
            gcm.Decrypt(sealedData.AsSpan(0, GcmNonceSize), sealedData.AsSpan(GcmNonceSize + GcmTagSize), sealedData.AsSpan(GcmNonceSize, GcmTagSize), plaintext, Encoding.UTF8.GetBytes(purpose));
            return plaintext;
        }

        public void Dispose()
        {
            ecdh.Dispose();
            if (sessionKey != null)
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
        }

        private void EnsureComplete()
        {
            if (sessionKey == null)
            {
                throw new InvalidOperationException("Pairing key agreement has not completed.");
            }
        }

        // Length-prefixed concatenation, so field boundaries are unambiguous.
        private static byte[] Concat(params byte[][] parts)
        {
            using var ms = new MemoryStream();
            Span<byte> len = stackalloc byte[4];
            foreach (byte[] part in parts)
            {
                BinaryPrimitives.WriteInt32BigEndian(len, part.Length);
                ms.Write(len);
                ms.Write(part);
            }

            return ms.ToArray();
        }
    }
}
