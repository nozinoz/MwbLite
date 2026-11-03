// MwbLite: the last addresses each PC was reached at, kept in addresses.json next to settings.json.
//
// Right after a PC wakes from sleep, Windows often can't resolve the other PCs' names yet ("No such host is known":
// LLMNR/mDNS/DNS aren't ready), and upstream MWB then gave up on that attempt. MwbLite remembers where each PC was
// last connected and tries those addresses when the name doesn't resolve (SocketStuff.StartNewTcpClient). The TLS
// key proof still decides whether whoever answers is really that PC.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteAddressBook
    {
        private const int MaxPerPc = 3;

        private static readonly Lock Gate = new();
        private static Dictionary<string, List<string>> book;

        private static string FilePath => Path.Combine(LitePaths.ConfigDir, "addresses.json");

        /// <summary>Records that <paramref name="pc"/> was connected at <paramref name="address"/> (most recent first).</summary>
        internal static void Remember(string pc, string address)
        {
            if (string.IsNullOrWhiteSpace(pc) || !IPAddress.TryParse(address, out IPAddress ip) || IPAddress.IsLoopback(ip))
            {
                return;
            }

            string key = pc.Trim().ToUpperInvariant();
            string text = ip.ToString();
            lock (Gate)
            {
                Load();
                if (!book.TryGetValue(key, out List<string> list))
                {
                    list = new List<string>();
                    book[key] = list;
                }

                if (list.Count > 0 && list[0] == text)
                {
                    return; // unchanged: nothing to write
                }

                list.Remove(text);
                list.Insert(0, text);
                if (list.Count > MaxPerPc)
                {
                    list.RemoveRange(MaxPerPc, list.Count - MaxPerPc);
                }

                Save();
            }
        }

        /// <summary>The addresses <paramref name="pc"/> was last reached at (most recent first), or none.</summary>
        internal static IPAddress[] Get(string pc)
        {
            lock (Gate)
            {
                Load();
                return book.TryGetValue((pc ?? string.Empty).Trim().ToUpperInvariant(), out List<string> list)
                    ? list.Select(a => IPAddress.TryParse(a, out IPAddress ip) ? ip : null).Where(ip => ip != null).ToArray()
                    : Array.Empty<IPAddress>();
            }
        }

        /// <summary>Tests: forget everything (and don't touch the file).</summary>
        internal static void ResetForTests() => book = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        internal static bool SaveToDisk { get; set; } = true;

        private static void Load()
        {
            if (book != null)
            {
                return;
            }

            try
            {
                book = SaveToDisk && File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath))
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                book = null;
            }

            book = new Dictionary<string, List<string>>(book ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
        }

        private static void Save()
        {
            if (!SaveToDisk)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(LitePaths.ConfigDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(book));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteAddressBook), "Could not save " + FilePath + ": " + e.Message);
            }
        }
    }
}
