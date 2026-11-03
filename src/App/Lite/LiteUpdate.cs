// MwbLite: new-version check and update, from the project's public GitHub Releases.
//
// - Check: one HTTPS GET of https://api.github.com/repos/<repo>/releases/latest (nothing about the PC or user is
//   sent beyond a "MwbLite/<version>" user agent). Weekly in the background (checkForUpdates, default on), and on
//   demand from Settings > About.
// - Signed releases: an update is installed only if the release carries SHA256SUMS.txt and SHA256SUMS.txt.sig and the
//   signature verifies with the release key built into this program (LiteReleaseKey). Both files and Setup must come
//   from https://github.com/<repo>/releases/download/. The SHA-256 Setup must have is taken from that verified list,
//   never from the digest GitHub's API reports, and there is no fallback: a release without a good signature is not
//   installed. The weekly check may still show that such a release exists (CheckSignatureAsync says if it is signed).
// - Update: downloads the release's MwbLite-Setup-<version>.exe over HTTPS, checks its SHA-256 against the verified
//   list, then installs it. Two ways:
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
using System.Text;
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
        string ChecksumsUrl,
        string SignatureUrl);

    /// <summary>The release isn't signed with MwbLite's release key (no signature, a bad one, or files from the wrong place).</summary>
    internal sealed class ReleaseNotSignedException : IOException
    {
        internal const string Text = "This release isn't signed with MwbLite's release key; not installing it.";

        internal ReleaseNotSignedException()
            : base(Text)
        {
        }
    }

    internal static class LiteUpdate
    {
        internal const string Repo = "nozinoz/MwbLite";
        internal static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);

        /// <summary>Everything an update downloads (Setup, the checksum list, its signature) must come from here.</summary>
        internal const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

        // The checksum list lists two files and the signature is 88 characters; anything bigger is not what we asked for.
        private const int MaxChecksumsBytes = 256 * 1024;
        private const int MaxSignatureBytes = 4 * 1024;

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

            string setupName = null, setupUrl = null, sums = null, sig = null;
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
                    }
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sums = Str(a, "browser_download_url");
                    }
                    else if (name.Equals("SHA256SUMS.txt.sig", StringComparison.OrdinalIgnoreCase))
                    {
                        sig = Str(a, "browser_download_url");
                    }
                }
            }

            DateTime published = DateTime.TryParse(Str(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime p) ? p : DateTime.MinValue;
            return new ReleaseInfo(version, tag, Str(root, "name"), Str(root, "body"), Str(root, "html_url"), published, setupName, setupUrl, size, sums, sig);
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static HttpClient NewClient(TimeSpan timeout, HttpMessageHandler handler = null)
        {
            var http = handler == null ? new HttpClient { Timeout = timeout } : new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
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

        /// <summary>
        /// True if <paramref name="url"/> is a GitHub release download of this project: https, github.com, no user info
        /// or odd port, and a path (with any "." and ".." resolved) under /<repo>/releases/download/.
        /// </summary>
        internal static bool IsPinnedUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(url, UriKind.Absolute, out Uri u))
            {
                return false;
            }

            return u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com" && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo)
                && u.AbsolutePath.StartsWith(new Uri(DownloadPrefix).AbsolutePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Downloads the release's Setup to the user's temp folder, checks it against the signed checksums, and returns its path.</summary>
        internal static async Task<string> DownloadSetupAsync(ReleaseInfo r, IProgress<double> progress, CancellationToken ct) =>
            (await DownloadVerifiedAsync(r, progress, Path.Combine(Path.GetTempPath(), "MwbLite-update"), ct).ConfigureAwait(false)).Path;

        /// <summary>A downloaded Setup and the SHA-256 (lower-case hex) it was verified against.</summary>
        internal sealed record VerifiedSetup(string Path, string Sha256);

        /// <summary>
        /// Downloads the release's checksum list and its signature, checks the signature with the release key and
        /// returns the SHA-256 the signed list gives for Setup (lower-case hex). Throws ReleaseNotSignedException if
        /// the release has no good signature, InvalidDataException if the signed list has no entry for Setup, and
        /// HttpRequestException if a file can't be downloaded.
        /// </summary>
        internal static async Task<string> GetSignedSetupSha256Async(HttpClient http, ReleaseInfo r, CancellationToken ct)
        {
            if (!IsPinnedUrl(r.ChecksumsUrl) || !IsPinnedUrl(r.SignatureUrl))
            {
                throw NotSigned("the release has no SHA256SUMS.txt and SHA256SUMS.txt.sig from " + DownloadPrefix);
            }

            byte[] sums = await DownloadSmallAsync(http, r.ChecksumsUrl, MaxChecksumsBytes, ct).ConfigureAwait(false);
            string sig = Encoding.ASCII.GetString(await DownloadSmallAsync(http, r.SignatureUrl, MaxSignatureBytes, ct).ConfigureAwait(false));
            return SignedSha256(sums, sig, r.SetupName);
        }

        /// <summary>The SHA-256 the checksum list gives for <paramref name="fileName"/>, once its signature is verified (see above).</summary>
        internal static string SignedSha256(byte[] sums, string sigBase64, string fileName)
        {
            if (!LiteReleaseKey.Verify(sums, sigBase64))
            {
                throw NotSigned(LiteReleaseKey.IsConfigured ? "the signature of SHA256SUMS.txt doesn't verify" : "this MwbLite has no release key built in");
            }

            return ShaFromSums(Encoding.UTF8.GetString(sums), fileName)
                ?? throw new InvalidDataException("The release's signed checksum list has no entry for " + fileName + ", so it can't be verified.");
        }

        private static ReleaseNotSignedException NotSigned(string why)
        {
            LiteLog.Write(nameof(LiteUpdate), "Refusing the release: " + why + ".");
            return new ReleaseNotSignedException();
        }

        private static async Task<byte[]> DownloadSmallAsync(HttpClient http, string url, int maxBytes, CancellationToken ct)
        {
            using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using Stream src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            int n;
            while ((n = await src.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > maxBytes)
                {
                    throw new InvalidDataException("A release file is bigger than expected, so it was not used.");
                }

                buffer.Write(chunk, 0, n);
            }

            return buffer.ToArray();
        }

        /// <summary>
        /// Null if <paramref name="r"/> is signed with the release key and can be installed; otherwise a message saying
        /// why not. Downloads only the two small files. Network errors are thrown, not reported as "not signed".
        /// </summary>
        internal static async Task<string> CheckSignatureAsync(ReleaseInfo r, CancellationToken ct, HttpMessageHandler handler = null)
        {
            using HttpClient http = NewClient(TimeSpan.FromSeconds(30), handler);
            try
            {
                _ = await GetSignedSetupSha256Async(http, r, ct).ConfigureAwait(false);
                return null;
            }
            catch (Exception e) when (e is ReleaseNotSignedException or InvalidDataException)
            {
                return e.Message;
            }
        }

        /// <summary>
        /// Downloads the release's Setup into <paramref name="dir"/> and checks its SHA-256 against the release's signed
        /// checksum list. Throws ReleaseNotSignedException, without downloading Setup, if the release isn't signed.
        /// </summary>
        internal static async Task<VerifiedSetup> DownloadVerifiedAsync(ReleaseInfo r, IProgress<double> progress, string dir, CancellationToken ct, HttpMessageHandler handler = null)
        {
            if (!IsPinnedUrl(r.SetupUrl))
            {
                throw new InvalidOperationException("This release has no MwbLite Setup to download.");
            }

            using HttpClient http = NewClient(TimeSpan.FromMinutes(10), handler);
            string expected = await GetSignedSetupSha256Async(http, r, ct).ConfigureAwait(false);

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
                throw new InvalidDataException("The downloaded Setup doesn't match its signed SHA-256, so it was deleted. Try again later.");
            }

            LiteLog.Write(nameof(LiteUpdate), $"Downloaded {r.SetupName} ({actual}); the checksum list is signed with the release key.");
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
        /// newer one is out, signed with the release key, and <paramref name="autoInstall"/> is on and the update task
        /// starts, calls <paramref name="installing"/>; otherwise, the first time that release is seen, calls
        /// <paramref name="notify"/> (its second argument says whether the release is signed, i.e. can be installed).
        /// </summary>
        internal static void MaybeCheckInBackground(bool enabled, bool autoInstall, Action<ReleaseInfo, bool> notify, Action<ReleaseInfo> installing)
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
                    bool newer = IsNewer(r);
                    string unsigned = null;
                    if (newer)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        unsigned = await CheckSignatureAsync(r, cts.Token).ConfigureAwait(false);
                        if (unsigned != null)
                        {
                            LiteLog.Write(nameof(LiteUpdate), $"{r.Version.ToString(3)} is out but can't be installed: {unsigned}");
                        }
                    }

                    if (unsigned == null && LiteAutoUpdate.ShouldInstallAutomatically(autoInstall, r, Current) && TryStartElevatedUpdate())
                    {
                        s.NotifiedVersion = r.Version.ToString(3);
                        WriteState(s);
                        LiteLog.Write(nameof(LiteUpdate), $"Installing {r.Version.ToString(3)} automatically.");
                        installing(r);
                    }
                    else if (newer && s.NotifiedVersion != r.Version.ToString(3))
                    {
                        s.NotifiedVersion = r.Version.ToString(3);
                        WriteState(s);
                        notify(r, unsigned == null);
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidDataException)
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
