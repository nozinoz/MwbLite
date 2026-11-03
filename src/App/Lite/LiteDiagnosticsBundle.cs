// MwbLite: "Save diagnostics…" on Settings > About. Collects what someone needs to work out why PCs don't connect
// into one zip the user can attach to an issue or send on:
//   logs (this user's, and the service's if this account may read them), status.json, settings.json (the security key
//   and any "dpapi:" values replaced), the update check state, the address book, the results of Check connection,
//   version and commit, Windows version, network adapters, and the Windows Firewall rules that mention MwbLite.
//
// Collecting never fails as a whole: anything missing or unreadable (the service's folder is administrators-only, for
// instance) becomes a line in summary.txt, and the rest is still saved. The security key never leaves the PC in
// the zip: settings.json goes through RedactSettings (which parses the JSON, and falls back to patterns on the text
// for a file that is not valid JSON), and the key's own text is replaced in every file added, logs included.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteDiagnosticsBundle
    {
        internal const string Redacted = "<redacted>";
        internal const string KeyRemoved = "[security key removed]";

        // A shorter "key" would also match ordinary words in a log, so it is left alone.
        private const int MinKeyLengthToScrub = 8;

        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Where the files come from (tests point it at a temporary folder).</summary>
        /// <param name="UserDir">%LOCALAPPDATA%\MwbLite.</param>
        /// <param name="ServiceDir">%ProgramData%\MwbLite, which only administrators can read.</param>
        /// <param name="Live">Also run the connection check and ask Windows for the firewall rules.</param>
        internal sealed record Locations(string UserDir, string ServiceDir, bool Live)
        {
            internal static Locations Real => new(LitePaths.UserDir, LitePaths.ServiceDir, Live: true);
        }

        internal static string DefaultFileName(string pc, DateTime localNow)
        {
            string safe = new string((pc ?? string.Empty).Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim();
            return $"MwbLite-diagnostics-{(safe.Length == 0 ? "PC" : safe)}-{localNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.zip";
        }

        // ---- redaction ------------------------------------------------------------------------------------

        private static readonly Regex KeyValue = new("(\"securityKey\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DpapiValue = new("\"" + Regex.Escape(LiteKeyProtection.Prefix) + "(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);

        /// <summary>
        /// settings.json with the security key (plain or "dpapi:...") and every other "dpapi:" value replaced. The JSON
        /// is read with a tokenizer, so escaped names and values are recognised too, and only those tokens are replaced in
        /// the original text: comments and formatting come through unchanged. A file that is not valid JSON is redacted
        /// by patterns on the text instead.
        /// </summary>
        internal static string RedactSettings(string text)
        {
            text ??= string.Empty;
            try
            {
                return RedactParsed(text);
            }
            catch (JsonException)
            {
                return RedactByPattern(text);
            }
        }

        private static string RedactParsed(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var edits = new List<(int Start, int Length, string Replacement)>();
            bool keyValueNext = false;
            while (reader.Read())
            {
                JsonTokenType type = reader.TokenType;
                if (type == JsonTokenType.PropertyName)
                {
                    keyValueNext = string.Equals(reader.GetString(), "securityKey", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                bool isKey = keyValueNext;
                keyValueNext = false;
                int start = (int)reader.TokenStartIndex;
                if (type == JsonTokenType.String)
                {
                    int length = reader.ValueSpan.Length + 2; // the raw text between the quotes, plus the quotes
                    if (isKey)
                    {
                        edits.Add((start, length, "\"" + Redacted + "\""));
                    }
                    else if (reader.GetString().StartsWith(LiteKeyProtection.Prefix, StringComparison.Ordinal))
                    {
                        edits.Add((start, length, "\"" + LiteKeyProtection.Prefix + Redacted + "\""));
                    }
                }
                else if (isKey && type is JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False)
                {
                    edits.Add((start, reader.ValueSpan.Length, "\"" + Redacted + "\""));
                }
            }

            if (edits.Count == 0)
            {
                return text;
            }

            using var result = new MemoryStream(bytes.Length);
            int at = 0;
            foreach ((int Start, int Length, string Replacement) in edits)
            {
                result.Write(bytes, at, Start - at);
                byte[] insert = Encoding.UTF8.GetBytes(Replacement);
                result.Write(insert, 0, insert.Length);
                at = Start + Length;
            }

            result.Write(bytes, at, bytes.Length - at);
            return Encoding.UTF8.GetString(result.GetBuffer(), 0, (int)result.Length);
        }

        private static string RedactByPattern(string text)
        {
            text = KeyValue.Replace(text, "$1\"" + Redacted + "\"");
            return DpapiValue.Replace(text, "\"" + LiteKeyProtection.Prefix + Redacted + "\"");
        }

        /// <summary>
        /// <paramref name="text"/> with the literal text of each security key (those of at least 8 characters) replaced by
        /// "[security key removed]". For files that aren't settings.json: logs, status, the connection check.
        /// </summary>
        internal static string RemoveKeys(string text, IEnumerable<string> keys)
        {
            foreach (string key in (keys ?? Enumerable.Empty<string>()).Where(k => k != null && k.Length >= MinKeyLengthToScrub).Distinct(StringComparer.Ordinal).OrderByDescending(k => k.Length))
            {
                text = text.Replace(key, KeyRemoved, StringComparison.Ordinal);
            }

            return text;
        }

        // The key in the settings being collected, and the one the running engine uses (they differ while an edit is pending).
        private static string[] KeysToRemove(LiteConfig cfg)
        {
            string engineKey = null;
            try
            {
                engineKey = LiteSecureChannel.SecurityKey();
            }
            catch (Exception e) when (e is NullReferenceException or TypeInitializationException)
            {
                // not running in the engine: no key there
            }

            return new[] { cfg?.SecurityKey, engineKey }.Where(k => !string.IsNullOrEmpty(k)).ToArray();
        }

        // ---- text of the other items ----------------------------------------------------------------------

        /// <summary>
        /// The rules in "netsh advfirewall firewall show rule name=all" output that mention MwbLite (in their name or their
        /// program path). Each rule is a block of lines separated from the next by an empty line, and whole blocks are kept so
        /// the ports, action and profiles stay with the name. Empty if there are none.
        /// </summary>
        internal static string FilterFirewallRules(string netshOutput)
        {
            var rules = Regex.Split((netshOutput ?? string.Empty).Replace("\r\n", "\n"), @"\n\s*\n")
                .Where(block => block.Contains(LitePaths.AppName, StringComparison.OrdinalIgnoreCase))
                .Select(block => block.Trim('\n'));
            return string.Join("\r\n\r\n", rules.Select(r => r.Replace("\n", "\r\n")));
        }

        internal static string FormatAdapter(string name, string type, string status, IEnumerable<string> ipv4)
        {
            string addresses = string.Join(", ", ipv4);
            return $"{name} ({type}, {status}): {(addresses.Length == 0 ? "no IPv4 address" : addresses)}";
        }

        internal static string FormatCheck(CheckItem item)
        {
            string tag = item.Result switch
            {
                CheckResult.Ok => "OK",
                CheckResult.Warning => "WARNING",
                CheckResult.Problem => "PROBLEM",
                _ => "INFO",
            };
            return $"[{tag}] {item.Title}: {item.Detail}";
        }

        private static string SystemInfo()
        {
            var sb = new StringBuilder();
            sb.Append("MwbLite ").Append(LiteUpdate.CurrentText);
            if (LiteUpdate.Commit.Length > 0)
            {
                sb.Append(" (build ").Append(LiteUpdate.Commit).Append(')');
            }

            sb.Append("\r\n");
            sb.Append("Windows: ").Append(RuntimeInformation.OSDescription).Append(' ').Append(RuntimeInformation.OSArchitecture)
                .Append(" (").Append(Environment.OSVersion.Version).Append(")\r\n");
            sb.Append(".NET: ").Append(RuntimeInformation.FrameworkDescription).Append("\r\n");
            sb.Append("This PC: ").Append(LiteConfig.NormalizeName(Environment.MachineName)).Append("\r\n");
            sb.Append("Signed in as: ").Append(Environment.UserName).Append("\r\n");
            sb.Append("Installed by Setup: ").Append(LitePaths.IsMsiInstalled ? "yes" : "no").Append("\r\n");
            sb.Append("Running from: ").Append(LitePaths.ExeDir).Append("\r\n");
            sb.Append("Time: ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append("\r\n");
            return sb.ToString();
        }

        private static string Adapters()
        {
            var lines = new List<string>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipv4 = new List<string>();
                try
                {
                    ipv4.AddRange(ni.GetIPProperties().UnicastAddresses
                        .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(u => u.Address + "/" + u.PrefixLength));
                }
                catch (NetworkInformationException)
                {
                }

                lines.Add(FormatAdapter(ni.Name, ni.NetworkInterfaceType.ToString(), ni.OperationalStatus.ToString(), ipv4));
            }

            return string.Join("\r\n", lines) + "\r\n";
        }

        // ---- the zip --------------------------------------------------------------------------------------

        /// <summary>
        /// Writes the zip to <paramref name="zipPath"/> (through a temporary file, so a failure leaves nothing half-written).
        /// Returns what could not be included, one line each (also written to summary.txt). Throws only if the zip itself
        /// cannot be written.
        /// </summary>
        internal static async Task<List<string>> CreateAsync(string zipPath, LiteConfig cfg, Locations where, CancellationToken ct)
        {
            var notes = new List<string>();
            var included = new List<string>();
            string[] keys = KeysToRemove(cfg);
            string tmp = zipPath + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    // Every text goes through RemoveKeys, so the key can't get in through a log line or a status field.
                    void AddText(string entry, string text, DateTimeOffset? written = null)
                    {
                        ZipArchiveEntry e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                        e.LastWriteTime = written ?? DateTimeOffset.Now;
                        using Stream s = e.Open();
                        byte[] bytes = new UTF8Encoding(false).GetBytes(RemoveKeys(text, keys));
                        s.Write(bytes, 0, bytes.Length);
                        included.Add(entry);
                    }

                    // One file from disk. A file that isn't there, or that this account may not read, is a note, not an error.
                    void AddFile(string entry, string path, string label, Func<string, string> transform = null)
                    {
                        try
                        {
                            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using var reader = new StreamReader(src, Encoding.UTF8);
                            string text = reader.ReadToEnd();
                            AddText(entry, transform == null ? text : transform(text), File.GetLastWriteTime(path));
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            notes.Add($"{label}: not found ({path}).");
                        }
                        catch (UnauthorizedAccessException)
                        {
                            notes.Add($"{label}: this account may not read it ({path}). Run MwbLite Settings as an administrator to include it.");
                        }
                        catch (IOException ex)
                        {
                            notes.Add($"{label}: couldn't read it ({ex.Message}).");
                        }
                    }

                    void AddFolderFiles(string entryFolder, string folder, string label)
                    {
                        string[] files;
                        try
                        {
                            files = Directory.GetFiles(folder);
                        }
                        catch (Exception ex) when (ex is DirectoryNotFoundException)
                        {
                            notes.Add($"{label}: no logs folder yet ({folder}).");
                            return;
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                        {
                            notes.Add($"{label}: this account may not read it ({folder}). Run MwbLite Settings as an administrator to include it.");
                            return;
                        }

                        if (files.Length == 0)
                        {
                            notes.Add($"{label}: the logs folder is empty ({folder}).");
                        }

                        foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        {
                            AddFile(entryFolder + "/" + Path.GetFileName(file), file, label + " " + Path.GetFileName(file));
                        }
                    }

                    // Anything unexpected in one item must not cost the others.
                    async Task Guarded(string label, Func<Task> work)
                    {
                        try
                        {
                            await work().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            notes.Add($"{label}: failed ({ex.GetType().Name}: {ex.Message}).");
                        }
                    }

                    await Guarded("Version and system", () => { AddText("system.txt", SystemInfo()); return Task.CompletedTask; }).ConfigureAwait(false);
                    await Guarded("Network adapters", () => { AddText("network-adapters.txt", Adapters()); return Task.CompletedTask; }).ConfigureAwait(false);

                    // This user's files, then the service's (SYSTEM) copies in service/.
                    (string Prefix, string Dir, string Who, string Status)[] sets =
                    {
                        (string.Empty, where.UserDir, "User", Path.Combine(where.UserDir, "status.json")),
                        ("service/", where.ServiceDir, "Service", Path.Combine(where.ServiceDir, "status", "status.json")),
                    };
                    foreach (var (prefix, dir, who, status) in sets)
                    {
                        await Guarded(who + " files", () =>
                        {
                            if (!Directory.Exists(dir))
                            {
                                notes.Add($"{who} files: {dir} isn't there, or this account may not see it" + (prefix.Length > 0 ? " (service mode isn't set up, or run MwbLite Settings as an administrator to include it)." : "."));
                                return Task.CompletedTask;
                            }

                            AddFile(prefix + "settings.json", Path.Combine(dir, LitePaths.SettingsFileName), who + " settings.json", RedactSettings);
                            AddFile(prefix + "status.json", status, who + " status.json");
                            AddFile(prefix + "update.json", Path.Combine(dir, "update.json"), who + " update check state (update.json)");
                            AddFile(prefix + "addresses.json", Path.Combine(dir, "addresses.json"), who + " address book (addresses.json)");
                            AddFolderFiles(prefix + "logs", Path.Combine(dir, "logs"), who + " log");
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
                    }

                    if (where.Live)
                    {
                        await Guarded("Connection check", async () =>
                        {
                            if (cfg == null)
                            {
                                notes.Add("Connection check: skipped, settings.json couldn't be read.");
                                return;
                            }

                            AddText("connection-check.txt", await RunConnectionCheckAsync(cfg, notes, ct).ConfigureAwait(false));
                        }).ConfigureAwait(false);

                        await Guarded("Firewall rules", async () =>
                        {
                            string rules = FilterFirewallRules(await RunNetshAsync(ct).ConfigureAwait(false));
                            AddText("firewall.txt", rules.Length == 0
                                ? "No Windows Firewall rule mentions MwbLite.\r\n"
                                : "Windows Firewall rules that mention MwbLite (netsh advfirewall firewall show rule name=all):\r\n\r\n" + rules + "\r\n");
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        notes.Add("Connection check and firewall rules: not collected.");
                    }

                    AddText("summary.txt", Summary(included, notes));
                }

                File.Move(tmp, zipPath, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return notes;
        }

        private static string Summary(List<string> included, List<string> notes)
        {
            var sb = new StringBuilder();
            sb.Append("MwbLite diagnostics, saved ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\r\n\r\n");
            sb.Append(SystemInfo()).Append("\r\n");
            sb.Append("The security key is not in this zip: settings.json shows it as ").Append(Redacted).Append(".\r\n\r\n");
            sb.Append("Not included (").Append(notes.Count).Append("):\r\n");
            foreach (string n in notes.Count == 0 ? new List<string> { "nothing, every item was collected." } : notes)
            {
                sb.Append("  - ").Append(n).Append("\r\n");
            }

            sb.Append("\r\nIncluded (").Append(included.Count + 1).Append("):\r\n");
            foreach (string name in included.Append("summary.txt"))
            {
                sb.Append("  ").Append(name).Append("\r\n");
            }

            return sb.ToString();
        }

        // ---- slow items -----------------------------------------------------------------------------------

        private static async Task<string> RunConnectionCheckAsync(LiteConfig cfg, List<string> notes, CancellationToken ct)
        {
            var items = new List<CheckItem>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);
            bool finished = true;
            try
            {
                await Task.Run(() => LiteDiagnostics.RunAsync(cfg.Clone(), item =>
                {
                    lock (items)
                    {
                        items.Add(item);
                    }
                }, timeout.Token), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                finished = false;
                notes.Add("Connection check: stopped after " + (int)CheckTimeout.TotalSeconds + " seconds, so the results may be incomplete.");
            }

            var sb = new StringBuilder("MwbLite connection check, " + DateTime.Now.ToString("g", CultureInfo.InvariantCulture) + (finished ? string.Empty : " (incomplete)") + "\r\n\r\n");
            lock (items)
            {
                foreach (CheckItem item in items)
                {
                    sb.Append(FormatCheck(item)).Append("\r\n");
                }
            }

            return sb.ToString();
        }

        private static async Task<string> RunNetshAsync(CancellationToken ct)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "advfirewall firewall show rule name=all")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = OemEncoding(),
            };
            using Process p = Process.Start(info) ?? throw new InvalidOperationException("netsh did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NetshTimeout);
            Task<string> output = p.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                string text = await output.ConfigureAwait(false);
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return text;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                throw new TimeoutException("netsh took longer than " + (int)NetshTimeout.TotalSeconds + " seconds.");
            }
        }

        // netsh writes in the console's (OEM) code page.
        private static Encoding OemEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.UTF8;
            }
        }
    }
}
