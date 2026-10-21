// MwbLite: copy/drag folders and several files at once.
//
// MWB transfers exactly one file per copy or drag. MwbLite keeps that protocol unchanged and packs a
// selection with folders or several files into one zip ("<name>.mwbbundle.zip"), which the receiving
// MwbLite unpacks again. (A receiver running stock Mouse Without Borders just gets the zip.)
//
//  Sender, helper process (the user):   Prepare() writes a small "<name>.mwbbundle" manifest and builds the zip
//                                       in the background; the manifest path is what MWB announces.
//  Sender, main process at send time:   ResolveForSend() waits briefly for the zip and returns its path.
//  Receiver, main process:              ExtractReceived() unpacks the zip next to where MWB saved it.
//
// Copy/paste keeps MWB's 100 MB limit (now for the whole selection); drag and drop has no limit.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteFileBundle
    {
        internal const string ManifestExtension = ".mwbbundle";
        internal const string ZipSuffix = ".mwbbundle.zip";
        internal const long CopyPasteLimit = 100L * 1024 * 1024; // MWB's own copy/paste limit (Clipboard.cs)
        private static readonly TimeSpan WaitForZip = TimeSpan.FromSeconds(20); // the receiver waits 30 s
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);
        private static readonly char[] Unsafe = Path.GetInvalidFileNameChars().Concat(new[] { '*' }).ToArray();

        private static readonly EnumerationOptions Walk = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, // no junction loops, no desktop.ini
        };

        private sealed class Manifest
        {
            public string Zip { get; set; }

            public string Error { get; set; }

            public List<string> Items { get; set; }
        }

        internal static string OutgoingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MwbLite", "outgoing");

        // ---- sender: helper process -------------------------------------------------------------

        /// <summary>
        /// Returns null when the selection is a single file (sent as-is, exactly as upstream). Otherwise returns
        /// the path of a bundle manifest and starts building the zip in the background.
        /// </summary>
        internal static string Prepare(IEnumerable<string> paths, bool limitTotalSize, string outgoingDir = null)
        {
            var items = paths.Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (items.Count == 0 || (items.Count == 1 && File.Exists(items[0])))
            {
                return null;
            }

            string dir = outgoingDir ?? OutgoingDir;
            Directory.CreateDirectory(dir);
            CleanOld(dir);

            string name = BundleName(items);
            string baseName = UniqueName(dir, name, ManifestExtension);
            string manifestPath = Path.Combine(dir, baseName + ManifestExtension);
            var manifest = new Manifest { Zip = Path.Combine(dir, baseName + ZipSuffix), Items = items };

            if (limitTotalSize && SizeExceeds(items, CopyPasteLimit))
            {
                manifest.Error = $"{name} - File too big (more than 100 MB in total), please drag and drop it instead!";
                WriteManifest(manifestPath, manifest);
                return manifestPath;
            }

            WriteManifest(manifestPath, manifest);

            // Small copy/paste selections are compressed; drag and drop favours speed (the receiver waits at most 30 s).
            var level = limitTotalSize ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
            _ = Task.Run(() => Build(manifestPath, manifest, level));
            return manifestPath;
        }

        private static void Build(string manifestPath, Manifest manifest, CompressionLevel level)
        {
            string partial = manifest.Zip + ".partial";
            try
            {
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (string item in manifest.Items)
                    {
                        string top = Path.GetFileName(item);
                        if (File.Exists(item))
                        {
                            _ = zip.CreateEntryFromFile(item, top, level);
                            continue;
                        }

                        _ = zip.CreateEntry(top + "/");
                        foreach (string sub in Directory.EnumerateFileSystemEntries(item, "*", Walk))
                        {
                            string entry = top + "/" + Path.GetRelativePath(item, sub).Replace('\\', '/');
                            if (Directory.Exists(sub))
                            {
                                _ = zip.CreateEntry(entry + "/");
                            }
                            else
                            {
                                _ = zip.CreateEntryFromFile(sub, entry, level);
                            }
                        }
                    }
                }

                File.Move(partial, manifest.Zip, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                manifest.Error = $"{Path.GetFileNameWithoutExtension(manifestPath)} could not be packed for sending";
                WriteManifest(manifestPath, manifest);
                LiteLog.Write(nameof(LiteFileBundle), "Packing failed: " + e.Message);
                TryDelete(partial);
            }
        }

        // ---- sender: main process, when a peer asks for the data --------------------------------

        /// <summary>
        /// For a bundle manifest, waits for its zip and returns the zip path; for anything else returns the path
        /// unchanged. On failure returns null and a short, file-name-safe message for the receiver.
        /// </summary>
        internal static string ResolveForSend(string path, out string error)
        {
            error = null;
            if (path == null || !path.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                return path;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            DateTime deadline = DateTime.UtcNow + WaitForZip;
            while (true)
            {
                Manifest manifest = ReadManifest(path);
                if (manifest == null)
                {
                    error = $"{name} could not be sent";
                    return null;
                }

                if (manifest.Error != null)
                {
                    error = manifest.Error;
                    return null;
                }

                if (File.Exists(manifest.Zip))
                {
                    return manifest.Zip;
                }

                if (DateTime.UtcNow > deadline)
                {
                    error = $"{name} is still being packed (large folder). Try again in a moment.";
                    return null;
                }

                Thread.Sleep(250);
            }
        }

        // ---- receiver -----------------------------------------------------------------------------

        internal static bool IsBundle(string file) => file != null && file.EndsWith(ZipSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Unpacks a received bundle into <paramref name="targetDir"/> (top-level items get "(2)" etc. instead of
        /// overwriting), deletes the zip and returns the top-level items. Entries that would land outside the
        /// target folder are rejected.
        /// </summary>
        internal static List<string> ExtractReceived(string zipPath, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            string root = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var topLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string[] parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                    {
                        continue;
                    }

                    if (parts.Any(p => p is "." or ".." || p.IndexOfAny(Unsafe) >= 0))
                    {
                        throw new InvalidDataException("Invalid bundle entry name: " + entry.FullName);
                    }

                    bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                    if (!topLevel.TryGetValue(parts[0], out string mapped))
                    {
                        bool topIsDirectory = parts.Length > 1 || isDirectory;
                        mapped = topIsDirectory ? UniqueName(targetDir, parts[0], string.Empty) : UniqueFileName(targetDir, parts[0]);
                        topLevel[parts[0]] = mapped;
                    }

                    string destination = Path.GetFullPath(Path.Combine(new[] { targetDir, mapped }.Concat(parts.Skip(1)).ToArray()));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Bundle entry outside the target folder: " + entry.FullName);
                    }

                    if (isDirectory)
                    {
                        Directory.CreateDirectory(destination);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination, overwrite: false);
                    }
                }
            }

            File.Delete(zipPath);
            return topLevel.Values.Select(v => Path.Combine(targetDir, v)).ToList();
        }

        /// <summary>
        /// ExtractReceived that never throws: on failure the zip is kept (so the files are not lost) and logged.
        /// <paramref name="description"/> is e.g. "Photos and 2 more (3 items)" for the received-file balloon.
        /// </summary>
        internal static bool TryExtract(string zipPath, string targetDir, out List<string> items, out string description)
        {
            description = Path.GetFileName(zipPath);
            try
            {
                items = ExtractReceived(zipPath, targetDir);
                description = items.Count == 0 ? "an empty folder"
                    : items.Count == 1
                    ? Path.GetFileName(items[0])
                    : $"{Path.GetFileName(items[0])} and {items.Count - 1} more ({items.Count} items)";
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                LiteLog.Write(nameof(LiteFileBundle), $"Could not unpack {zipPath}: {e.Message}");
                items = null;
                return false;
            }
        }

        /// <summary>Removes unpacked copy/paste bundles older than a day (MWB already does this for files).</summary>
        internal static void CleanOldBundleFolders(string storageDir)
        {
            try
            {
                foreach (var dir in new DirectoryInfo(storageDir).EnumerateDirectories("bundle-*"))
                {
                    if (dir.CreationTimeUtc + KeepFor < DateTime.UtcNow)
                    {
                        dir.Delete(recursive: true);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteFileBundle), "Cleanup: " + e.Message);
            }
        }

        // ---- helpers ------------------------------------------------------------------------------

        private static string BundleName(List<string> items)
        {
            string first = Path.GetFileName(items[0]);
            string name = items.Count == 1 ? first : $"{first} and {items.Count - 1} more";
            name = new string(name.Select(c => Array.IndexOf(Unsafe, c) >= 0 ? '_' : c).ToArray());
            return name.Length > 80 ? name[..80] : name;
        }

        private static bool SizeExceeds(List<string> items, long limit)
        {
            long total = 0;
            foreach (string item in items)
            {
                IEnumerable<string> files = File.Exists(item) ? new[] { item } : Directory.EnumerateFiles(item, "*", Walk);
                foreach (string f in files)
                {
                    total += new FileInfo(f).Length;
                    if (total > limit)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // "name", "name (2)", ... not yet used in dir (for the given extension; "" = folder).
        private static string UniqueName(string dir, string name, string extension)
        {
            string candidate = name;
            for (int i = 2; File.Exists(Path.Combine(dir, candidate + extension)) || Directory.Exists(Path.Combine(dir, candidate + extension)) ||
                (extension == ManifestExtension && File.Exists(Path.Combine(dir, candidate + ZipSuffix))); i++)
            {
                candidate = $"{name} ({i})";
            }

            return candidate;
        }

        private static string UniqueFileName(string dir, string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            string candidate = fileName;
            for (int i = 2; File.Exists(Path.Combine(dir, candidate)) || Directory.Exists(Path.Combine(dir, candidate)); i++)
            {
                candidate = $"{stem} ({i}){ext}";
            }

            return candidate;
        }

        private static void CleanOld(string dir)
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
            {
                if (file.LastWriteTimeUtc + KeepFor < DateTime.UtcNow)
                {
                    TryDelete(file.FullName);
                }
            }
        }

        private static void WriteManifest(string path, Manifest manifest)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(manifest));
            File.Move(tmp, path, overwrite: true);
        }

        private static Manifest ReadManifest(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
