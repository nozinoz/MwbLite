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

        internal static string ExePath => Environment.ProcessPath;

        internal static string ExeDir => Path.GetDirectoryName(Environment.ProcessPath);

        /// <summary>
        /// Creates (if needed) a directory that only SYSTEM and Administrators can access, and resets
        /// its ACL to exactly that (inheritance from %ProgramData% removed).
        /// </summary>
        internal static void EnsureProtectedDirectory(string path)
        {
            var security = new DirectorySecurity();
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
