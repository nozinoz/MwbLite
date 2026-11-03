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

        /// <summary>
        /// The primary screen's physical size in millimetres, as Windows reports it (from the monitor's EDID). False if
        /// Windows doesn't know it (some monitors and virtual displays report nothing, or a made-up size).
        /// </summary>
        internal static bool TryGetPrimaryScreenSizeMm(out int widthMm, out int heightMm)
        {
            const int HorzSize = 4, VertSize = 6;
            widthMm = heightMm = 0;
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                widthMm = GetDeviceCaps(dc, HorzSize);
                heightMm = GetDeviceCaps(dc, VertSize);
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, dc);
            }

            return widthMm > 0 && heightMm > 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int GetDeviceCaps(IntPtr dc, int index);
    }
}
