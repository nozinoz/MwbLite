// MwbLite: whether the signed-in user's taskbar (and apps) use the light theme, so the tray glyph and the progress
// window match. A service-mode instance runs as SYSTEM, whose own HKCU says nothing about the user, so it reads the
// console user's hive (HKEY_USERS\<SID>) instead. Re-read at most every 30 seconds.
using System;
using System.Security.Principal;

using Microsoft.Win32;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Lite.Ui
{
    internal static class TaskbarTheme
    {
        private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

        private static (bool Taskbar, bool Apps) cached;
        private static DateTime readAt = DateTime.MinValue;

        static TaskbarTheme()
        {
            SystemEvents.UserPreferenceChanged += (_, _) => Invalidate(); // the user switched light/dark
        }

        /// <summary>Light taskbar (dark glyph). Windows 11's default is a dark taskbar.</summary>
        internal static bool IsLight => Read().Taskbar;

        /// <summary>Light app windows.</summary>
        internal static bool AppsLight => Read().Apps;

        internal static void Invalidate() => readAt = DateTime.MinValue;

        private static (bool Taskbar, bool Apps) Read()
        {
            if (DateTime.UtcNow - readAt < MaxAge)
            {
                return cached;
            }

            readAt = DateTime.UtcNow;
            try
            {
                using RegistryKey key = LitePaths.IsSystem
                    ? ConsoleUserSid() is string sid ? Registry.Users.OpenSubKey(sid + "\\" + Personalize) : null
                    : Registry.CurrentUser.OpenSubKey(Personalize);
                cached = (key?.GetValue("SystemUsesLightTheme") is int t && t == 1, key?.GetValue("AppsUseLightTheme") is not int a || a == 1);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                cached = (false, true);
            }

            return cached;
        }

        private static string ConsoleUserSid()
        {
            IntPtr token = IntPtr.Zero;
            if (NativeMethods.WTSQueryUserToken(NativeMethods.WTSGetActiveConsoleSessionId(), ref token) == 0 || token == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User?.Value;
            }
            finally
            {
                _ = NativeMethods.CloseHandle(token);
            }
        }
    }
}
