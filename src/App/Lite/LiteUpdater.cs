// MwbLite: "MwbLite.exe --install-update", run as SYSTEM by the "MwbLite Update" scheduled task (see LiteUpdateTask).
// It installs the newest release without a UAC prompt:
//
// 1. Staging. The installer replaces MwbLite.exe and stops every MwbLite.exe first, which would stop this very process.
//    So the task's process copies the installed program to a protected folder under another name (MwbLite.Updater.exe)
//    and runs that copy, which does the rest. The task's own process just waits for it.
// 2. The copy asks GitHub for the latest release (LiteUpdate.GetLatestAsync) and stops if it isn't newer than the
//    installed version. Otherwise it downloads the release's Setup into %ProgramData%\MwbLite\update, a folder only
//    SYSTEM and Administrators can write (explicit ACL, no inheritance). It first checks that the release's
//    SHA256SUMS.txt is signed with the release key built into this program (LiteReleaseKey) and refuses the release
//    if it isn't; Setup must then match the SHA-256 in that signed list.
// 3. Right before running it, the file is opened again with writing and deleting denied to everyone else, hashed once
//    more on that handle, and run with "/quiet /norestart" while the handle is still held, so the file that was
//    checked is the file that runs. Its exit code goes to the system log (%ProgramData%\MwbLite\logs\update.log).
// 4. If MwbLite was running before (the installer stops it), it is started again for the user at the console, from the
//    installed folder (LiteUserLaunch). Service mode needs nothing more: the service is stopped by the installer and is
//    not started by it, so the user's MwbLite is what starts it again, as at every sign-in.
//
// Only one update runs at a time (an exclusive lock on a file in the protected update folder; a named mutex would be
// open to any user, who could hold it to stop updates). The folders used here are checked before use: a junction or
// link, or a folder an ordinary user owns, is removed and created again (LitePaths.EnsureTrustedDirectory). Nothing
// here takes input from whoever started the task. This runs in session 0 where there is no desktop, so every failure
// is logged and returned as an exit code, never shown.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteUpdater
    {
        internal const string RunnerExeName = "MwbLite.Updater.exe";

        private const string LockFileName = "update.lock";
        private const string MainExeName = LitePaths.AppName + ".exe";

        // The task is stopped after 30 minutes; the download and the install together must fit.
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(15);

        // Exit codes of "--install-update" (0: installed, or nothing to install).
        private const int ExitFailed = 1;
        private const int ExitNotElevated = 5;
        private const int ExitBusy = 10;
        private const int ExitSetupFailed = 12;
        private const int ExitNotSigned = 13;

        /// <summary>Where the update is downloaded and the staged copy runs from: SYSTEM and Administrators only.</summary>
        internal static string UpdateDir => Path.Combine(LitePaths.ServiceDir, "update");

        internal static string RunnerDir => Path.Combine(UpdateDir, "runner");

        /// <summary>"--install-update [--parent-pid n]": see the file header.</summary>
        internal static int Run(string[] args)
        {
            try
            {
                if (!LitePaths.IsSystem && !LiteCommands.IsElevated())
                {
                    return ExitNotElevated;
                }

                // A user can create %ProgramData%\MwbLite (and anything in it) before Setup does, so none of these is trusted as found.
                LitePaths.EnsureTrustedDirectory(LitePaths.ServiceDir, requireTrustedOwner: false);
                string logs = Path.Combine(LitePaths.ServiceDir, "logs");
                LitePaths.EnsureTrustedDirectory(logs, requireTrustedOwner: true);
                LiteLog.Initialize(logs, "update");
                LitePaths.EnsureTrustedDirectory(UpdateDir, requireTrustedOwner: true);

                return string.Equals(Path.GetFileName(LitePaths.ExePath), RunnerExeName, StringComparison.OrdinalIgnoreCase)
                    ? InstallLatest(ParentPid(args))
                    : StageAndRun();
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteUpdater), "The update failed: " + e);
                return ExitFailed;
            }
        }

        // ---- 1. staging ---------------------------------------------------------------------------------------

        private static int StageAndRun()
        {
            string installedExe = Path.Combine(LitePaths.InstallDir, MainExeName);
            if (!LiteCommands.IsInstalledExe(LitePaths.ExePath, LitePaths.InstallDir))
            {
                LiteLog.Write(nameof(LiteUpdater), "Refusing to update from " + LitePaths.ExePath + ": only the copy installed by Setup (" + installedExe + ") may.");
                return ExitFailed;
            }

            string runner;
            try
            {
                runner = StageRunner(LitePaths.ExeDir, RunnerDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteUpdater), "Another update seems to be running (" + e.Message + ").");
                return ExitBusy;
            }

            LiteLog.Write(nameof(LiteUpdater), "Starting the staged copy " + runner);
            string args = LiteUpdateTask.Arguments + " --parent-pid " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using Process child = Process.Start(new ProcessStartInfo(runner, args) { UseShellExecute = false, WorkingDirectory = RunnerDir })
                ?? throw new InvalidOperationException("The staged copy didn't start.");
            child.WaitForExit();
            int code = child.ExitCode;
            LiteCommands.DeleteDirectory(RunnerDir);
            return code;
        }

        private static int ParentPid(string[] args)
        {
            int at = Array.FindIndex(args, a => string.Equals(a, "--parent-pid", StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int pid) ? pid : -1;
        }

        /// <summary>
        /// Copies the installed program (the whole folder) into <paramref name="runnerDir"/>, replacing what is there,
        /// with MwbLite.exe renamed to <see cref="RunnerExeName"/> (the installer only stops MwbLite.exe by name).
        /// Returns the staged exe. Throws IOException if the folder is in use, i.e. another update is running.
        /// </summary>
        internal static string StageRunner(string sourceDir, string runnerDir)
        {
            LitePaths.DeleteDirectoryOrLink(runnerDir); // a link planted here is removed, never followed
            _ = Directory.CreateDirectory(runnerDir); // inherits the protected ACL of the update folder
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDir, file);
                if (string.Equals(relative, MainExeName, StringComparison.OrdinalIgnoreCase))
                {
                    relative = RunnerExeName;
                }

                string target = Path.Combine(runnerDir, relative);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: true);
            }

            string exe = Path.Combine(runnerDir, RunnerExeName);
            return File.Exists(exe) ? exe : throw new FileNotFoundException("MwbLite.exe wasn't found in " + sourceDir, exe);
        }

        // ---- 2-4. download, verify, install, restart -------------------------------------------------------------

        /// <summary>
        /// Takes the exclusive lock that keeps a second update from running: a file in <paramref name="dir"/> that nobody
        /// else may open while it is held (and that goes away when the holder closes it). Null if another update holds it.
        /// </summary>
        internal static FileStream TryLock(string dir)
        {
            try
            {
                return new FileStream(Path.Combine(dir, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            {
                return null;
            }
        }

        private static int InstallLatest(int taskProcessId)
        {
            using FileStream updateLock = TryLock(UpdateDir);
            if (updateLock == null)
            {
                LiteLog.Write(nameof(LiteUpdater), "Another update is already running.");
                return ExitBusy;
            }

            try
            {
                LiteLog.Write(nameof(LiteUpdater), "Checking for a newer MwbLite than " + LiteUpdate.CurrentText);
                using var cts = new CancellationTokenSource(DownloadTimeout);
                ReleaseInfo release = LiteUpdate.GetLatestAsync(cts.Token).GetAwaiter().GetResult();
                if (!LiteUpdate.IsNewer(release))
                {
                    LiteLog.Write(nameof(LiteUpdater), "Already up to date (" + (release == null ? "no release" : release.Version.ToString(3)) + ").");
                    return 0;
                }

                LiteLog.Write(nameof(LiteUpdater), $"Updating {LiteUpdate.CurrentText} to {release.Version.ToString(3)}.");
                bool wasRunning = IsMwbLiteRunning(taskProcessId);

                // Old downloads are removed first; only the runner folder and this download live here.
                foreach (string old in Directory.EnumerateFiles(UpdateDir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    File.Delete(old);
                }

                LiteUpdate.VerifiedSetup setup;
                try
                {
                    setup = LiteUpdate.DownloadVerifiedAsync(release, null, UpdateDir, cts.Token).GetAwaiter().GetResult();
                }
                catch (ReleaseNotSignedException)
                {
                    LiteLog.Write(nameof(LiteUpdater), ReleaseNotSignedException.Text);
                    return ExitNotSigned;
                }

                int code = RunVerifiedSetup(setup.Path, setup.Sha256);
                LiteLog.Write(nameof(LiteUpdater), $"Setup exited with code {code}.");

                if (wasRunning)
                {
                    _ = LiteUserLaunch.StartAsConsoleUser(Path.Combine(LitePaths.InstallDir, MainExeName));
                }

                try
                {
                    File.Delete(setup.Path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    LiteLog.Write(nameof(LiteUpdater), "Cannot remove " + setup.Path + ": " + e.Message);
                }

                return IsSetupSuccess(code) ? 0 : ExitSetupFailed;
            }
            finally
            {
                LiteCommands.DeleteDirectory(RunnerDir); // this process runs from there: removed just after it exits
            }
        }

        /// <summary>
        /// Hashes the file on a handle that denies others writing and deleting it and, if it is still what was
        /// verified, runs it silently while that handle is held. Returns Setup's exit code.
        /// </summary>
        private static int RunVerifiedSetup(string path, string expectedSha256)
        {
            using FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!HashMatches(held, expectedSha256))
            {
                throw new InvalidDataException("The downloaded Setup changed after it was verified, so it was not run.");
            }

            using Process setup = Process.Start(new ProcessStartInfo(path, "/quiet /norestart") { UseShellExecute = false, WorkingDirectory = UpdateDir })
                ?? throw new InvalidOperationException("Setup didn't start.");
            if (!setup.WaitForExit(SetupTimeout))
            {
                LiteLog.Write(nameof(LiteUpdater), "Setup is still running after " + SetupTimeout.TotalMinutes + " minutes; not waiting any longer.");
                return -1;
            }

            return setup.ExitCode;
        }

        /// <summary>True if the stream's SHA-256 equals <paramref name="expectedHex"/> (any case). Leaves the stream at its start.</summary>
        internal static bool HashMatches(Stream s, string expectedHex)
        {
            s.Position = 0;
            string actual = LiteUpdate.Sha256Hex(s);
            s.Position = 0;
            return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Setup's exit codes that mean it installed: success, or success with a restart still needed.</summary>
        internal static bool IsSetupSuccess(int exitCode) => exitCode is 0 or 3010 or 1641;

        // MwbLite counts as running if any MwbLite.exe is, other than the task's own first process: the tray app, the
        // SYSTEM instances of service mode, or just the Settings window. The installer stops them all, so this decides
        // whether to start MwbLite again, which is not wanted if the user had quit it.
        private static bool IsMwbLiteRunning(int exceptProcessId)
        {
            Process[] all = Process.GetProcessesByName(LitePaths.AppName);
            bool running = all.Any(p => p.Id != exceptProcessId);
            foreach (Process p in all)
            {
                p.Dispose();
            }

            return running;
        }
    }
}
