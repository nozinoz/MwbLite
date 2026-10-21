// MwbLite: native helpers needed because MwbLite ships as a single-file executable.
using System;
using System.Runtime.InteropServices;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteNative
    {
        /// <summary>
        /// Module handle passed to SetWindowsHookEx for the low-level mouse/keyboard hooks.
        /// Upstream used Marshal.GetHINSTANCE(assembly module), which returns -1 when the assembly is
        /// bundled inside a single-file exe (hook install then fails with ERROR_MOD_NOT_FOUND, 126).
        /// Low-level hooks are not injected into other processes, so any module loaded in this process
        /// is valid; the executable's own module handle is the conventional choice.
        /// </summary>
        internal static IntPtr HookModuleHandle => GetModuleHandleW(null);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);
    }
}
