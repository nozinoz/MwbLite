// MwbLite: updating by itself, and together with the other PCs. The decisions are small pure functions (tested in
// UpdateTests); this file only wires them to GitHub, the update task and the control channel (LiteControl).
//
// Automatic updates (installUpdatesAutomatically): LiteUpdate's weekly check starts the update task when it finds a
// newer release; ShouldInstallAutomatically is its rule.
//
// Update together (updateWithOtherPcs, on by default). PCs must run the same version to connect, so:
// - When this PC says hello to a connected PC that runs an older MwbLite, it sends "update-available" with its own
//   version (OnPeerHello), once per PC and version per hour.
// - When this PC receives "update-available" (OnUpdateAvailable) for a version newer than its own, it does NOT trust
//   the sender for anything but the version number: it asks GitHub itself whether a release at least that new is
//   published, and only then starts the normal update (the same task, downloading the newest release from GitHub and
//   checking it against GitHub's SHA-256). Nothing the other PC names is ever downloaded or run. Without the update
//   task (not installed with Setup) it only shows a notification that points at Settings > About.
// Both sides are rate limited. The sender spends its hourly slot for a PC only when that PC answered (it retries after
// a few minutes otherwise). The receiver acts on one announcement per hour, whichever PC or version it names, and
// doesn't act again on a version whose update it already tried for a day, so a PC (or a bug) can't make it download,
// prompt or start the update task over and over.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal enum PeerUpdateAction
    {
        /// <summary>Do nothing (off, not newer, or GitHub doesn't confirm it).</summary>
        Ignore,

        /// <summary>Start the update task.</summary>
        UpdateNow,

        /// <summary>Can't update without a prompt: tell the user, who can update from Settings > About.</summary>
        TellUser,
    }

    internal static class LiteAutoUpdate
    {
        /// <summary>The control-channel request one PC sends another: {"version": "x.y.z", "from": "<PC name>"}.</summary>
        internal const string UpdateAvailableOp = "update-available";

        private static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);
        private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FailedWindow = TimeSpan.FromHours(24);
        private const string AnyAnnouncement = "update-available";

        private static readonly OncePerWindow Received = new(RateWindow);       // one accepted announcement per hour, from anyone
        private static readonly OncePerWindow TriedVersions = new(FailedWindow); // versions we started (or failed to start) an update to
        private static readonly OncePerWindow Sent = new(RateWindow);           // PCs (and version) that have answered our announcement
        private static readonly OncePerWindow SentAttempts = new(RetryWindow);  // and when we last tried, answered or not

        // ---- decisions (pure) --------------------------------------------------------------------------------

        /// <summary>True if <paramref name="candidate"/> is a version newer than <paramref name="current"/>.</summary>
        internal static bool IsNewerThan(Version candidate, Version current) => candidate != null && current != null && candidate > current;

        /// <summary>The weekly check found <paramref name="release"/>: install it by itself?</summary>
        internal static bool ShouldInstallAutomatically(bool enabled, ReleaseInfo release, Version current) =>
            enabled && release != null && !string.IsNullOrEmpty(release.SetupUrl) && IsNewerThan(release.Version, current);

        /// <summary>This PC is connected to one running <paramref name="peer"/>: tell it to update?</summary>
        internal static bool ShouldAnnounceToPeer(bool enabled, Version ours, Version peer) => enabled && peer != null && IsNewerThan(ours, peer);

        /// <summary>
        /// What to do about another PC announcing <paramref name="announced"/>, given the newest release GitHub lists
        /// (<paramref name="latestPublished"/>, null if unknown). Only a version that is newer than ours and no newer
        /// than what GitHub publishes is acted on, so a PC can't make this one chase a version that isn't released.
        /// </summary>
        internal static PeerUpdateAction DecideOnAnnouncement(bool enabled, Version announced, Version current, Version latestPublished, bool updateTaskAvailable)
        {
            if (!enabled || !IsNewerThan(announced, current) || latestPublished == null || latestPublished < announced)
            {
                return PeerUpdateAction.Ignore;
            }

            return updateTaskAvailable ? PeerUpdateAction.UpdateNow : PeerUpdateAction.TellUser;
        }

        /// <summary>
        /// Should this PC send its announcement to <paramref name="key"/> (peer and version) now? Not if that PC already
        /// answered within the hour (<paramref name="answered"/>), and not if we tried within the last few minutes
        /// (<paramref name="attempts"/>); a PC that doesn't answer is tried again then, not an hour later.
        /// </summary>
        internal static bool ShouldSendAnnouncement(OncePerWindow answered, OncePerWindow attempts, string key, DateTime nowUtc) =>
            !answered.IsHeld(key, nowUtc) && attempts.TryAcquire(key, nowUtc);

        /// <summary>
        /// Should an incoming announcement of <paramref name="announced"/> be acted on? Only a newer version, not one we
        /// already tried to update to (<paramref name="tried"/>), and only one announcement per window from anybody
        /// (<paramref name="received"/>). The window's slot is used up only by an announcement that passes the other checks.
        /// </summary>
        internal static bool ShouldAcceptAnnouncement(bool enabled, Version announced, Version current, OncePerWindow received, OncePerWindow tried, DateTime nowUtc) =>
            enabled && IsNewerThan(announced, current) && !tried.IsHeld(announced.ToString(3), nowUtc) && received.TryAcquire(AnyAnnouncement, nowUtc);

        /// <summary>Allows each key once per window (an hour for update announcements).</summary>
        internal sealed class OncePerWindow
        {
            private const int PruneAbove = 64;
            private readonly TimeSpan window;
            private readonly Dictionary<string, DateTime> last = new(StringComparer.OrdinalIgnoreCase);
            private readonly object gate = new();

            internal OncePerWindow(TimeSpan window) => this.window = window;

            /// <summary>
            /// True (and remembered) if <paramref name="key"/> wasn't allowed within the window before
            /// <paramref name="nowUtc"/>. A clock set back counts as a new start rather than blocking until it catches up.
            /// </summary>
            internal bool TryAcquire(string key, DateTime nowUtc)
            {
                lock (gate)
                {
                    if (IsHeldLocked(key, nowUtc))
                    {
                        return false;
                    }

                    RecordLocked(key, nowUtc);
                    return true;
                }
            }

            /// <summary>True if <paramref name="key"/> was acquired or recorded within the window before <paramref name="nowUtc"/>.</summary>
            internal bool IsHeld(string key, DateTime nowUtc)
            {
                lock (gate)
                {
                    return IsHeldLocked(key, nowUtc);
                }
            }

            /// <summary>Starts the window for <paramref name="key"/> without checking it (for something that is only known afterwards).</summary>
            internal void Record(string key, DateTime nowUtc)
            {
                lock (gate)
                {
                    RecordLocked(key, nowUtc);
                }
            }

            private bool IsHeldLocked(string key, DateTime nowUtc) => last.TryGetValue(key, out DateTime t) && nowUtc >= t && nowUtc - t < window;

            private void RecordLocked(string key, DateTime nowUtc)
            {
                last[key] = nowUtc;
                if (last.Count > PruneAbove)
                {
                    foreach (string old in last.Where(kv => nowUtc - kv.Value >= window).Select(kv => kv.Key).ToList())
                    {
                        _ = last.Remove(old);
                    }
                }
            }
        }

        // ---- wiring (engine) ---------------------------------------------------------------------------------

        /// <summary>Called once at engine startup: answers "update-available" and announces our version to older PCs.</summary>
        internal static void Start()
        {
            if (Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop)
            {
                return; // the lock-screen instance shows no UI and must not start updates
            }

            try
            {
                LiteControl.RegisterHandler(UpdateAvailableOp, OnUpdateAvailable);
                LiteControl.PeerHello += OnPeerHello;
            }
            catch (Exception e)
            {
                // The control channel is optional; without it, PCs just don't update each other.
                LiteLog.Write(nameof(LiteAutoUpdate), "Update together is not available: " + e.Message);
            }
        }

        // A connected PC answered "hello". If it runs an older MwbLite, tell it ours.
        private static void OnPeerHello(string peer, PeerInfo info)
        {
            Version theirs = LiteUpdate.ParseVersion(info?.Version);
            if (string.IsNullOrEmpty(peer) || !ShouldAnnounceToPeer(Setting.Values.UpdateWithOtherPcs, LiteUpdate.Current, theirs))
            {
                return;
            }

            // Told within the hour: done. Otherwise try, but not more often than every few minutes while it doesn't answer.
            string key = peer + "|" + LiteUpdate.CurrentText;
            if (!ShouldSendAnnouncement(Sent, SentAttempts, key, DateTime.UtcNow))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var args = new JsonObject { ["version"] = LiteUpdate.CurrentText, ["from"] = Common.MachineName };
                    JsonObject reply = await LiteControl.SendAsync(peer, UpdateAvailableOp, args, cts.Token).ConfigureAwait(false);
                    if (reply != null)
                    {
                        Sent.Record(key, DateTime.UtcNow); // it answered (even with "not accepted"), so it isn't asked again for an hour
                    }

                    LiteLog.Write(nameof(LiteAutoUpdate), $"Told {peer} (MwbLite {theirs.ToString(3)}) that {LiteUpdate.CurrentText} is available: " + (reply == null ? "no answer" : reply.ToJsonString()));
                }
                catch (Exception e)
                {
                    LiteLog.Write(nameof(LiteAutoUpdate), $"Could not tell {peer} about {LiteUpdate.CurrentText}: {e.Message}");
                }
            });
        }

        // Another PC says a newer version exists. Returns at once; the GitHub check and the update run in the background.
        private static JsonObject OnUpdateAvailable(JsonObject request)
        {
            Version announced = LiteUpdate.ParseVersion(Text(request, "version"));
            if (!ShouldAcceptAnnouncement(Setting.Values.UpdateWithOtherPcs, announced, LiteUpdate.Current, Received, TriedVersions, DateTime.UtcNow))
            {
                return new JsonObject { ["accepted"] = false };
            }

            string from = CleanName(Text(request, "from"));
            LiteLog.Write(nameof(LiteAutoUpdate), $"{from} announced MwbLite {announced.ToString(3)} (this PC runs {LiteUpdate.CurrentText}).");
            _ = Task.Run(() => ActOnAnnouncementAsync(announced, from));
            return new JsonObject { ["accepted"] = true };
        }

        private static async Task ActOnAnnouncementAsync(Version announced, string from)
        {
            try
            {
                // Don't take the other PC's word for it: ask GitHub what is published.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                ReleaseInfo latest = await LiteUpdate.GetLatestAsync(cts.Token).ConfigureAwait(false);
                bool task = LiteUpdate.CanUpdateWithoutPrompt();
                PeerUpdateAction action = DecideOnAnnouncement(Setting.Values.UpdateWithOtherPcs, announced, LiteUpdate.Current, latest?.Version, task);
                LiteLog.Write(nameof(LiteAutoUpdate), $"GitHub's latest is {latest?.Version.ToString(3) ?? "unknown"}; {action}.");

                if (action != PeerUpdateAction.Ignore)
                {
                    // Whatever happens next counts as the attempt for this version: a successful update replaces this
                    // process, so if it is still here a day later the update failed, and peers don't get to ask again.
                    TriedVersions.Record(announced.ToString(3), DateTime.UtcNow);
                }

                if (action == PeerUpdateAction.UpdateNow && LiteUpdate.TryStartElevatedUpdate())
                {
                    LiteTray.NotifyUpdate($"Updating MwbLite to {latest.Version.ToString(3)}… ({from} already runs it.)");
                }
                else if (action != PeerUpdateAction.Ignore)
                {
                    LiteTray.NotifyUpdate($"{from} runs MwbLite {announced.ToString(3)}. Open Settings > About to update.");
                }
            }
            catch (Exception e)
            {
                TriedVersions.Record(announced.ToString(3), DateTime.UtcNow);
                LiteLog.Write(nameof(LiteAutoUpdate), "Could not act on the update announcement: " + e.Message);
            }
        }

        private static string Text(JsonObject o, string name) =>
            o != null && o.TryGetPropertyValue(name, out JsonNode n) && n is JsonValue v && v.TryGetValue(out string s) ? s : null;

        /// <summary>A PC name safe to show in a notification: no control characters, at most 40 characters.</summary>
        internal static string CleanName(string name)
        {
            string clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (clean.Length > 40)
            {
                clean = clean[..40];
            }

            return clean.Length == 0 ? "Another PC" : clean;
        }
    }
}
