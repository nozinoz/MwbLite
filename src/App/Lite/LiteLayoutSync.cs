// MwbLite: keeps the machine layout consistent across PCs.
//
// 1. Pool seeding. When MWB meets a machine that is not in its "machine pool" it rebuilds the layout
//    from the pool's learn order and broadcasts it (MachineStuff.AddToMachinePool), which can scramble
//    the order chosen in Settings. Upstream's pairing avoided this by loading the configured names into
//    the pool (ConnectToMachine -> MachinePool.Initialize); MwbLite does the same at startup, on reload,
//    and when a layout arrives from a peer.
// 2. Persisting peer layouts. MWB syncs the layout between connected PCs (e.g. after "Add a PC" on one
//    of them). Upstream saved a received layout; MwbLite now writes it to the settings file this
//    instance reads, so it survives a restart. A SYSTEM (service-mode) instance writes its protected
//    copy and asks a process running as the user to update the user's own settings.json.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteLayoutSync
    {
        private static readonly Lock Gate = new();
        private static Timer persistTimer;

        /// <summary>Adds configured machine names the pool doesn't know yet (never call while holding McMatrixLock).</summary>
        internal static void SeedPool(IEnumerable<string> names)
        {
            try
            {
                bool learned = false;
                foreach (string name in names.Select(n => (n ?? string.Empty).Trim()).Where(n => n.Length > 0))
                {
                    if (!MachineStuff.MachinePool.TryFindMachineByName(name, out _))
                    {
                        learned |= MachineStuff.MachinePool.LearnMachine(name);
                    }
                }

                if (learned)
                {
                    MachineStuff.UpdateMachinePoolStringSetting();
                    Logger.Log("Machine pool seeded from the configured layout.");
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Logger.Log(e);
            }
        }

        /// <summary>Called when MWB changes the layout at runtime (normally a layout received from a peer).</summary>
        internal static void OnRuntimeLayoutChange(string[] names)
        {
            if (Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop)
            {
                return; // the default-desktop instance handles it
            }

            _ = Task.Run(() => SeedPool(names));

            lock (Gate)
            {
                persistTimer ??= new Timer(_ => PersistCurrent());
                _ = persistTimer.Change(2000, Timeout.Infinite); // debounce: one write per burst
            }
        }

        private static void PersistCurrent()
        {
            try
            {
                var (machines, oneRow, wrap) = Setting.Values.LayoutSnapshot();
                if (!TryPersist(Setting.Values.SettingsFile, machines, oneRow, wrap))
                {
                    return;
                }

                Logger.Log("Saved layout received from a peer: " + string.Join(",", machines));

                if (LitePaths.IsSystem)
                {
                    // Keep the user's own settings.json (shown in Settings, pushed by --apply-config) in step.
                    _ = Launch.CreateProcessInInputDesktopSession(
                        "\"" + LitePaths.ExePath + "\"",
                        $"--set-layout {string.Join(",", machines)} {oneRow} {wrap}",
                        "default",
                        0);
                }
            }
            catch (Exception e)
            {
                Logger.Log(e);
            }
        }

        /// <summary>Writes the layout into a settings file if it differs. Returns true if the file changed.</summary>
        internal static bool TryPersist(string path, IList<string> machines, bool oneRow, bool wrap)
        {
            var cfg = LiteConfig.TryParse(path, out _);
            if (cfg == null)
            {
                return false; // never overwrite a file the user still has to fix
            }

            var newList = machines.Select(LiteConfig.NormalizeName).ToList();
            while (newList.Count > 0 && newList[^1].Length == 0)
            {
                newList.RemoveAt(newList.Count - 1);
            }

            var updated = cfg.Clone();
            updated.Machines = newList;
            updated.Layout.OneRow = oneRow;
            updated.Layout.WrapMouse = wrap;
            updated.Normalize();

            if (cfg.MatrixSlots().SequenceEqual(updated.MatrixSlots()) && cfg.Layout.OneRow == oneRow && cfg.Layout.WrapMouse == wrap)
            {
                return false;
            }

            if (updated.Validate(out _).Count > 0)
            {
                return false;
            }

            try
            {
                updated.Save(path);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteLayoutSync), "Cannot save layout: " + e.Message);
                return false;
            }
        }
    }
}
