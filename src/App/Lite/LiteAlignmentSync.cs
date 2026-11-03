// MwbLite: keeps the screen alignment (LiteEdgeAlignment) the same on every PC.
//
// In relative mouse mode (the default) the PC the cursor is on computes where it comes out on the next screen, so
// every PC needs the same entries. When settings.json is reloaded here with different entries, they are sent to each
// other PC in the layout over the control channel (LiteControl op "alignment"). A PC that receives them keeps them
// in memory for the running engine and writes them to its settings file, the way LiteLayoutSync persists a layout
// received from a peer: a SYSTEM (service-mode) instance writes its protected copy and asks a process running as
// the user to update the user's own settings.json (--set-alignment).
//
// A received set is never sent on: the handler only stores it, and a reload that finds the file already matches the
// memory changes nothing. The sets travel in one hop from the PC where you pressed Apply. A PC that is off at that
// moment keeps its old entries until you apply again.
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
    internal static class LiteAlignmentSync
    {
        internal const string Op = "alignment";

        private static readonly Lock Gate = new();
        private static string lastReceived; // Canonical() of the set most recently received

        /// <summary>Registers the "alignment" handler with the control channel (engine startup).</summary>
        internal static void Register()
        {
            try
            {
                LiteControl.RegisterHandler(Op, OnAlignment);
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteAlignmentSync), "Cannot register the alignment handler: " + e.Message);
            }
        }

        private static JsonObject OnAlignment(JsonObject request)
        {
            List<EdgeAlignmentEntry> entries = LiteEdgeAlignment.FromJson(request); // throws -> {"ok": false, "error": ...}

            lock (Gate)
            {
                lastReceived = LiteEdgeAlignment.Canonical(entries);
            }

            if (Setting.Values.SetEdgeAlignment(entries))
            {
                LiteLog.Write(nameof(LiteAlignmentSync), $"Received screen alignment ({entries.Count} pair(s)) from a peer.");
                _ = Task.Run(() => Persist(entries));
            }

            return null;
        }

        private static void Persist(List<EdgeAlignmentEntry> entries)
        {
            try
            {
                if (!TryPersist(Setting.Values.SettingsFile, entries))
                {
                    return;
                }

                if (LitePaths.IsSystem)
                {
                    // Keep the user's own settings.json (shown in Settings, pushed by --apply-config) in step.
                    _ = Launch.CreateProcessInInputDesktopSession(
                        "\"" + LitePaths.ExePath + "\"",
                        $"--set-alignment \"{LiteEdgeAlignment.Encode(entries)}\"",
                        "default",
                        0);
                }
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteAlignmentSync), "Cannot save the received alignment: " + e.Message);
            }
        }

        /// <summary>Writes the entries into a settings file if they differ. Returns true if the file changed.</summary>
        internal static bool TryPersist(string path, IEnumerable<EdgeAlignmentEntry> entries)
        {
            var cfg = LiteConfig.TryParse(path, out _);
            if (cfg == null)
            {
                return false; // never overwrite a file the user still has to fix
            }

            var updated = cfg.Clone();
            updated.Advanced.EdgeAlignment = entries.Select(e => e.Clone()).ToList();
            updated.Normalize();

            if (LiteEdgeAlignment.Canonical(cfg.Advanced.EdgeAlignment) == LiteEdgeAlignment.Canonical(updated.Advanced.EdgeAlignment) || updated.Validate(out _).Count > 0)
            {
                return false;
            }

            try
            {
                updated.Save(path);
                LiteLog.Write(nameof(LiteAlignmentSync), "Saved the screen alignment received from a peer.");
                return true;
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteAlignmentSync), "Cannot save the alignment: " + e.Message);
                return false;
            }
        }

        /// <summary>Called when settings.json was reloaded: sends the entries to the other PCs if they changed here.</summary>
        internal static void OnConfigReloaded(IEnumerable<EdgeAlignmentEntry> before, LiteConfig now)
        {
            try
            {
                string after = LiteEdgeAlignment.Canonical(now.Advanced.EdgeAlignment);
                if (after == LiteEdgeAlignment.Canonical(before))
                {
                    return;
                }

                string received;
                lock (Gate)
                {
                    received = lastReceived;
                    lastReceived = null;
                }

                // The default-desktop instance sends (the logon-desktop instances reload the same file); and a set that
                // just arrived from a peer is not sent back.
                if (Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop || after == received)
                {
                    return;
                }

                string me = LiteConfig.NormalizeName(Common.MachineName ?? Environment.MachineName);
                string[] peers = now.MatrixSlots().Where(n => n.Length > 0 && n != me).Distinct().ToArray();
                List<EdgeAlignmentEntry> entries = now.Advanced.EdgeAlignment.Select(e => e.Clone()).ToList();
                if (peers.Length > 0)
                {
                    _ = Task.Run(() => SendToAsync(peers, entries));
                }
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteAlignmentSync), "Cannot send the alignment: " + e.Message);
            }
        }

        private static async Task SendToAsync(string[] peers, List<EdgeAlignmentEntry> entries)
        {
            await Task.WhenAll(peers.Select(async peer =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    JsonObject reply = await LiteControl.SendAsync(peer, Op, LiteEdgeAlignment.ToJson(entries), cts.Token).ConfigureAwait(false);
                    LiteLog.Write(nameof(LiteAlignmentSync), reply == null
                        ? $"{peer} did not answer the screen alignment (offline, or MwbLite older than 0.9); it keeps its own until you apply again."
                        : reply["ok"]?.GetValue<bool>() == true
                            ? $"Sent the screen alignment to {peer}."
                            : $"{peer} refused the screen alignment: {reply["error"]}");
                }
                catch (Exception e)
                {
                    LiteLog.Write(nameof(LiteAlignmentSync), $"Cannot send the screen alignment to {peer}: {e.Message}");
                }
            })).ConfigureAwait(false);
        }
    }
}
