// MwbLite: "startWithWindows" = a per-user HKCU\...\Run entry pointing at the running MwbLite.exe.
// (Upstream relied on the PowerToys runner being started at logon.)
using System;

using Microsoft.Win32;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteStartup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal static void Sync(bool enabled, string exePath)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                string wanted = "\"" + exePath + "\"";
                string current = key.GetValue(LitePaths.AppName) as string;

                if (enabled && !string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(LitePaths.AppName, wanted, RegistryValueKind.String);
                    LiteLog.Write(nameof(LiteStartup), "Start with Windows enabled: " + wanted);
                }
                else if (!enabled && current != null)
                {
                    key.DeleteValue(LitePaths.AppName, throwOnMissingValue: false);
                    LiteLog.Write(nameof(LiteStartup), "Start with Windows disabled.");
                }
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteStartup), "Cannot update the Run key: " + e.Message);
            }
        }
    }
}
