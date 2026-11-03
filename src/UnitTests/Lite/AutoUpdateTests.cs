// MwbLite: tests for updating without a UAC prompt, automatically, and together with the other PCs: the decision
// functions, rate limit, the staged copy of the updater, the file check before running Setup, and the shape of the
// scheduled task (built in memory only; nothing is registered).
using System;
using System.IO;
using System.Security.AccessControl;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Lite;

namespace MouseWithoutBorders.UnitTests.Lite;

[TestClass]
public sealed class AutoUpdateTests
{
    private static readonly Version V080 = new(0, 8, 0);
    private static readonly Version V090 = new(0, 9, 0);
    private static readonly Version V091 = new(0, 9, 1);

    private static ReleaseInfo Release(Version v, string? setupUrl = "https://github.com/nozinoz/MwbLite/releases/download/v9/MwbLite-Setup-9.exe") =>
        new(v, "v" + v.ToString(3), "MwbLite", string.Empty, string.Empty, DateTime.UtcNow, "MwbLite-Setup.exe", setupUrl!, 1, null!, null!);

    [TestMethod]
    public void NewerThanComparesVersionsAndToleratesUnknown()
    {
        Assert.IsTrue(LiteAutoUpdate.IsNewerThan(V091, V090));
        Assert.IsTrue(LiteAutoUpdate.IsNewerThan(new Version(1, 0, 0), V091));
        Assert.IsFalse(LiteAutoUpdate.IsNewerThan(V090, V090));
        Assert.IsFalse(LiteAutoUpdate.IsNewerThan(V080, V090));
        Assert.IsFalse(LiteAutoUpdate.IsNewerThan(null!, V090));
        Assert.IsFalse(LiteAutoUpdate.IsNewerThan(V090, null!));
        Assert.IsTrue(LiteAutoUpdate.IsNewerThan(LiteUpdate.ParseVersion("v0.10.0")!, LiteUpdate.ParseVersion("0.9.9")!), "numeric, not text, order");
    }

    [TestMethod]
    public void WeeklyCheckInstallsByItselfOnlyWhenOnAndNewerAndDownloadable()
    {
        Assert.IsTrue(LiteAutoUpdate.ShouldInstallAutomatically(true, Release(V091), V090));
        Assert.IsFalse(LiteAutoUpdate.ShouldInstallAutomatically(false, Release(V091), V090), "switch off");
        Assert.IsFalse(LiteAutoUpdate.ShouldInstallAutomatically(true, Release(V090), V090), "same version");
        Assert.IsFalse(LiteAutoUpdate.ShouldInstallAutomatically(true, Release(V080), V090), "older");
        Assert.IsFalse(LiteAutoUpdate.ShouldInstallAutomatically(true, Release(V091, setupUrl: null), V090), "no Setup in the release");
        Assert.IsFalse(LiteAutoUpdate.ShouldInstallAutomatically(true, null!, V090), "no release");
    }

    [TestMethod]
    public void OlderConnectedPcsAreToldOnlyWhenOn()
    {
        Assert.IsTrue(LiteAutoUpdate.ShouldAnnounceToPeer(true, V091, V090));
        Assert.IsFalse(LiteAutoUpdate.ShouldAnnounceToPeer(false, V091, V090), "switch off");
        Assert.IsFalse(LiteAutoUpdate.ShouldAnnounceToPeer(true, V090, V090), "same version");
        Assert.IsFalse(LiteAutoUpdate.ShouldAnnounceToPeer(true, V090, V091), "peer is newer: it will tell us");
        Assert.IsFalse(LiteAutoUpdate.ShouldAnnounceToPeer(true, V091, null!), "version unknown");
    }

    [TestMethod]
    public void AnnouncementIsActedOnOnlyWhenGitHubConfirmsIt()
    {
        // announced, current, latest on GitHub, task available
        Assert.AreEqual(PeerUpdateAction.UpdateNow, LiteAutoUpdate.DecideOnAnnouncement(true, V091, V090, V091, true));
        Assert.AreEqual(PeerUpdateAction.UpdateNow, LiteAutoUpdate.DecideOnAnnouncement(true, V091, V090, new Version(0, 9, 5), true), "GitHub has even newer");
        Assert.AreEqual(PeerUpdateAction.TellUser, LiteAutoUpdate.DecideOnAnnouncement(true, V091, V090, V091, false), "no update task: only notify");

        Assert.AreEqual(PeerUpdateAction.Ignore, LiteAutoUpdate.DecideOnAnnouncement(false, V091, V090, V091, true), "switch off");
        Assert.AreEqual(PeerUpdateAction.Ignore, LiteAutoUpdate.DecideOnAnnouncement(true, V090, V090, V091, true), "not newer than ours");
        Assert.AreEqual(PeerUpdateAction.Ignore, LiteAutoUpdate.DecideOnAnnouncement(true, V080, V090, V091, true), "older than ours");
        Assert.AreEqual(PeerUpdateAction.Ignore, LiteAutoUpdate.DecideOnAnnouncement(true, V091, V090, V090, true), "GitHub has nothing that new (the peer lies or runs a build that isn't released)");
        Assert.AreEqual(PeerUpdateAction.Ignore, LiteAutoUpdate.DecideOnAnnouncement(true, V091, V090, null!, true), "GitHub unreachable");
        Assert.AreEqual(PeerUpdateAction.Ignore, LiteAutoUpdate.DecideOnAnnouncement(true, null!, V090, V091, true), "unreadable version");
    }

    [TestMethod]
    public void EachKeyIsAllowedOncePerHour()
    {
        var limit = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromHours(1));
        DateTime t = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

        Assert.IsTrue(limit.TryAcquire("0.9.1", t));
        Assert.IsFalse(limit.TryAcquire("0.9.1", t.AddMinutes(1)));
        Assert.IsFalse(limit.TryAcquire("0.9.1", t.AddMinutes(59)));
        Assert.IsTrue(limit.TryAcquire("0.9.2", t.AddMinutes(2)), "another version is separate");
        Assert.IsTrue(limit.TryAcquire("0.9.1", t.AddMinutes(61)), "an hour later it is allowed again");
        Assert.IsFalse(limit.TryAcquire("0.9.1", t.AddMinutes(62)));
        Assert.IsFalse(limit.TryAcquire("0.9.1".ToUpperInvariant(), t.AddMinutes(63)), "keys ignore case");
    }

    [TestMethod]
    public void RateLimitRestartsIfTheClockIsSetBack()
    {
        var limit = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromHours(1));
        DateTime t = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        Assert.IsTrue(limit.TryAcquire("0.9.1", t));
        Assert.IsTrue(limit.TryAcquire("0.9.1", t.AddDays(-1)), "a clock set back must not block for a day");
        Assert.IsFalse(limit.TryAcquire("0.9.1", t.AddDays(-1).AddMinutes(5)));
    }

    [TestMethod]
    public void RateLimitForgetsOldKeys()
    {
        var limit = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromMinutes(10));
        DateTime t = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 200; i++)
        {
            Assert.IsTrue(limit.TryAcquire("pc" + i, t.AddSeconds(i)));
        }

        Assert.IsTrue(limit.TryAcquire("pc0", t.AddMinutes(30)));
    }

    [TestMethod]
    public void PcNamesInNotificationsAreCleaned()
    {
        Assert.AreEqual("LAPTOP", LiteAutoUpdate.CleanName("  LAPTOP \r\n"));
        Assert.AreEqual("Another PC", LiteAutoUpdate.CleanName(null!));
        Assert.AreEqual("Another PC", LiteAutoUpdate.CleanName("\u0007\t"));
        Assert.AreEqual(40, LiteAutoUpdate.CleanName(new string('x', 200)).Length);
    }

    [TestMethod]
    public void NewSettingsDefaultAndRoundTrip()
    {
        var cfg = new LiteConfig();
        Assert.IsFalse(cfg.InstallUpdatesAutomatically, "installing by itself is opt-in");
        Assert.IsTrue(cfg.UpdateWithOtherPcs);

        cfg.InstallUpdatesAutomatically = true;
        cfg.UpdateWithOtherPcs = false;
        LiteConfig copy = cfg.Clone();
        Assert.IsTrue(copy.InstallUpdatesAutomatically);
        Assert.IsFalse(copy.UpdateWithOtherPcs);
    }

    // ---- the updater (LiteUpdater, LiteCommands) -------------------------------------------------------------

    [TestMethod]
    public void OnlyTheCopyInProgramFilesMayRegisterTheTask()
    {
        string installDir = @"C:\Program Files\MwbLite";
        Assert.IsTrue(LiteCommands.IsInstalledExe(@"C:\Program Files\MwbLite\MwbLite.exe", installDir));
        Assert.IsTrue(LiteCommands.IsInstalledExe(@"c:\program files\mwblite\..\MwbLite\mwblite.EXE", installDir));
        Assert.IsFalse(LiteCommands.IsInstalledExe(@"C:\Users\someone\Downloads\MwbLite.exe", installDir));
        Assert.IsFalse(LiteCommands.IsInstalledExe(@"C:\Program Files\MwbLite\sub\MwbLite.exe", installDir));
        Assert.IsFalse(LiteCommands.IsInstalledExe(@"C:\Program Files\MwbLite\MwbLite.Updater.exe", installDir));
        Assert.IsFalse(LiteCommands.IsInstalledExe(null!, installDir));
        Assert.IsFalse(LiteCommands.IsInstalledExe(@"C:\Program Files\MwbLite\MwbLite.exe", string.Empty));
    }

    [TestMethod]
    public void SetupExitCodesThatMeanInstalled()
    {
        Assert.IsTrue(LiteUpdater.IsSetupSuccess(0));
        Assert.IsTrue(LiteUpdater.IsSetupSuccess(3010), "restart still needed");
        Assert.IsTrue(LiteUpdater.IsSetupSuccess(1641));
        Assert.IsFalse(LiteUpdater.IsSetupSuccess(1602), "cancelled");
        Assert.IsFalse(LiteUpdater.IsSetupSuccess(1603));
        Assert.IsFalse(LiteUpdater.IsSetupSuccess(-1), "timed out");
    }

    [TestMethod]
    public void FileIsRecheckedOnTheHandleThatIsHeld()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "MwbLite-Setup.exe");
            File.WriteAllText(path, "pretend this is Setup");
            string sha;
            using (FileStream f = File.OpenRead(path))
            {
                sha = LiteUpdate.Sha256Hex(f);
            }

            Assert.AreEqual(64, sha.Length);
            using FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.IsTrue(LiteUpdater.HashMatches(held, sha));
            Assert.IsTrue(LiteUpdater.HashMatches(held, sha.ToUpperInvariant()));
            Assert.IsFalse(LiteUpdater.HashMatches(held, new string('0', 64)));
            Assert.AreEqual(0, held.Position, "left at the start");

            // While held, nobody else can change or delete it, so what was hashed is what runs.
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(path, "tampered"));
            Assert.ThrowsExactly<IOException>(() => File.Delete(path));
            Assert.ThrowsExactly<IOException>(() => File.Move(path, path + ".moved"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void UpdaterIsStagedUnderAnotherNameAndReplacedEachTime()
    {
        string root = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "installed");
        string runner = Path.Combine(root, "update", "runner");
        Directory.CreateDirectory(Path.Combine(installed, "de"));
        try
        {
            File.WriteAllText(Path.Combine(installed, "MwbLite.exe"), "exe v1");
            File.WriteAllText(Path.Combine(installed, "MwbLite.dll"), "dll");
            File.WriteAllText(Path.Combine(installed, "de", "Strings.resources.dll"), "res");

            string exe = LiteUpdater.StageRunner(installed, runner);
            Assert.AreEqual(Path.Combine(runner, LiteUpdater.RunnerExeName), exe);
            Assert.AreEqual("exe v1", File.ReadAllText(exe));
            Assert.IsFalse(File.Exists(Path.Combine(runner, "MwbLite.exe")), "the installer stops MwbLite.exe by name, so the copy mustn't have it");
            Assert.IsTrue(File.Exists(Path.Combine(runner, "MwbLite.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(runner, "de", "Strings.resources.dll")));

            File.WriteAllText(Path.Combine(runner, "stale.txt"), "left from last time");
            File.WriteAllText(Path.Combine(installed, "MwbLite.exe"), "exe v2");
            exe = LiteUpdater.StageRunner(installed, runner);
            Assert.AreEqual("exe v2", File.ReadAllText(exe));
            Assert.IsFalse(File.Exists(Path.Combine(runner, "stale.txt")));

            File.Delete(Path.Combine(installed, "MwbLite.exe"));
            Assert.ThrowsExactly<FileNotFoundException>(() => LiteUpdater.StageRunner(installed, runner));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- the scheduled task ----------------------------------------------------------------------------------

    [TestMethod]
    public void TaskSecurityDescriptorIsValidAndGivesUsersReadAndExecuteOnly()
    {
        var sd = new RawSecurityDescriptor(LiteUpdateTask.SecurityDescriptor);
        Assert.AreEqual(3, sd.DiscretionaryAcl!.Count);
        const int genericRead = unchecked((int)0x80000000), genericExecute = 0x20000000, fileAllAccess = 0x1F01FF;
        var rights = new System.Collections.Generic.Dictionary<string, int>();
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            var allow = (CommonAce)ace;
            Assert.AreEqual(AceQualifier.AccessAllowed, allow.AceQualifier);
            rights[allow.SecurityIdentifier.Value] = allow.AccessMask;
        }

        Assert.AreEqual(fileAllAccess, rights["S-1-5-18"], "SYSTEM: full control");
        Assert.AreEqual(fileAllAccess, rights["S-1-5-32-544"], "Administrators: full control");
        Assert.AreEqual(genericRead | genericExecute, rights["S-1-5-11"], "Authenticated Users: read and execute only");
    }

    [TestMethod]
    public void LookingForTheTaskNeverThrows()
    {
        // Whether or not this PC has the task (installed with Setup), asking must not throw: callers fall back to UAC.
        _ = LiteUpdateTask.Exists();
        _ = LiteUpdateTask.IsRunning();
        _ = LiteUpdate.CanUpdateWithoutPrompt();
    }

    [TestMethod]
    public void TaskRunsTheInstalledExeAsSystemOnDemandOnly()
    {
        string xml;
        try
        {
            xml = LiteUpdateTask.DefinitionXml(@"C:\Program Files\MwbLite\MwbLite.exe");
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or System.IO.FileNotFoundException)
        {
            Assert.Inconclusive("The Task Scheduler isn't available here: " + e.Message);
            return;
        }

        StringAssert.Contains(xml, "<UserId>S-1-5-18</UserId>");
        StringAssert.Contains(xml, "<RunLevel>HighestAvailable</RunLevel>");
        StringAssert.Contains(xml, "<Command>C:\\Program Files\\MwbLite\\MwbLite.exe</Command>");
        StringAssert.Contains(xml, "<Arguments>--install-update</Arguments>");
        StringAssert.Contains(xml, "<ExecutionTimeLimit>PT30M</ExecutionTimeLimit>");
        StringAssert.Contains(xml, "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        StringAssert.Contains(xml, "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        StringAssert.Contains(xml, "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        Assert.IsFalse(xml.Contains("Trigger>", StringComparison.Ordinal), "no triggers: it only runs when MwbLite starts it");
    }
}
