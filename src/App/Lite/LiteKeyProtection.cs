// MwbLite: the security key is stored encrypted in settings.json ("securityKey": "dpapi:<base64>").
//
// Windows DPAPI does the encryption:
//  - the user's %LOCALAPPDATA%\MwbLite\settings.json uses the user's own key (CurrentUser scope): only that Windows
//    account on that PC can read it, so a copied or backed-up file doesn't give the key away;
//  - the service's %ProgramData%\MwbLite copy uses LocalMachine scope, so the SYSTEM instances and the elevated
//    "--apply-config" (possibly another admin account) can read it. Any program on the PC could decrypt that
//    scope, so what protects the service copy is still its SYSTEM/Administrators-only folder; DPAPI adds that the
//    file is useless anywhere else.
// In memory the key is plain text (LiteConfig.SecurityKey), so nothing else changes. A plain key in the file (older
// versions, or typed by hand) still works and is encrypted the next time the file is written.
using System;
using System.Security.Cryptography;
using System.Text;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteKeyProtection
    {
        internal const string Prefix = "dpapi:";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MwbLite security key v1");

        internal static bool IsProtected(string stored) => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

        internal static string Protect(string plain, bool machineScope)
        {
            if (string.IsNullOrEmpty(plain) || IsProtected(plain))
            {
                return plain ?? string.Empty;
            }

            byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(blob);
        }

        /// <summary>Plain values pass through. False if a protected value can't be decrypted here (another user or PC).</summary>
        internal static bool TryUnprotect(string stored, out string plain)
        {
            if (!IsProtected(stored))
            {
                plain = stored ?? string.Empty;
                return true;
            }

            try
            {
                byte[] blob = Convert.FromBase64String(stored[Prefix.Length..]);
                byte[] data;
                try
                {
                    data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
                }

                plain = Encoding.UTF8.GetString(data);
                return true;
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                plain = string.Empty;
                return false;
            }
        }
    }
}
