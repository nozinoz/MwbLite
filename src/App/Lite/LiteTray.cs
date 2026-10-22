// MwbLite: tray menu actions (Open config file, Reload config) and config notices.
//
// In service mode the tray belongs to a SYSTEM process. It must never start an editor (or anything
// else interactive) as SYSTEM, so both actions are delegated to "MwbLite.exe --open-config" /
// "--apply-config" started with the interactive user's token (the existing upstream Launch helper).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteTray
    {
        private static FileSystemWatcher settingsWatcher;
        private static System.Threading.Timer reloadDebounce;

        /// <summary>
        /// Opens the Settings window: always its own process ("MwbLite.exe --settings", WPF), as the interactive user
        /// (never as SYSTEM). In normal mode the running MwbLite reloads when that window saves settings.json
        /// (<see cref="WatchUserSettings"/>).
        /// </summary>
        internal static void ShowSettings()
        {
            if (Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop)
            {
                return;
            }

            try
            {
                if (LitePaths.IsSystem)
                {
                    _ = Launch.CreateProcessInInputDesktopSession("\"" + LitePaths.ExePath + "\"", "--settings", "default", 1);
                }
                else
                {
                    _ = Process.Start(new ProcessStartInfo(LitePaths.ExePath, "--settings") { UseShellExecute = false });
                }
            }
            catch (Exception e)
            {
                Logger.Log(e);
                Common.ShowToolTip("Cannot open Settings: " + e.Message, 5000, ToolTipIcon.Error);
            }
        }

        /// <summary>"MwbLite.exe --settings": the Settings window (WPF, Windows 11 Fluent theme) as its own process.</summary>
        internal static int RunSettingsProcess()
        {
            using var mutex = new System.Threading.Mutex(true, @"Local\MwbLite-Settings", out bool first);
            if (!first)
            {
                BringExistingSettingsToFront();
                return 0;
            }

            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
#pragma warning disable WPF0001 // Fluent theme: supported in .NET 10, still flagged as preview by some SDKs
            app.ThemeMode = System.Windows.ThemeMode.System;
#pragma warning restore WPF0001
            var window = Ui.SettingsWindow.Create(LitePaths.UserSettingsFile, cfg =>
            {
                string installedExe = Path.Combine(LitePaths.InstallDir, LitePaths.AppName + ".exe");
                LiteStartup.Sync(cfg.StartWithWindows, File.Exists(installedExe) ? installedExe : LitePaths.ExePath);
                _ = AfterSettingsSaved(cfg); // normal mode: the running MwbLite reloads by itself (WatchUserSettings)
            });

            if (window != null)
            {
                _ = app.Run(window);
            }

            return 0;
        }

        private static void BringExistingSettingsToFront()
        {
            foreach (Process p in Process.GetProcessesByName(LitePaths.AppName))
            {
                using (p)
                {
                    if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle == "MwbLite Settings")
                    {
                        _ = NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                    }
                }
            }
        }

        /// <summary>
        /// Service-mode follow-up after a save: offer the one-time install, or push the new settings to the
        /// service's protected copy (UAC). Returns true if it started either.
        /// </summary>
        private static bool AfterSettingsSaved(LiteConfig cfg)
        {
            if (!cfg.UseService)
            {
                return false;
            }

            if (!LiteServiceControl.IsInstalled())
            {
                if (MessageBox.Show(
                    "Service mode needs a one-time install (administrator): MwbLite is copied to Program Files, the MwbLite.Service service and a firewall rule are added, and MwbLite restarts.\r\n\r\nInstall now?",
                    "MwbLite Settings",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    _ = Process.Start(new ProcessStartInfo(LitePaths.ExePath, "--install") { UseShellExecute = false });
                }

                return true;
            }

            // The SYSTEM instances read %ProgramData%\MwbLite\settings.json; copying it needs a UAC prompt.
            _ = System.Threading.Tasks.Task.Run(LiteCommands.ApplyConfig);
            return true;
        }

        internal static void OpenConfigFile()
        {
            if (Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop)
            {
                return;
            }

            try
            {
                if (LitePaths.IsSystem)
                {
                    _ = Launch.CreateProcessInInputDesktopSession("\"" + LitePaths.ExePath + "\"", "--open-config", "default", 1);
                    return;
                }

                OpenUserConfigInEditor();
            }
            catch (Exception e)
            {
                Logger.Log(e);
                Common.ShowToolTip("Cannot open the config file: " + e.Message, 5000, ToolTipIcon.Error);
            }
        }

        /// <summary>Opens the current user's settings.json in Notepad (creating it with defaults if missing).</summary>
        internal static void OpenUserConfigInEditor()
        {
            string path = LitePaths.UserSettingsFile;
            _ = LiteConfig.WriteDefaultIfMissing(path);
            _ = Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
        }

        internal static void ReloadConfig()
        {
            if (Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop)
            {
                return;
            }

            if (LitePaths.IsSystem)
            {
                // Copy the user's settings.json to %ProgramData%\MwbLite after a UAC prompt; the watcher
                // below then reloads it in every SYSTEM instance.
                _ = Launch.CreateProcessInInputDesktopSession("\"" + LitePaths.ExePath + "\"", "--apply-config", "default", 1);
                Common.ShowToolTip("Service mode: approve the administrator prompt to apply settings.json.", 5000);
                return;
            }

            ReloadAndReport();
        }

        internal static void ReloadAndReport()
        {
            if (Setting.Values.Reload(out var errors, out var warnings, out bool restartNeeded))
            {
                if (!LitePaths.IsSystem)
                {
                    LiteStartup.Sync(Setting.Values.StartWithWindows, Application.ExecutablePath);
                }

                string msg = "Configuration reloaded.";
                if (restartNeeded)
                {
                    msg += " Restart MwbLite to apply tcpPort/useService changes.";
                }

                if (warnings.Count > 0)
                {
                    msg += "\r\n" + warnings[0];
                }

                Common.ShowToolTip(Truncate(msg), 5000, ToolTipIcon.Info);
            }
            else
            {
                Common.ShowToolTip(Truncate("settings.json was NOT applied:\r\n" + string.Join("\r\n", errors)), 10000, ToolTipIcon.Error);
            }
        }

        /// <summary>SYSTEM instances: reload when the elevated --apply-config command updates settings.json.</summary>
        internal static void WatchServiceSettings() => WatchSettings(LitePaths.ServiceDir);

        /// <summary>Normal mode: reload when the Settings window (its own process) or pairing saves settings.json.</summary>
        internal static void WatchUserSettings() => WatchSettings(LitePaths.UserDir);

        private static void WatchSettings(string dir)
        {
            try
            {
                settingsWatcher = new FileSystemWatcher(dir, LitePaths.SettingsFileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };

                FileSystemEventHandler onChange = (s, e) => reloadDebounce?.Change(750, System.Threading.Timeout.Infinite);
                settingsWatcher.Changed += onChange;
                settingsWatcher.Created += onChange;
                settingsWatcher.Renamed += (s, e) => onChange(s, e);

                reloadDebounce = new System.Threading.Timer(_ => Common.DoSomethingInUIThread(ReloadAndReport));
                settingsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                Logger.Log(e);
            }
        }

        /// <summary>Called once the tray icon exists.</summary>
        internal static void ShowStartupNotices()
        {
            var values = Setting.Values;
            if (values.ConfigErrors.Count > 0)
            {
                Common.ShowToolTip(Truncate("settings.json has errors, using defaults:\r\n" + string.Join("\r\n", values.ConfigErrors)), 15000, ToolTipIcon.Error);
            }
            else if (values.CreatedDefaultConfig)
            {
                Common.ShowToolTip(Truncate($"Created {values.SettingsFile}. Put the same securityKey and machine names on every PC, then use Reload config."), 15000, ToolTipIcon.Info);
            }
            else if (values.ConfigWarnings.Count > 0)
            {
                Common.ShowToolTip(Truncate(values.ConfigWarnings[0]), 8000, ToolTipIcon.Warning);
            }
        }

        private static string Truncate(string s) => s.Length <= 250 ? s : s[..247] + "...";

        // ---- live status (tray tooltip/icon, status.json, transfer window) -------------------------------------

        private static readonly TimeSpan StatusRewrite = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ShowWindowAfter = TimeSpan.FromSeconds(1);
        private const long ShowWindowAbove = 2L << 20; // 2 MB

        private static int timerTicks;
        private static string lastSignature;
        private static DateTime lastStatusWrite;
        private static bool keyMismatchNotified;
        private static bool olderVersionNotified;
        private static bool statusDirReady;

        /// <summary>Called by FrmScreen.HelperTimer_Tick (UI thread, every 100 ms).</summary>
        internal static void OnTimer()
        {
            if (Common.RunOnLogonDesktop || Common.RunOnScrSaverDesktop)
            {
                return;
            }

            try
            {
                timerTicks++;
                DateTime now = DateTime.UtcNow;

                if (timerTicks % 3 == 0 && Setting.Values.ShowTransferProgress && !TransferWindow.IsOpen &&
                    LiteTransfers.Snapshot(now).Any(t => t.IsActive && (t.Preparing || t.Total > ShowWindowAbove || now - t.StartedUtc > ShowWindowAfter)))
                {
                    TransferWindow.ShowIfNeeded();
                }

                if (timerTicks % 10 != 0)
                {
                    return; // the rest once a second
                }

                StatusSnapshot status = LiteStatus.Refresh();
                string signature = status.Signature + "|" + Ui.TaskbarTheme.IsLight; // a theme change redraws the glyph
                if (signature != lastSignature)
                {
                    lastSignature = signature;
                    Common.MainForm?.SetTrayIconText(LiteStatus.TrayText(status));
                    if (Common.ToggleIcons == null)
                    {
                        Common.MainForm?.ChangeIcon(-1); // status dot colour
                    }

                    PcStatus mismatch = status.Pcs.FirstOrDefault(p => p.State == PcState.KeyMismatch);
                    if (mismatch != null && !keyMismatchNotified)
                    {
                        keyMismatchNotified = true;
                        Common.ShowToolTip($"{mismatch.Name} has a different security key, so it can't connect. Pair the PCs again: Settings > Add a PC.", 10000, ToolTipIcon.Warning, true);
                    }
                    else if (mismatch == null)
                    {
                        keyMismatchNotified = false;
                    }

                    PcStatus older = status.Pcs.FirstOrDefault(p => p.State == PcState.OlderVersion);
                    if (older != null && !olderVersionNotified)
                    {
                        olderVersionNotified = true;
                        Common.ShowToolTip($"{older.Name} runs an older MwbLite (or PowerToys), which isn't secure, so it was refused. Update it to this version, or allow older versions in Settings > Advanced.", 10000, ToolTipIcon.Warning, true);
                    }
                    else if (older == null)
                    {
                        olderVersionNotified = false;
                    }

                    WriteStatus(status, now);
                }
                else if (now - lastStatusWrite > StatusRewrite)
                {
                    WriteStatus(status, now);
                }
            }
            catch (Exception e)
            {
                Logger.Log(e);
            }
        }

        private static void WriteStatus(StatusSnapshot status, DateTime now)
        {
            lastStatusWrite = now;
            string path = LitePaths.StatusFile;
            if (LitePaths.IsSystem && !statusDirReady)
            {
                LitePaths.EnsureReadableDirectory(Path.GetDirectoryName(path));
                statusDirReady = true;
            }

            LiteStatus.Write(status, path);
        }
    }
}
