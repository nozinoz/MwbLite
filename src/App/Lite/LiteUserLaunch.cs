// MwbLite: starts a program as the user signed in at the console, from a SYSTEM process in session 0 (the update task).
//
// Core/Launch.cs does this for the service-mode instances, but it takes the session of the calling process, which for a
// scheduled task is session 0 (no user). This asks Windows which session is at the console instead
// (WTSGetActiveConsoleSessionId), takes that user's token (WTSQueryUserToken) and starts the program on their desktop.
//
// For an administrator with UAC the token WTSQueryUserToken returns is the full (elevated) one. MwbLite is meant to
// run unelevated, like when it is started from the Start menu, so in that case the user's linked, limited token is used.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteUserLaunch
    {
        private const int TokenElevationType = 18;
        private const int TokenLinkedToken = 19;
        private const int ElevationTypeFull = 2;
        private const int TokenPrimary = 1;
        private const int SecurityImpersonation = 2;
        private const uint NoConsoleSession = 0xFFFFFFFF;

        /// <summary>
        /// Starts <paramref name="exe"/> (no arguments) on the console user's desktop, unelevated. Returns false, and
        /// logs why, if nobody is signed in at the console or the program can't be started.
        /// </summary>
        internal static bool StartAsConsoleUser(string exe)
        {
            IntPtr userToken = IntPtr.Zero, limited = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
            try
            {
                uint session = NativeMethods.WTSGetActiveConsoleSessionId();
                if (session == NoConsoleSession)
                {
                    LiteLog.Write(nameof(LiteUserLaunch), "Nobody is at the console, so " + Path.GetFileName(exe) + " isn't started.");
                    return false;
                }

                if (NativeMethods.WTSQueryUserToken(session, ref userToken) == 0)
                {
                    LiteLog.Write(nameof(LiteUserLaunch), $"No user is signed in on session {session} (error {Marshal.GetLastWin32Error()}), so {Path.GetFileName(exe)} isn't started.");
                    return false;
                }

                limited = LimitedToken(userToken);
                var attributes = new NativeMethods.SECURITY_ATTRIBUTES { Length = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>() };
                if (!DuplicateTokenEx(limited, NativeMethods.MAXIMUM_ALLOWED, ref attributes, SecurityImpersonation, TokenPrimary, out primary))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
                }

                int flags = NativeMethods.NORMAL_PRIORITY_CLASS | NativeMethods.CREATE_UNICODE_ENVIRONMENT;
                if (!CreateEnvironmentBlock(out env, primary, false))
                {
                    env = IntPtr.Zero;
                    flags &= ~NativeMethods.CREATE_UNICODE_ENVIRONMENT;
                }

                var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
                string commandLine = "\"" + exe + "\"";
                if (!CreateProcessAsUserW(primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, env, Path.GetDirectoryName(exe), ref si, out NativeMethods.PROCESS_INFORMATION pi))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");
                }

                _ = NativeMethods.CloseHandle(pi.hThread);
                _ = NativeMethods.CloseHandle(pi.hProcess);
                LiteLog.Write(nameof(LiteUserLaunch), $"Started {Path.GetFileName(exe)} for the user on session {session} (process {pi.dwProcessId}).");
                return true;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                LiteLog.Write(nameof(LiteUserLaunch), "Cannot start " + exe + ": " + e.Message);
                return false;
            }
            finally
            {
                if (env != IntPtr.Zero)
                {
                    _ = DestroyEnvironmentBlock(env);
                }

                foreach (IntPtr h in new[] { primary, limited == userToken ? IntPtr.Zero : limited, userToken })
                {
                    if (h != IntPtr.Zero)
                    {
                        _ = NativeMethods.CloseHandle(h);
                    }
                }
            }
        }

        // The token to start the program with: the user's own token, or for an elevated administrator the limited
        // token linked to it (a new handle the caller closes; the user's own token is closed separately).
        private static IntPtr LimitedToken(IntPtr userToken)
        {
            if (GetTokenInformation(userToken, TokenElevationType, out int type, sizeof(int), out _) && type == ElevationTypeFull &&
                GetTokenInformation(userToken, TokenLinkedToken, out IntPtr linked, IntPtr.Size, out _) && linked != IntPtr.Zero)
            {
                return linked;
            }

            return userToken;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass, out IntPtr information, int length, out int returned);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateTokenEx(IntPtr existing, uint access, ref NativeMethods.SECURITY_ATTRIBUTES attributes, int impersonationLevel, int tokenType, out IntPtr duplicate);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessAsUserW(IntPtr token, string applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, int creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out NativeMethods.PROCESS_INFORMATION processInformation);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    }
}
