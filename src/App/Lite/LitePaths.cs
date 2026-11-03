// MwbLite: file locations. Replaces PowerToys' SettingsUtils / %LOCALAPPDATA%\Microsoft\PowerToys paths.
//
//  User instance (normal mode):   %LOCALAPPDATA%\MwbLite\{settings.json,state.json,logs\}
//  SYSTEM instances (service):    %ProgramData%\MwbLite\{settings.json,state.json,logs\}  (admin/SYSTEM-only ACL)
//
// The SYSTEM instances can drive the secure desktop (UAC prompts, lock screen), so they never read a
// file that the unelevated user can write. The user's settings.json is copied there by the elevated
// "--apply-config" / "--install" commands.
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MouseWithoutBorders.Lite
{
    internal static class LitePaths
    {
        internal const string AppName = "MwbLite";
        internal const string ServiceName = "MwbLite.Service";
        internal const string HelperExeName = "MwbLite.Helper.exe";
        internal const string SettingsFileName = "settings.json";

        private static readonly Lazy<bool> IsSystemLazy = new(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        });

        internal static bool IsSystem => IsSystemLazy.Value;

        internal static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

        internal static string ServiceDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

        internal static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

        internal static string ConfigDir => IsSystem ? ServiceDir : UserDir;

        internal static string SettingsFile => Path.Combine(ConfigDir, SettingsFileName);

        internal static string UserSettingsFile => Path.Combine(UserDir, SettingsFileName);

        internal static string ServiceSettingsFile => Path.Combine(ServiceDir, SettingsFileName);

        internal static string StateFile => Path.Combine(ConfigDir, "state.json");

        internal static string LogDir => Path.Combine(ConfigDir, "logs");

        /// <summary>
        /// Live connection/transfer status (no secrets) for the Settings window. The SYSTEM instance writes it in a
        /// subfolder users may read; the rest of %ProgramData%\MwbLite stays admin-only.
        /// </summary>
        internal static string StatusFile => IsSystem ? ServiceStatusFile : UserStatusFile;

        internal static string UserStatusFile => Path.Combine(UserDir, "status.json");

        internal static string ServiceStatusFile => Path.Combine(ServiceDir, "status", "status.json");

        internal static string ExePath => Environment.ProcessPath;

        internal static string ExeDir => Path.GetDirectoryName(Environment.ProcessPath);

        /// <summary>
        /// True when MwbLite was installed by MwbLite-Setup.exe / the MSI (which then owns the service,
        /// firewall rule and Program Files folder, instead of the "--install" command).
        /// </summary>
        internal static bool IsMsiInstalled
        {
            get
            {
                try
                {
                    using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).OpenSubKey(@"SOFTWARE\" + AppName);
                    return string.Equals(key?.GetValue("InstallSource") as string, "msi", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Creates (if needed) a directory that only SYSTEM and Administrators can access, and resets
        /// its ACL to exactly that (inheritance from %ProgramData% removed).
        /// </summary>
        internal static void EnsureProtectedDirectory(string path) => EnsureDirectory(path, usersCanRead: false);

        /// <summary>As <see cref="EnsureProtectedDirectory"/>, plus read access for Users (for the status file).</summary>
        internal static void EnsureReadableDirectory(string path) => EnsureDirectory(path, usersCanRead: true);

        /// <summary>
        /// For a folder the SYSTEM updater is about to use under %ProgramData%\MwbLite: first removes whatever an ordinary
        /// user could have planted there (see <see cref="RemoveUntrustedDirectory"/>), then
        /// <see cref="EnsureProtectedDirectory"/>. Without this, a junction or symbolic link made there beforehand
        /// would send SYSTEM's writes (the download, the staged program, the log) to a folder of the attacker's choice.
        /// </summary>
        internal static void EnsureTrustedDirectory(string path, bool requireTrustedOwner)
        {
            if (RemoveUntrustedDirectory(path, requireTrustedOwner))
            {
                LiteLog.Write(nameof(LitePaths), "Removed " + path + ", which was a link, a file, or not owned by SYSTEM or Administrators; it is created again.");
            }

            EnsureProtectedDirectory(path);
        }

        /// <summary>
        /// Removes <paramref name="path"/> if it is a reparse point (junction or symbolic link: the link itself goes, never
        /// the folder it points to), a file instead of a folder, or, with <paramref name="requireTrustedOwner"/>, a folder
        /// that isn't owned by SYSTEM or Administrators. Returns true if something was removed; false if the path is
        /// missing or fine as it is.
        /// </summary>
        internal static bool RemoveUntrustedDirectory(string path, bool requireTrustedOwner)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path); // describes a link itself, it doesn't follow it
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                File.Delete(path); // a file (or a file link) where the folder belongs
                return true;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path, recursive: false); // removes the link only
                return true;
            }

            if (requireTrustedOwner && !HasTrustedOwner(path))
            {
                Directory.Delete(path, recursive: true); // links inside are removed, not followed
                return true;
            }

            return false;
        }

        /// <summary>Removes a folder and what is in it; if <paramref name="path"/> is itself a link, only the link.</summary>
        internal static void DeleteDirectoryOrLink(string path)
        {
            if (!RemoveUntrustedDirectory(path, requireTrustedOwner: false) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        /// <summary>SYSTEM and the Administrators group are the owners a folder under %ProgramData%\MwbLite may have.</summary>
        internal static bool IsTrustedOwner(SecurityIdentifier owner) =>
            owner != null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

        private static bool HasTrustedOwner(string path)
        {
            try
            {
                var owner = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                return IsTrustedOwner(owner);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException)
            {
                return false; // can't tell who owns it: don't trust it
            }
        }

        private static void EnsureDirectory(string path, bool usersCanRead)
        {
            var security = new DirectorySecurity();
            if (usersCanRead)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
            {
                dir.Create(security);
            }

            dir.SetAccessControl(security);
        }
    }
}
