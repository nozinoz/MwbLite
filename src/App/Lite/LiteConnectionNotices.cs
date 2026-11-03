// MwbLite: decides when to tell the user that a PC dropped off or came back ("LAPTOP disconnected", "LAPTOP is back").
//
// Pure logic with the clock passed in, so it is tested with fake times (LiteTray feeds it the live status once a
// second and shows what it returns as balloons). The rules:
//  - a PC that was connected and then stays not connected for 20 s is reported once as disconnected (a short
//    drop that reconnects by itself, e.g. a Wi-Fi blip or a redial, says nothing);
//  - when it reconnects after such a notice, it is reported once as back;
//  - nothing for the first 2 minutes after MwbLite starts or this PC suspends or resumes (everything is
//    reconnecting then), and a PC that was never connected in this stretch (for instance one that is switched off)
//    is never reported;
//  - the Settings switch (notifyConnectionChanges) turns the notices off without losing track of who is connected.
using System;
using System.Collections.Generic;
using System.Linq;

namespace MouseWithoutBorders.Lite
{
    internal sealed class LiteConnectionNotices
    {
        internal static readonly TimeSpan DropAfter = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan QuietAfterStartOrResume = TimeSpan.FromMinutes(2);

        private sealed class Track
        {
            internal bool WasConnected;
            internal DateTime? NotConnectedSince;
            internal bool DropReported;
        }

        private readonly object gate = new();
        private readonly Dictionary<string, Track> tracks = new(StringComparer.OrdinalIgnoreCase);
        private DateTime quietUntil;

        internal LiteConnectionNotices(DateTime startedUtc) => quietUntil = startedUtc + QuietAfterStartOrResume;

        /// <summary>
        /// This PC went to sleep or woke up: forget who was connected (every link is closed and reopened around
        /// sleep) and stay quiet for a while.
        /// </summary>
        internal void PowerChanged(DateTime nowUtc)
        {
            lock (gate)
            {
                tracks.Clear();
                quietUntil = nowUtc + QuietAfterStartOrResume;
            }
        }

        /// <summary>The balloon text for each change since the last call (usually none).</summary>
        internal List<string> Update(DateTime nowUtc, IEnumerable<PcStatus> pcs, bool enabled)
        {
            var dropped = new List<string>();
            var back = new List<string>();
            lock (gate)
            {
                bool quiet = nowUtc < quietUntil;
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (PcStatus pc in pcs.Where(p => p.State != PcState.ThisPc))
                {
                    _ = present.Add(pc.Name);
                    if (!tracks.TryGetValue(pc.Name, out Track t))
                    {
                        t = new Track();
                        tracks[pc.Name] = t;
                    }

                    if (pc.State == PcState.Connected)
                    {
                        if (t.DropReported && enabled && !quiet)
                        {
                            back.Add(pc.Name);
                        }

                        t.WasConnected = true;
                        t.NotConnectedSince = null;
                        t.DropReported = false;
                    }
                    else if (quiet)
                    {
                        // Drops while everything is still settling are forgotten, not reported later.
                        t.WasConnected = false;
                        t.NotConnectedSince = null;
                        t.DropReported = false;
                    }
                    else if (t.WasConnected)
                    {
                        t.NotConnectedSince ??= nowUtc;
                        if (!t.DropReported && nowUtc - t.NotConnectedSince.Value >= DropAfter)
                        {
                            t.DropReported = enabled; // switched off: say nothing now, and nothing when it returns
                            if (enabled)
                            {
                                dropped.Add(pc.Name);
                            }
                        }
                    }
                }

                foreach (string gone in tracks.Keys.Where(k => !present.Contains(k)).ToList())
                {
                    _ = tracks.Remove(gone); // taken out of the layout
                }
            }

            var messages = new List<string>();
            if (dropped.Count > 0)
            {
                messages.Add(Join(dropped) + " disconnected");
            }

            if (back.Count > 0)
            {
                messages.Add(back.Count == 1 ? back[0] + " is back" : Join(back) + " are back");
            }

            return messages;
        }

        private static string Join(List<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }
}
