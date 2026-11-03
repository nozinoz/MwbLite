// MwbLite: new-version check and update, from the project's public GitHub Releases.
//
// - Check: one HTTPS GET of https://api.github.com/repos/<repo>/releases/latest (nothing about the PC or user is
//   sent beyond a "MwbLite/<version>" user agent). Weekly in the background (checkForUpdates, default on), and on
//   demand from Settings > About.
// - Update: downloads the release's MwbLite-Setup-<version>.exe over HTTPS, checks its size and SHA-256 (the digest
//   GitHub reports for the asset, or the release's SHA256SUMS.txt), then installs it. Two ways:
//   * With the "MwbLite Update" scheduled task (added by Setup, see LiteUpdateTask), TryStartElevatedUpdate starts
//     the task and LiteUpdater does the download and install as SYSTEM, silently, with no UAC prompt.
//   * Otherwise the user's MwbLite downloads Setup to its temp folder and starts it; Setup shows its own window and
//     asks for the usual UAC approval. Either way it stops and upgrades MwbLite in place.
// - Automatic: with installUpdatesAutomatically on, the weekly check starts the task when it finds a newer release.
//   LiteAutoUpdate adds updating together with the other PCs.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MouseWithoutBorders.Lite
{
    internal sealed record ReleaseInfo(
        Version Version,
        string Tag,
        string Name,
        string Notes,
        string PageUrl,
        DateTime PublishedUtc,
        string SetupName,
        string SetupUrl,
        long SetupSize,
        string SetupSha256,
        string ChecksumsUrl);

    internal static class LiteUpdate
    {
        internal const string Repo = "nozinoz/MwbLite";
        internal static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);

        internal static string RepoUrl => "https://github.com/" + Repo;

        internal static string ReleasesUrl => RepoUrl + "/releases";

        internal static string IssuesUrl => RepoUrl + "/issues";

        private static string LatestApi => "https://api.github.com/repos/" + Repo + "/releases/latest";

        /// <summary>This build's version (e.g. 0.8.0).</summary>
        internal static Version Current { get; } = ParseVersion(typeof(LiteUpdate).Assembly.GetName().Version?.ToString()) ?? new Version(0, 0, 0);

        internal static string CurrentText => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";

        /// <summary>The git commit this build came from (short hash), or "".</summary>
        internal static string Commit
        {
            get
            {
                string info = typeof(LiteUpdate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
                int plus = info.IndexOf('+');
                string sha = plus >= 0 ? info[(plus + 1)..] : string.Empty;
                return sha.Length > 9 ? sha[..9] : sha;
            }
        }

        /// <summary>"v0.8.0", "0.8", "MwbLite 1.2.3" → a Version (major.minor.build), or null.</summary>
        internal static Version ParseVersion(string text)
        {
            string digits = new string((text ?? string.Empty).SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            if (!Version.TryParse(digits.Contains('.') ? digits : digits + ".0", out Version v))
            {
                return null;
            }

            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        internal static bool IsNewer(ReleaseInfo r) => r != null && r.Version > Current;

        /// <summary>Reads GitHub's "latest release" JSON.</summary>
        internal static ReleaseInfo ParseLatest(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string tag = Str(root, "tag_name");
            Version version = ParseVersion(tag);
            if (version == null)
            {
                return null;
            }

            string setupName = null, setupUrl = null, sha = null, sums = null;
            long size = 0;
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in assets.EnumerateArray())
                {
                    string name = Str(a, "name");
                    if (name.StartsWith("MwbLite-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        setupName = name;
                        setupUrl = Str(a, "browser_download_url");
                        size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long n) ? n : 0;
                        string digest = Str(a, "digest");
                        sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                    }
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sums = Str(a, "browser_download_url");
                    }
                }
            }

            DateTime published = DateTime.TryParse(Str(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime p) ? p : DateTime.MinValue;
            return new ReleaseInfo(version, tag, Str(root, "name"), Str(root, "body"), Str(root, "html_url"), published, setupName, setupUrl, size, sha, sums);
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static HttpClient NewClient(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MwbLite/" + CurrentText);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        /// <summary>The latest release, or null if the project has none yet.</summary>
        internal static async Task<ReleaseInfo> GetLatestAsync(CancellationToken ct)
        {
            using HttpClient http = NewClient(TimeSpan.FromSeconds(20));
            using HttpResponseMessage response = await http.GetAsync(LatestApi, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            ReleaseInfo r = ParseLatest(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            RecordCheck(r);
            return r;
        }

        /// <summary>Downloads the release's Setup to the user's temp folder, checks its SHA-256, and returns its path.</summary>
        internal static async Task<string> DownloadSetupAsync(ReleaseInfo r, IProgress<double> progress, CancellationToken ct) =>
            (await DownloadVerifiedAsync(r, progress, Path.Combine(Path.GetTempPath(), "MwbLite-update"), ct).ConfigureAwait(false)).Path;

        /// <summary>A downloaded Setup and the SHA-256 (lower-case hex) it was verified against.</summary>
        internal sealed record VerifiedSetup(string Path, string Sha256);

        /// <summary>Downloads the release's Setup into <paramref name="dir"/> and checks its SHA-256.</summary>
        internal static async Task<VerifiedSetup> DownloadVerifiedAsync(ReleaseInfo r, IProgress<double> progress, string dir, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(r.SetupUrl) || !r.SetupUrl.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This release has no MwbLite Setup to download.");
            }

            using HttpClient http = NewClient(TimeSpan.FromMinutes(10));
            string expected = r.SetupSha256;
            if (expected == null && !string.IsNullOrEmpty(r.ChecksumsUrl))
            {
                string sums = await http.GetStringAsync(r.ChecksumsUrl, ct).ConfigureAwait(false);
                expected = ShaFromSums(sums, r.SetupName);
            }

            if (expected == null)
            {
                throw new InvalidOperationException("The release has no checksum for its Setup, so it can't be verified.");
            }

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Path.GetFileName(r.SetupName));
            using (HttpResponseMessage response = await http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? r.SetupSize;
                using Stream src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0)
                    {
                        progress?.Report((double)done / total);
                    }
                }
            }

            string actual;
            using (FileStream f = File.OpenRead(path))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false)).ToLowerInvariant();
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("The downloaded Setup doesn't match its published SHA-256, so it was deleted. Try again later.");
            }

            LiteLog.Write(nameof(LiteUpdate), $"Downloaded {r.SetupName} ({actual}).");
            return new VerifiedSetup(path, actual.ToLowerInvariant());
        }

        /// <summary>SHA-256 of a stream as lower-case hex.</summary>
        internal static string Sha256Hex(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

        /// <summary>The hash for a file in a SHA256SUMS.txt ("&lt;hash&gt;  &lt;name&gt;" lines), or null.</summary>
        internal static string ShaFromSums(string sums, string fileName)
        {
            foreach (string line in (sums ?? string.Empty).Split('\n'))
            {
                string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 64 && Path.GetFileName(parts[^1].Replace('\\', '/')).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[0].ToLowerInvariant();
                }
            }

            return null;
        }

        internal static void RunSetup(string path)
        {
            LiteLog.Write(nameof(LiteUpdate), "Starting " + path);
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }

        // ---- updating without a UAC prompt (the "MwbLite Update" task) ------------------------------------------

        /// <summary>True if the update task exists, so <see cref="TryStartElevatedUpdate"/> can work.</summary>
        internal static bool CanUpdateWithoutPrompt() => LiteUpdateTask.Exists();

        /// <summary>
        /// Starts the update task: it installs the newest release as SYSTEM and restarts MwbLite, without elevation
        /// or a prompt. False if there is no task (not installed with Setup) or it can't be started.
        /// </summary>
        internal static bool TryStartElevatedUpdate() => LiteUpdateTask.TryRun();

        // ---- weekly background check (engine process) -----------------------------------------------------

        internal sealed class CheckState
        {
            public DateTime LastCheckUtc { get; set; }

            public string LatestVersion { get; set; } = string.Empty;

            public string NotifiedVersion { get; set; } = string.Empty;
        }

        private static string StateFile => Path.Combine(LitePaths.ConfigDir, "update.json");

        private static int checking;

        internal static CheckState ReadState()
        {
            try
            {
                return JsonSerializer.Deserialize<CheckState>(File.ReadAllText(StateFile), LiteConfig.JsonOptions) ?? new CheckState();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return new CheckState();
            }
        }

        private static void WriteState(CheckState s)
        {
            try
            {
                Directory.CreateDirectory(LitePaths.ConfigDir);
                File.WriteAllText(StateFile, JsonSerializer.Serialize(s, LiteConfig.JsonOptions));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteUpdate), "Could not save " + StateFile + ": " + e.Message);
            }
        }

        private static void RecordCheck(ReleaseInfo r)
        {
            CheckState s = ReadState();
            s.LastCheckUtc = DateTime.UtcNow;
            s.LatestVersion = r == null ? string.Empty : r.Version.ToString(3);
            WriteState(s);
        }

        /// <summary>
        /// Called from the tray timer: once a week (if checkForUpdates is on) asks GitHub for the latest release. If a
        /// newer one is out and <paramref name="autoInstall"/> is on and the update task starts, calls
        /// <paramref name="installing"/>; otherwise, the first time that release is seen, calls <paramref name="notify"/>.
        /// </summary>
        internal static void MaybeCheckInBackground(bool enabled, bool autoInstall, Action<ReleaseInfo> notify, Action<ReleaseInfo> installing)
        {
            if (!enabled || DateTime.UtcNow - ReadState().LastCheckUtc < CheckInterval || Interlocked.Exchange(ref checking, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    ReleaseInfo r = await GetLatestAsync(CancellationToken.None).ConfigureAwait(false);
                    CheckState s = ReadState();
                    if (LiteAutoUpdate.ShouldInstallAutomatically(autoInstall, r, Current) && TryStartElevatedUpdate())
                    {
                        s.NotifiedVersion = r.Version.ToString(3);
                        WriteState(s);
                        LiteLog.Write(nameof(LiteUpdate), $"Installing {r.Version.ToString(3)} automatically.");
                        installing(r);
                    }
                    else if (IsNewer(r) && s.NotifiedVersion != r.Version.ToString(3))
                    {
                        s.NotifiedVersion = r.Version.ToString(3);
                        WriteState(s);
                        notify(r);
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
                {
                    // Offline or GitHub unreachable: try again next week (the timestamp is only written on success),
                    // but not for another day.
                    CheckState s = ReadState();
                    s.LastCheckUtc = DateTime.UtcNow - CheckInterval + TimeSpan.FromDays(1);
                    WriteState(s);
                    LiteLog.Write(nameof(LiteUpdate), "Update check failed: " + e.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref checking, 0);
                }
            });
        }
    }
}
