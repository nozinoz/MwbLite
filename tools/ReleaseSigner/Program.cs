// MwbLite release signer: the maintainer's tool for signed releases. Run it through tools\release-key.ps1.
//
// An MwbLite release is trusted by installed copies only if its SHA256SUMS.txt is signed with the release key, whose
// public half is built into the app (src\App\Lite\LiteReleaseKey.cs). The private half lives only on the maintainer's
// PC, in a file encrypted with DPAPI for the signed-in Windows user (so it is useless when copied to another PC or
// user), plus an offline backup protected by a passphrase.
//
//   new                 make a key pair; store the private key in the DPAPI file; put the public key in LiteReleaseKey.cs
//   backup <file>       write the private key as a passphrase-encrypted PKCS#8 PEM (PBES2, AES-256, PBKDF2-SHA256)
//   restore <file>      read such a backup back into the DPAPI file (on a new PC, or after a Windows reinstall)
//   sign <sums-file>    write <sums-file>.sig, and check it against the public key in LiteReleaseKey.cs
//
// Options: --key <file> (the DPAPI file; default %USERPROFILE%\.mwblite-release\release-key.dpapi),
// --source <file> (LiteReleaseKey.cs). The passphrase for backup and restore comes from the environment variable
// MWBLITE_RELEASE_PASSPHRASE, which release-key.ps1 fills from a hidden prompt.
//
// Signature format (what LiteReleaseKey.Verify expects): ECDSA on P-256 with SHA-256 over the exact bytes of the file,
// as base64 of the 64-byte IEEE P1363 form (r followed by s).
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static class Program
{
    private const string PassphraseVariable = "MWBLITE_RELEASE_PASSPHRASE";
    private const int MinPassphraseLength = 10;
    private const int Pbkdf2Iterations = 600_000;
    private const string P256Oid = "1.2.840.10045.3.1.7";

    // Mixed into DPAPI so that only this tool (not any program running as the same user that just asks DPAPI) can read it
    // casually. It is not a secret; DPAPI's protection is the Windows account.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MwbLite release key v1");

    private static readonly Regex KeyLine = new("""(internal const string PublicKeyBase64 = ")([^"]*)(";)""", RegexOptions.CultureInvariant);

    private sealed class ToolException(string message) : Exception(message);

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (ToolException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string? command = null, argument = null;
        string keyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mwblite-release", "release-key.dpapi");
        string? source = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--key" when i + 1 < args.Length:
                    keyFile = Path.GetFullPath(args[++i]);
                    break;
                case "--source" when i + 1 < args.Length:
                    source = Path.GetFullPath(args[++i]);
                    break;
                default:
                    if (command == null)
                    {
                        command = args[i];
                    }
                    else if (argument == null)
                    {
                        argument = args[i];
                    }
                    else
                    {
                        throw new ToolException("Unexpected argument: " + args[i]);
                    }

                    break;
            }
        }

        source ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "App", "Lite", "LiteReleaseKey.cs"));
        return command switch
        {
            "new" => NewKey(keyFile, source),
            "backup" => Backup(keyFile, Need(argument, "backup <file>")),
            "restore" => Restore(keyFile, Need(argument, "restore <file>")),
            "sign" => Sign(keyFile, source, Need(argument, "sign <SHA256SUMS.txt>")),
            _ => throw new ToolException("Usage: ReleaseSigner new | backup <file> | restore <file> | sign <SHA256SUMS.txt> [--key <file>] [--source <LiteReleaseKey.cs>]"),
        };
    }

    private static string Need(string? argument, string usage) =>
        argument == null ? throw new ToolException("Usage: ReleaseSigner " + usage) : Path.GetFullPath(argument);

    // ---- new ---------------------------------------------------------------------------------------------

    private static int NewKey(string keyFile, string source)
    {
        if (File.Exists(keyFile))
        {
            throw new ToolException($"A release key already exists ({keyFile}). Creating another would make every installed MwbLite refuse your releases; it is not done by accident. Delete that file yourself if you really mean to start over.");
        }

        string text = ReadSource(source, out _);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        byte[] pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            WriteKeyFile(keyFile, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        WriteSource(source, KeyLine.Replace(text, m => m.Groups[1].Value + publicKey + m.Groups[3].Value, 1));

        Console.WriteLine("Release key created.");
        Console.WriteLine("  Private key (encrypted for your Windows account): " + keyFile);
        Console.WriteLine("  Public key, now in " + source + ":");
        Console.WriteLine("  " + publicKey);
        Console.WriteLine();
        Console.WriteLine("NEXT: back the private key up now (tools\\release-key.ps1 -Backup <file>) and keep the file and its passphrase somewhere safe, off this PC.");
        Console.WriteLine("If you lose the key, installed copies can never be updated again by themselves: everyone must install the next version by hand once.");
        Console.WriteLine("Then commit LiteReleaseKey.cs and build a release with this public key before publishing.");
        return 0;
    }

    // ---- backup and restore ------------------------------------------------------------------------------

    private static int Backup(string keyFile, string backupFile)
    {
        if (File.Exists(backupFile))
        {
            throw new ToolException(backupFile + " already exists; choose a new file name.");
        }

        string passphrase = Passphrase();
        using ECDsa key = LoadKey(keyFile);
        string pem = key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Pbkdf2Iterations));

        // Read it back before trusting it.
        using (ECDsa check = ECDsa.Create())
        {
            check.ImportFromEncryptedPem(pem, passphrase);
            if (!check.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
            {
                throw new ToolException("The backup didn't read back correctly; nothing was written.");
            }
        }

        string? dir = Path.GetDirectoryName(backupFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var f = new FileStream(backupFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(f, new UTF8Encoding(false)))
        {
            w.Write(pem);
            w.Write('\n');
        }

        Console.WriteLine("Backup written to " + backupFile);
        Console.WriteLine("It is encrypted with your passphrase (AES-256, PBKDF2-SHA256, " + Pbkdf2Iterations + " rounds). Keep the file and the passphrase apart, off this PC.");
        return 0;
    }

    private static int Restore(string keyFile, string backupFile)
    {
        if (File.Exists(keyFile))
        {
            throw new ToolException($"A release key already exists ({keyFile}); not replacing it.");
        }

        string passphrase = Passphrase();
        using ECDsa key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(File.ReadAllText(backupFile), passphrase);
        }
        catch (CryptographicException)
        {
            throw new ToolException("That isn't a MwbLite release key backup, or the passphrase is wrong.");
        }

        RequireP256(key);
        byte[] pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            WriteKeyFile(keyFile, pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        Console.WriteLine("Release key restored to " + keyFile);
        Console.WriteLine("  Public key: " + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Console.WriteLine("It must be the key built into the app (src\\App\\Lite\\LiteReleaseKey.cs); -Sign checks that.");
        return 0;
    }

    // ---- sign --------------------------------------------------------------------------------------------

    private static int Sign(string keyFile, string source, string sumsFile)
    {
        if (!File.Exists(sumsFile))
        {
            throw new ToolException(sumsFile + " doesn't exist.");
        }

        // The key the app will check against.
        ReadSource(source, out string builtIn);
        using ECDsa verifier = ImportPublic(builtIn)
            ?? throw new ToolException("LiteReleaseKey.cs still has the placeholder public key. Run tools\\release-key.ps1 -New first, then rebuild.");

        byte[] sums = File.ReadAllBytes(sumsFile);
        using ECDsa key = LoadKey(keyFile);
        byte[] signature = key.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!verifier.VerifyData(sums, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new ToolException("The private key doesn't match the public key in " + source + ", so installed copies would refuse this release. Restore the right key (-Restore) or rebuild the app with the key you have. Nothing was written.");
        }

        string sigFile = sumsFile + ".sig";
        File.WriteAllText(sigFile, Convert.ToBase64String(signature), Encoding.ASCII);
        Console.WriteLine("Signed " + sumsFile);
        Console.WriteLine("  -> " + sigFile + " (verifies with the public key in LiteReleaseKey.cs)");
        return 0;
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static string Passphrase()
    {
        string? p = Environment.GetEnvironmentVariable(PassphraseVariable);
        if (string.IsNullOrEmpty(p))
        {
            throw new ToolException("No passphrase given (use tools\\release-key.ps1, which asks for it).");
        }

        return p.Length >= MinPassphraseLength ? p : throw new ToolException($"The passphrase must be at least {MinPassphraseLength} characters.");
    }

    private static void WriteKeyFile(string keyFile, byte[] pkcs8)
    {
        string? dir = Path.GetDirectoryName(keyFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] blob = Dpapi.Protect(pkcs8, Entropy);
        using var f = new FileStream(keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        f.Write(blob);
    }

    private static ECDsa LoadKey(string keyFile)
    {
        if (!File.Exists(keyFile))
        {
            throw new ToolException($"No release key at {keyFile}. Create one with tools\\release-key.ps1 -New, or bring it back from your backup with -Restore.");
        }

        byte[] pkcs8;
        try
        {
            pkcs8 = Dpapi.Unprotect(File.ReadAllBytes(keyFile), Entropy);
        }
        catch (CryptographicException)
        {
            throw new ToolException("The release key can't be unlocked. It only works for the Windows user that created it, on the PC where it was created; use -Restore with your backup.");
        }

        try
        {
            ECDsa key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            RequireP256(key);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static void RequireP256(ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid?.Value != P256Oid)
        {
            throw new ToolException("That key isn't an ECDSA P-256 key.");
        }
    }

    // A base64 SubjectPublicKeyInfo as a P-256 verifier, or null (for the placeholder).
    private static ECDsa? ImportPublic(string base64)
    {
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
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

    // LiteReleaseKey.cs as text (keeping its line endings), and the public key constant in it.
    private static string ReadSource(string source, out string constant)
    {
        if (!File.Exists(source))
        {
            throw new ToolException("Can't find " + source + " (run the tool from a MwbLite checkout).");
        }

        string text = File.ReadAllText(source);
        Match m = KeyLine.Match(text);
        if (!m.Success)
        {
            throw new ToolException("Can't find the PublicKeyBase64 constant in " + source + ".");
        }

        constant = m.Groups[2].Value;
        return text;
    }

    private static void WriteSource(string source, string text)
    {
        bool bom = File.ReadAllBytes(source).AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        File.WriteAllText(source, text, new UTF8Encoding(bom));
    }
}

// Windows DPAPI for the current user (CryptProtectData), called directly so the tool needs no NuGet package.
internal static class Dpapi
{
    private const uint UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static byte[] Protect(byte[] data, byte[] entropy) => Call(data, entropy, protect: true);

    internal static byte[] Unprotect(byte[] data, byte[] entropy) => Call(data, entropy, protect: false);

    private static byte[] Call(byte[] data, byte[] entropy, bool protect)
    {
        DataBlob input = Pin(data), extra = Pin(entropy), output;
        try
        {
            bool ok = protect
                ? CryptProtectData(ref input, "MwbLite release key", ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                _ = LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size); // the plain private key, when protecting
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(extra.Data);
        }
    }

    private static DataBlob Pin(byte[] bytes)
    {
        IntPtr p = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = p };
    }
}
