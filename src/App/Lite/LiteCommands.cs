// MwbLite: command-line entry points (replace the PowerToys runner / module interface / installer).
//
//   MwbLite.exe                 run (tray app). With "useService": true, starts the service and exits.
//   MwbLite.exe --install       copy to Program Files, register MwbLite.Service, add firewall rule,
//                               copy settings.json for the service (one UAC prompt), then start it.
//   MwbLite.exe --apply-config  copy your settings.json to the service's protected copy (UAC prompt).
//   MwbLite.exe --uninstall     remove service, firewall rule, Program Files copy, service data.
//   MwbLite.exe --settings      open the Settings window (edits %LOCALAPPDATA%\MwbLite\settings.json).
//   MwbLite.exe --open-config   open %LOCALAPPDATA%\MwbLite\settings.json in Notepad.
//   MwbLite.exe --service       service entry point (used by the Service Control Manager only).
//
// The "-elevated" variants are what the commands above re-launch through a UAC prompt.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteCommands
    {
        private const string MainExeName = LitePaths.AppName + ".exe";
        private const string FirewallRuleName = LitePaths.AppName;
        private const int ErrorCancelled = 1223;

        private static readonly string Usage =
            "MwbLite - standalone Mouse Without Borders\r\n\r\n" +
            "MwbLite.exe                 run (tray icon)\r\n" +
            "MwbLite.exe --install       install to Program Files + service + firewall rule (admin)\r\n" +
            "MwbLite.exe --apply-config  push settings.json to the service (admin)\r\n" +
            "MwbLite.exe --uninstall     remove everything --install added (admin)\r\n" +
            "MwbLite.exe --settings      open the Settings window\r\n" +
            "MwbLite.exe --open-config   open settings.json in Notepad\r\n\r\n" +
            "Config: " + LitePaths.UserSettingsFile;

        internal static int Run(string[] args)
        {
            string cmd = args[1].ToLowerInvariant();
            try
            {
                switch (cmd)
                {
                    case "--service":
                        LitePaths.EnsureProtectedDirectory(LitePaths.ServiceDir);
                        LiteLog.Initialize(Path.Combine(LitePaths.ServiceDir, "logs"), "service");
                        LiteLog.Write("Service", "Starting: " + Environment.CommandLine);
                        MouseWithoutBordersService.Program.RunService(args);
                        return 0;

                    case "--settings":
                        LiteLog.Initialize(Path.Combine(LitePaths.UserDir, "logs"), "commands");
                        Application.EnableVisualStyles();
                        _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                        Application.SetCompatibleTextRenderingDefault(false);
                        return LiteTray.RunSettingsProcess();

                    case "--open-config":
                        LiteLog.Initialize(Path.Combine(LitePaths.UserDir, "logs"), "commands");
                        LiteTray.OpenUserConfigInEditor();
                        return 0;

                    case "--install":
                        LiteLog.Initialize(Path.Combine(LitePaths.UserDir, "logs"), "commands");
                        return Install();

                    case "--apply-config":
                        LiteLog.Initialize(Path.Combine(LitePaths.UserDir, "logs"), "commands");
                        return ApplyConfig();

                    case "--uninstall":
                        LiteLog.Initialize(Path.Combine(LitePaths.UserDir, "logs"), "commands");
                        return Uninstall();

                    case "--install-elevated":
                    case "--apply-config-elevated":
                    case "--uninstall-elevated":
                        LitePaths.EnsureProtectedDirectory(LitePaths.ServiceDir);
                        LiteLog.Initialize(Path.Combine(LitePaths.ServiceDir, "logs"), "install");
                        return RunElevatedCommand(cmd, ParseOptions(args));

                    case "--help":
                    case "-h":
                    case "/?":
                        Show(Usage, MessageBoxIcon.Information);
                        return 0;

                    default:
                        Show("Unknown option " + args[1] + "\r\n\r\n" + Usage, MessageBoxIcon.Warning);
                        return 2;
                }
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteCommands), cmd + " failed: " + e);
                Show(cmd + " failed:\r\n" + e.Message, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ---- unelevated parts --------------------------------------------------------------

        private static int Install()
        {
            string userSettings = LitePaths.UserSettingsFile;
            bool created = LiteConfig.WriteDefaultIfMissing(userSettings);
            var cfg = LiteConfig.TryLoad(userSettings, out var errors, out _);
            if (cfg == null)
            {
                Show("Fix " + userSettings + " first:\r\n\r\n" + string.Join("\r\n", errors), MessageBoxIcon.Error);
                return 1;
            }

            if (!File.Exists(Path.Combine(LitePaths.ExeDir, LitePaths.HelperExeName)))
            {
                Show(LitePaths.HelperExeName + " must be next to " + MainExeName + ".", MessageBoxIcon.Error);
                return 1;
            }

            int rc = Elevate("--install-elevated", userSettings);
            if (rc != 0)
            {
                Show(rc == ErrorCancelled ? "Install cancelled." : "Install failed (exit code " + rc + "). See " + Path.Combine(LitePaths.ServiceDir, "logs", "install.log"), MessageBoxIcon.Error);
                return rc;
            }

            string installedExe = Path.Combine(LitePaths.InstallDir, MainExeName);
            LiteStartup.Sync(cfg.StartWithWindows, installedExe);
            StartUnelevated(installedExe);

            Show(
                "MwbLite installed to " + LitePaths.InstallDir + " and started.\r\n\r\n" +
                (created ? "A default settings.json was created; edit it (same securityKey on every PC) and use Reload config.\r\n\r\n" : string.Empty) +
                "Service mode (lock screen / UAC prompts): " + (cfg.UseService ? "ON" : "off (set \"useService\": true in settings.json)") + "\r\n" +
                "Start with Windows: " + (cfg.StartWithWindows ? "ON" : "off"),
                MessageBoxIcon.Information);
            return 0;
        }

        internal static int ApplyConfig()
        {
            string userSettings = LitePaths.UserSettingsFile;
            var cfg = LiteConfig.TryLoad(userSettings, out var errors, out _);
            if (cfg == null)
            {
                Show("settings.json was NOT applied:\r\n\r\n" + string.Join("\r\n", errors), MessageBoxIcon.Error);
                return 1;
            }

            LiteStartup.Sync(cfg.StartWithWindows, File.Exists(Path.Combine(LitePaths.InstallDir, MainExeName)) ? Path.Combine(LitePaths.InstallDir, MainExeName) : LitePaths.ExePath);

            int rc = Elevate("--apply-config-elevated", userSettings);
            if (rc != 0 && rc != ErrorCancelled)
            {
                Show("Applying settings failed (exit code " + rc + ").", MessageBoxIcon.Error);
            }

            return rc;
        }

        private static int Uninstall()
        {
            LiteStartup.Sync(false, LitePaths.ExePath);
            int rc = Elevate("--uninstall-elevated", null);
            if (rc == 0)
            {
                Show("MwbLite was removed. Your settings remain in " + LitePaths.UserDir + ".", MessageBoxIcon.Information);
            }
            else if (rc != ErrorCancelled)
            {
                Show("Uninstall failed (exit code " + rc + ").", MessageBoxIcon.Error);
            }

            return rc;
        }

        // ---- elevated parts -----------------------------------------------------------------

        private static int RunElevatedCommand(string cmd, Dictionary<string, string> opts)
        {
            if (!IsElevated())
            {
                Show(cmd + " must run elevated.", MessageBoxIcon.Error);
                return 5;
            }

            int parentPid = opts.TryGetValue("--parent-pid", out var p) && int.TryParse(p, out int pid) ? pid : -1;

            switch (cmd)
            {
                case "--install-elevated":
                    {
                        string userSid = opts.GetValueOrDefault("--user-sid") ?? WindowsIdentity.GetCurrent().User.Value;
                        _ = new SecurityIdentifier(userSid); // validate
                        var cfg = CopyConfigToService(opts.GetValueOrDefault("--user-settings"));

                        LiteServiceControl.Stop();
                        KillMwbProcesses(parentPid);
                        CopyBinaries();
                        LiteServiceControl.Register(Path.Combine(LitePaths.InstallDir, MainExeName), userSid);
                        SetFirewallRule(Path.Combine(LitePaths.InstallDir, MainExeName), cfg.Advanced.TcpPort);
                        LiteLog.Write(nameof(LiteCommands), "Install completed.");
                        return 0;
                    }

                case "--apply-config-elevated":
                    {
                        var cfg = CopyConfigToService(opts.GetValueOrDefault("--user-settings"));
                        SetFirewallRuleIfInstalled(cfg.Advanced.TcpPort);
                        return 0;
                    }

                case "--uninstall-elevated":
                    {
                        _ = LiteServiceControl.Unregister();
                        KillMwbProcesses(parentPid);
                        RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
                        LiteLog.Write(nameof(LiteCommands), "Uninstall: service and firewall rule removed.");
                        DeleteDirectory(LitePaths.ServiceDir);
                        DeleteDirectory(LitePaths.InstallDir);
                        return 0;
                    }
            }

            return 2;
        }

        private static LiteConfig CopyConfigToService(string userSettings)
        {
            if (string.IsNullOrEmpty(userSettings) || !File.Exists(userSettings))
            {
                throw new FileNotFoundException("User settings.json not found.", userSettings);
            }

            var cfg = LiteConfig.TryLoad(userSettings, out var errors, out _)
                ?? throw new InvalidDataException(string.Join("\r\n", errors));

            LitePaths.EnsureProtectedDirectory(LitePaths.ServiceDir);
            string tmp = LitePaths.ServiceSettingsFile + ".tmp";
            File.Copy(userSettings, tmp, overwrite: true);
            File.Move(tmp, LitePaths.ServiceSettingsFile, overwrite: true);
            LitePaths.EnsureProtectedDirectory(LitePaths.ServiceDir); // re-apply inherited ACL to the new file
            LiteLog.Write(nameof(LiteCommands), "Copied " + userSettings + " to " + LitePaths.ServiceSettingsFile);
            return cfg;
        }

        private static void CopyBinaries()
        {
            string src = LitePaths.ExeDir;
            string dst = LitePaths.InstallDir;
            if (string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(dst).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(dst); // inherits the admin-only-write ACL of Program Files
            foreach (string name in new[] { MainExeName, LitePaths.HelperExeName })
            {
                File.Copy(Path.Combine(src, name), Path.Combine(dst, name), overwrite: true);
            }

            foreach (string extra in new[] { "LICENSE", "NOTICE.md", "README.md" })
            {
                string file = Path.Combine(src, extra);
                if (File.Exists(file))
                {
                    File.Copy(file, Path.Combine(dst, extra), overwrite: true);
                }
            }

            LiteLog.Write(nameof(LiteCommands), "Copied binaries from " + src + " to " + dst);
        }

        private static void SetFirewallRuleIfInstalled(int tcpPort)
        {
            string exe = Path.Combine(LitePaths.InstallDir, MainExeName);
            if (File.Exists(exe))
            {
                SetFirewallRule(exe, tcpPort);
            }
        }

        // Inbound TCP to MWB's two ports (tcpPort = clipboard/files, tcpPort+1 = input/control), for this
        // program only, from the local subnet only. (Upstream allowed the program from any address.)
        private static void SetFirewallRule(string exe, int tcpPort)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any protocol=tcp localport={tcpPort},{tcpPort + 1} remoteip=localsubnet");
        }

        private static void RunNetsh(string arguments)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            LiteLog.Write(nameof(LiteCommands), $"netsh {arguments} -> {p.ExitCode} {output.Trim()}");
        }

        private static void KillMwbProcesses(int exceptPid)
        {
            int me = Environment.ProcessId;
            foreach (string name in new[] { LitePaths.AppName, Path.GetFileNameWithoutExtension(LitePaths.HelperExeName) })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        if (p.Id == me || p.Id == exceptPid)
                        {
                            continue;
                        }

                        try
                        {
                            p.Kill();
                            _ = p.WaitForExit(5000);
                            LiteLog.Write(nameof(LiteCommands), $"Stopped {name} ({p.Id}).");
                        }
                        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                        {
                            LiteLog.Write(nameof(LiteCommands), $"Cannot stop {name} ({p.Id}): {e.Message}");
                        }
                    }
                }
            }
        }

        private static void DeleteDirectory(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            string exeDir = LitePaths.ExeDir.TrimEnd('\\');
            if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), exeDir, StringComparison.OrdinalIgnoreCase))
            {
                // Running from the directory being removed: delete it after this process exits.
                _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteCommands), "Cannot delete " + dir + ": " + e.Message);
            }
        }

        // ---- helpers ------------------------------------------------------------------------

        private static int Elevate(string elevatedCommand, string userSettings)
        {
            var args = new List<string> { elevatedCommand, "--parent-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (userSettings != null)
            {
                // Pass the invoking user's file and SID explicitly: the elevated process may run as a
                // different administrator account (over-the-shoulder elevation).
                args.AddRange(new[] { "--user-settings", userSettings, "--user-sid", WindowsIdentity.GetCurrent().User.Value });
            }

            if (IsElevated())
            {
                return RunElevatedCommand(elevatedCommand, ParseOptions(new[] { LitePaths.ExePath }.Concat(args).ToArray()));
            }

            var psi = new ProcessStartInfo(LitePaths.ExePath, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            try
            {
                using var p = Process.Start(psi);
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
            {
                return ErrorCancelled;
            }
        }

        private static void StartUnelevated(string exe)
        {
            if (IsElevated())
            {
                // Explorer starts it with the shell's (unelevated) token.
                _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Quote(exe)));
            }
            else
            {
                _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 2; i + 1 < args.Length; i += 2)
            {
                opts[args[i]] = args[i + 1];
            }

            return opts;
        }

        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return arg;
            }

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                sb.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
                backslashes = 0;
                sb.Append(c);
            }

            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        private static void Show(string text, MessageBoxIcon icon)
        {
            LiteLog.Write(nameof(LiteCommands), text);
            _ = MessageBox.Show(text, LitePaths.AppName, MessageBoxButtons.OK, icon);
        }
    }
}
