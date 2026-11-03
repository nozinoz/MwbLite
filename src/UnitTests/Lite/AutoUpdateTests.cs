// MwbLite: tests for updating without a UAC prompt, automatically, and together with the other PCs: the decision
// functions, rate limit, the staged copy of the updater, the file check before running Setup, and the shape of the
// scheduled task (built in memory only; nothing is registered).
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

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
    public void IncomingAnnouncementsAreAcceptedOncePerHourWhateverTheVersion()
    {
        var received = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromHours(1));
        var tried = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromHours(24));
        DateTime t = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        bool Accept(Version announced, DateTime at, bool enabled = true) => LiteAutoUpdate.ShouldAcceptAnnouncement(enabled, announced, V090, received, tried, at);

        // Announcements that would be ignored anyway don't use up the hour's slot.
        Assert.IsFalse(Accept(V091, t, enabled: false), "switch off");
        Assert.IsFalse(Accept(V080, t), "older");
        Assert.IsFalse(Accept(V090, t), "same version");
        Assert.IsFalse(Accept(null!, t), "unreadable");

        Assert.IsTrue(Accept(V091, t));
        Assert.IsFalse(Accept(V091, t.AddMinutes(1)), "the same version again");
        Assert.IsFalse(Accept(new Version(0, 9, 2), t.AddMinutes(2)), "another version doesn't get its own slot");
        Assert.IsFalse(Accept(new Version(0, 9, 3), t.AddMinutes(59)));
        Assert.IsTrue(Accept(new Version(0, 9, 2), t.AddMinutes(61)), "an hour later");
    }

    [TestMethod]
    public void AVersionWhoseUpdateWasTriedIsNotAcceptedAgainForADay()
    {
        var received = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromHours(1));
        var tried = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromHours(24));
        DateTime t = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

        tried.Record("0.9.1", t);

        Assert.IsFalse(LiteAutoUpdate.ShouldAcceptAnnouncement(true, V091, V090, received, tried, t.AddHours(2)), "failed 2 hours ago");
        Assert.IsTrue(LiteAutoUpdate.ShouldAcceptAnnouncement(true, new Version(0, 9, 2), V090, received, tried, t.AddHours(2)), "another version is separate, and the refused one used no slot");
        Assert.IsFalse(LiteAutoUpdate.ShouldAcceptAnnouncement(true, V091, V090, received, tried, t.AddHours(23)));
        Assert.IsTrue(LiteAutoUpdate.ShouldAcceptAnnouncement(true, V091, V090, received, tried, t.AddHours(25)), "a day later it may be tried again");
    }

    [TestMethod]
    public void OurAnnouncementUsesTheHourOnlyWhenThePcAnswered()
    {
        var answered = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromHours(1));
        var attempts = new LiteAutoUpdate.OncePerWindow(TimeSpan.FromMinutes(5));
        DateTime t = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        const string key = "LAPTOP|0.9.1";

        Assert.IsTrue(LiteAutoUpdate.ShouldSendAnnouncement(answered, attempts, key, t));
        // No answer: nothing is recorded as answered. It isn't hammered, but it is tried again within minutes.
        Assert.IsFalse(LiteAutoUpdate.ShouldSendAnnouncement(answered, attempts, key, t.AddMinutes(1)));
        Assert.IsTrue(LiteAutoUpdate.ShouldSendAnnouncement(answered, attempts, key, t.AddMinutes(6)));

        answered.Record(key, t.AddMinutes(6)); // this time it replied
        Assert.IsFalse(LiteAutoUpdate.ShouldSendAnnouncement(answered, attempts, key, t.AddMinutes(7)));
        Assert.IsFalse(LiteAutoUpdate.ShouldSendAnnouncement(answered, attempts, key, t.AddMinutes(30)), "answered: not again within the hour");
        Assert.IsTrue(LiteAutoUpdate.ShouldSendAnnouncement(answered, attempts, key, t.AddMinutes(68)));
        Assert.IsTrue(LiteAutoUpdate.ShouldSendAnnouncement(answered, attempts, "OFFICE|0.9.1", t.AddMinutes(7)), "other PCs are separate");
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

    [TestMethod]
    public void OnlyOneUpdateHoldsTheLockFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (FileStream first = LiteUpdater.TryLock(root)!)
            {
                Assert.IsNotNull(first);
                Assert.IsNull(LiteUpdater.TryLock(root), "a second update must not start while the first holds the lock");
            }

            using FileStream again = LiteUpdater.TryLock(root)!;
            Assert.IsNotNull(again, "free again once the first update is over");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.IsFalse(Directory.Exists(root));
    }

    // ---- links and foreign folders in %ProgramData%\MwbLite ---------------------------------------------------

    // Makes a directory junction (no administrator rights needed). Skips the test where that isn't possible.
    private static void MakeJunction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(info)!;
        _ = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 || !Directory.Exists(link))
        {
            Assert.Inconclusive("Cannot create a junction here.");
        }
    }

    [TestMethod]
    public void ALinkWhereAFolderBelongsIsRemovedWithoutTouchingWhatItPointsTo()
    {
        string root = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "elsewhere");
        string link = Path.Combine(root, "update");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "do not delete");
        try
        {
            MakeJunction(link, target);

            Assert.IsTrue(LitePaths.RemoveUntrustedDirectory(link, requireTrustedOwner: false));

            Assert.IsFalse(Directory.Exists(link));
            Assert.IsTrue(File.Exists(Path.Combine(target, "precious.txt")), "the target and its files stay");

            // Same through the runner folder's own path: a link is removed, not followed, when the staged copy is made.
            string installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "MwbLite.exe"), "exe");
            string runner = Path.Combine(root, "runner");
            MakeJunction(runner, target);

            string exe = LiteUpdater.StageRunner(installed, runner);

            Assert.IsTrue(File.Exists(exe));
            Assert.AreEqual(runner, Path.GetDirectoryName(exe));
            Assert.IsFalse(new DirectoryInfo(runner).Attributes.HasFlag(FileAttributes.ReparsePoint), "now a real folder");
            Assert.IsTrue(File.Exists(Path.Combine(target, "precious.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(target, LiteUpdater.RunnerExeName)), "nothing was written through the link");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void AFileWhereAFolderBelongsIsRemovedAndMissingOrOrdinaryFoldersAreLeftAlone()
    {
        string root = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.IsFalse(LitePaths.RemoveUntrustedDirectory(Path.Combine(root, "missing"), requireTrustedOwner: true));

            string file = Path.Combine(root, "update");
            File.WriteAllText(file, "not a folder");
            Assert.IsTrue(LitePaths.RemoveUntrustedDirectory(file, requireTrustedOwner: false));
            Assert.IsFalse(File.Exists(file));

            string folder = Path.Combine(root, "plain");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "keep.txt"), "x");
            Assert.IsFalse(LitePaths.RemoveUntrustedDirectory(folder, requireTrustedOwner: false), "an ordinary folder is fine when the owner doesn't matter");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OnlySystemAndAdministratorsMayOwnTheFolders()
    {
        Assert.IsTrue(LitePaths.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.IsTrue(LitePaths.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.IsFalse(LitePaths.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.IsFalse(LitePaths.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
        Assert.IsFalse(LitePaths.IsTrustedOwner(WindowsIdentity.GetCurrent().User) && !WindowsIdentity.GetCurrent().IsSystem, "an ordinary account's own SID");
        Assert.IsFalse(LitePaths.IsTrustedOwner(null!));

        // A folder owned by whoever runs this test: removed if that isn't SYSTEM or Administrators, kept if it is.
        string folder = Path.Combine(Path.GetTempPath(), "MwbLiteTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var owner = (SecurityIdentifier)new DirectoryInfo(folder).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;

            bool removed = LitePaths.RemoveUntrustedDirectory(folder, requireTrustedOwner: true);

            Assert.AreEqual(!LitePaths.IsTrustedOwner(owner), removed);
            Assert.AreEqual(removed, !Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
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
        Assert.AreEqual(genericRead | genericExecute, rights["S-1-5-4"], "Interactive users: read and execute only");
        Assert.IsFalse(rights.ContainsKey("S-1-5-11"), "not every authenticated (e.g. network) logon");
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
