// MwbLite: the release key. Every release publishes SHA256SUMS.txt (the SHA-256 of Setup and the MSI) and
// SHA256SUMS.txt.sig, a signature over those exact bytes made with a private key that only exists on the maintainer's
// PC (tools\release-key.ps1). An update is installed only if the signature verifies with the public key built in here,
// so someone who gets hold of the GitHub account can publish a release but not one that MwbLite will install.
//
// - Algorithm: ECDSA on P-256 with SHA-256, all in .NET. The signature file is the base64 of the 64-byte IEEE P1363
//   form (r followed by s), nothing else.
// - The public key is a base64 SubjectPublicKeyInfo. PublicKeyBase64 is rewritten by `release-key.ps1 -New`; while it
//   still holds the placeholder, nothing verifies and no update is installed.
// - PublicKeyProvider exists for the unit tests, which sign with a key of their own.
using System;
using System.Security.Cryptography;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteReleaseKey
    {
        // The line below is rewritten by tools\release-key.ps1 -New: keep its form (const string, one line).
        internal const string PublicKeyBase64 = "PLACEHOLDER-RUN-tools-release-key.ps1--New";

        // OID of the NIST P-256 curve (secp256r1 / prime256v1).
        private const string P256Oid = "1.2.840.10045.3.1.7";

        /// <summary>The public key Verify uses. Tests replace it with their own and put it back.</summary>
        internal static Func<string> PublicKeyProvider { get; set; } = () => PublicKeyBase64;

        /// <summary>True if a real key is built in (not the placeholder).</summary>
        internal static bool IsConfigured => TryImport(PublicKeyBase64) != null;

        /// <summary>True if <paramref name="sigBase64"/> is a valid signature of <paramref name="sums"/> by the release key.</summary>
        internal static bool Verify(byte[] sums, string sigBase64) => Verify(sums, sigBase64, PublicKeyProvider());

        /// <summary>True if <paramref name="sigBase64"/> is a valid signature of <paramref name="sums"/> by the given public key.</summary>
        internal static bool Verify(byte[] sums, string sigBase64, string publicKeyBase64)
        {
            if (sums == null || string.IsNullOrWhiteSpace(sigBase64))
            {
                return false;
            }

            using ECDsa key = TryImport(publicKeyBase64);
            if (key == null)
            {
                return false;
            }

            try
            {
                byte[] sig = Convert.FromBase64String(sigBase64.Trim());
                return sig.Length == 64 && key.VerifyData(sums, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (Exception e) when (e is FormatException or CryptographicException)
            {
                return false;
            }
        }

        // The key from its base64 SubjectPublicKeyInfo, or null if it isn't a P-256 public key (e.g. the placeholder).
        private static ECDsa TryImport(string publicKeyBase64)
        {
            if (string.IsNullOrWhiteSpace(publicKeyBase64))
            {
                return null;
            }

            ECDsa key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
                if (key.ExportParameters(false).Curve.Oid?.Value == P256Oid)
                {
                    return key;
                }
            }
            catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
            {
            }

            key.Dispose();
            return null;
        }
    }
}
