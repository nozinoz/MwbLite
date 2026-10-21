// MwbLite: registers/removes the MwbLite.Service Windows service. C# port of what the PowerToys MWB
// module interface (ModuleInterface/dllmain.cpp register_service/unregister_service) did, including the
// same service DACL (generateSecurityDescriptor.h) so the installing user can start it without elevation.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.ServiceProcess;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteServiceControl
    {
        private const uint ScManagerAllAccess = 0xF003F;
        private const uint ServiceAllAccess = 0xF01FF;
        private const uint ServiceWin32OwnProcess = 0x10;
        private const uint ServiceDemandStart = 3;
        private const uint ServiceErrorNormal = 1;
        private const uint ServiceNoChange = 0xFFFFFFFF;
        private const uint DaclSecurityInformation = 4;
        private const int ErrorServiceDoesNotExist = 1060;

        // Same as upstream generateSecurityDescriptor(): SYSTEM, Administrators, interactive/service/
        // power users can query; the installing user (last ACE) can start/stop the service.
        private static string SecurityDescriptor(string userSid) =>
            "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CR;;;AU)(A;;CCLCSWRPWPDTLOCRRC;;;PU)(A;;RPWPDTLO;;;"
            + userSid + ")";

        internal static void Register(string exePath, string userSid)
        {
            string binPath = "\"" + exePath + "\" --service";
            IntPtr scm = OpenSCManagerW(null, null, ScManagerAllAccess);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager");
            }

            try
            {
                IntPtr svc = OpenServiceW(scm, LitePaths.ServiceName, ServiceAllAccess);
                if (svc == IntPtr.Zero)
                {
                    svc = CreateServiceW(scm, LitePaths.ServiceName, "MwbLite Service", ServiceAllAccess, ServiceWin32OwnProcess, ServiceDemandStart, ServiceErrorNormal, binPath, null, IntPtr.Zero, null, null, null);
                    if (svc == IntPtr.Zero)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateService");
                    }

                    LiteLog.Write(nameof(LiteServiceControl), "Created service " + LitePaths.ServiceName + ": " + binPath);
                }
                else if (!ChangeServiceConfigW(svc, ServiceWin32OwnProcess, ServiceDemandStart, ServiceErrorNormal, binPath, null, IntPtr.Zero, null, null, null, "MwbLite Service"))
                {
                    int err = Marshal.GetLastWin32Error();
                    CloseServiceHandle(svc);
                    throw new Win32Exception(err, "ChangeServiceConfig");
                }
                else
                {
                    LiteLog.Write(nameof(LiteServiceControl), "Updated service " + LitePaths.ServiceName + ": " + binPath);
                }

                try
                {
                    var sd = new RawSecurityDescriptor(SecurityDescriptor(userSid));
                    byte[] binary = new byte[sd.BinaryLength];
                    sd.GetBinaryForm(binary, 0);
                    if (!SetServiceObjectSecurity(svc, DaclSecurityInformation, binary))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "SetServiceObjectSecurity");
                    }
                }
                finally
                {
                    CloseServiceHandle(svc);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        internal static void Stop()
        {
            try
            {
                using var sc = new ServiceController(LitePaths.ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException or Win32Exception)
            {
                LiteLog.Write(nameof(LiteServiceControl), "Stop: " + e.Message);
            }
        }

        internal static bool Unregister()
        {
            Stop();
            IntPtr scm = OpenSCManagerW(null, null, ScManagerAllAccess);
            if (scm == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager");
            }

            try
            {
                IntPtr svc = OpenServiceW(scm, LitePaths.ServiceName, ServiceAllAccess);
                if (svc == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ErrorServiceDoesNotExist)
                    {
                        return false;
                    }

                    throw new Win32Exception(err, "OpenService");
                }

                try
                {
                    if (!DeleteService(svc))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "DeleteService");
                    }

                    return true;
                }
                finally
                {
                    CloseServiceHandle(svc);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machineName, string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateServiceW(IntPtr scManager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType, uint errorControl, string binaryPathName, string loadOrderGroup, IntPtr tagId, string dependencies, string serviceStartName, string password);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl, string binaryPathName, string loadOrderGroup, IntPtr tagId, string dependencies, string serviceStartName, string password, string displayName);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetServiceObjectSecurity(IntPtr service, uint securityInformation, byte[] securityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
