// MwbLite: lock and sleep, for this PC and for all PCs (tray menu "Lock all PCs" / "Sleep all PCs…").
//
// Other PCs are asked over the control channel (LiteControl ops "lock" and "sleep"); this file is both what those
// ops run on the receiving PC and the sender's side of "all". Locking needs the interactive user's session: a normal
// instance calls LockWorkStation itself; the SYSTEM instance of service mode (which has no interactive session of its
// own, so LockWorkStation from it is unreliable) starts "rundll32 user32.dll,LockWorkStation" as the signed-in user
// with the existing Launch helper. Sleep replies first and suspends about a second later, so the asker gets its answer.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal static class LitePower
    {
        private static readonly TimeSpan SleepDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(8);

        [DllImport("powrprof.dll", ExactSpelling = true)]
        private static extern byte SetSuspendState(byte hibernate, byte forceCritical, byte disableWakeEvent);

        /// <summary>Locks the signed-in user's session. False if it couldn't be started.</summary>
        internal static bool LockThisPc()
        {
            try
            {
                if (Common.RunOnLogonDesktop)
                {
                    return true; // already at the sign-in or lock screen
                }

                if (!LitePaths.IsSystem)
                {
                    return NativeMethods.LockWorkStation() != 0;
                }

                string rundll = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
                return Launch.CreateProcessInInputDesktopSession("\"" + rundll + "\"", "user32.dll,LockWorkStation", "default", 0) > 0;
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LitePower), "Could not lock this PC: " + e.Message);
                return false;
            }
        }

        /// <summary>Puts this PC to sleep after <paramref name="delay"/> (default about a second), without blocking the caller.</summary>
        internal static void SleepThisPcSoon(TimeSpan? delay = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay ?? SleepDelay).ConfigureAwait(false);
                    LiteLog.Write(nameof(LitePower), "Going to sleep.");
                    _ = SetSuspendState(0, 0, 0); // sleep, not hibernate; returns after the PC resumes
                }
                catch (Exception e)
                {
                    LiteLog.Write(nameof(LitePower), "Could not sleep: " + e.Message);
                }
            });
        }

        /// <summary>The control-channel handlers for "lock" and "sleep".</summary>
        internal static void RegisterHandlers()
        {
            LiteControl.RegisterHandler("lock", _ =>
            {
                LiteLog.Write(nameof(LitePower), "Another PC asked this one to lock.");
                return LockThisPc() ? null : throw new InvalidOperationException("could not lock");
            });
            LiteControl.RegisterHandler("sleep", _ =>
            {
                LiteLog.Write(nameof(LitePower), "Another PC asked this one to sleep.");
                SleepThisPcSoon();
                return null;
            });
        }

        /// <summary>Names of the PCs in the layout that are connected right now (not this one).</summary>
        internal static string[] ConnectedPcs() =>
            LiteStatus.Current.Pcs.Where(p => p.State == PcState.Connected).Select(p => p.Name).ToArray();

        /// <summary>Asks each connected PC to lock, waits for their answers, then locks this PC. Returns how many others said yes.</summary>
        internal static async Task<int> LockAllAsync()
        {
            int n = await AskAllAsync("lock").ConfigureAwait(false);
            _ = LockThisPc();
            return n;
        }

        /// <summary>Asks each connected PC to sleep, waits for their answers, then puts this PC to sleep. Returns how many others said yes.</summary>
        internal static async Task<int> SleepAllAsync()
        {
            int n = await AskAllAsync("sleep").ConfigureAwait(false);
            SleepThisPcSoon(TimeSpan.Zero);
            return n;
        }

        private static async Task<int> AskAllAsync(string op)
        {
            string[] pcs = ConnectedPcs();
            using var cts = new CancellationTokenSource(AskTimeout);
            JsonObject[] replies = await Task.WhenAll(pcs.Select(pc => LiteControl.SendAsync(pc, op, null, cts.Token))).ConfigureAwait(false);
            int ok = replies.Count(r => r != null && (r["ok"]?.GetValue<bool>() ?? false));
            LiteLog.Write(nameof(LitePower), $"Asked {pcs.Length} PC(s) to {op}: {ok} agreed.");
            return ok;
        }
    }
}
