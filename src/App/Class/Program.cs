// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// <summary>
//     Application entry and pre-process/initialization.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
//     MwbLite: removed the PowerToys logger/ETW trace, Group Policy checks, the PowerToys runner exit
//              watcher, the Command Palette event listener, the PowerToys Settings IPC server and
//              telemetry. Added "--" commands (service host, install, apply-config...), see LiteCommands.
// </history>
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

using MouseWithoutBorders.Core;
using MouseWithoutBorders.Lite;

using Logger = MouseWithoutBorders.Core.Logger;
using Thread = MouseWithoutBorders.Core.Thread;

[module: SuppressMessage("Microsoft.MSInternal", "CA904:DeclareTypesInMicrosoftOrSystemNamespace", Scope = "namespace", Target = "MouseWithoutBorders", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1014:MarkAssembliesWithClsCompliant", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Globalization", "CA1304:SpecifyCultureInfo", Scope = "member", Target = "MouseWithoutBorders.Program.#Main()", MessageId = "System.String.ToLower", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Globalization", "CA1300:SpecifyMessageBoxOptions", Scope = "member", Target = "MouseWithoutBorders.Program.#Main()", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders.Class
{
    internal static class Program
    {
        private static readonly string ServiceName = LitePaths.ServiceName;

        private static readonly string ServiceModeArg = "UseService";

        public static bool ShowServiceModeErrorTooltip;

        [STAThread]
        private static void Main()
        {
            string[] args = Environment.GetCommandLineArgs();

            // MwbLite commands: --service, --install, --uninstall, --apply-config, --open-config, --help.
            if (args.Length > 1 && args[1] != null && args[1].StartsWith("--", StringComparison.Ordinal))
            {
                Environment.ExitCode = LiteCommands.Run(args);
                return;
            }

            WindowsIdentity currentUser = WindowsIdentity.GetCurrent();
            SecurityIdentifier currentUserSID = currentUser.User;
            SecurityIdentifier localSystemSID = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            bool runningAsSystem = currentUserSID.Equals(localSystemSID);

            if (runningAsSystem)
            {
                // SYSTEM instances only use %ProgramData%\MwbLite (SYSTEM/Administrators only).
                LitePaths.EnsureProtectedDirectory(LitePaths.ServiceDir);
            }

            LiteLog.Initialize(LitePaths.LogDir, runningAsSystem ? "mwblite-system" : "mwblite");
            Logger.Log(Application.ProductName + " Started!");

            Thread.CurrentThread.Name = Application.ProductName + " main thread";
            Common.BinaryName = Path.GetFileNameWithoutExtension(Application.ExecutablePath);

            Common.RunWithNoAdminRight = !runningAsSystem;
            try
            {
                string firstArg = string.Empty;
                if (args.Length > 1 && args[1] != null)
                {
                    firstArg = args[1].Trim();
                }

                User = WindowsIdentity.GetCurrent().Name;
                Logger.LogDebug("*** Started as " + User);

                Logger.Log(Environment.CommandLine);

                // Upstream got "UseService" from the PowerToys runner; MwbLite reads it from settings.json.
                bool serviceMode = firstArg == ServiceModeArg || (firstArg.Length == 0 && Setting.Values.UseService);

                if (!runningAsSystem)
                {
                    LiteStartup.Sync(Setting.Values.StartWithWindows, Application.ExecutablePath);
                }

                // If we're started from the .dll module or from the service process, we should
                // assume the service mode.
                if (serviceMode && !runningAsSystem)
                {
                    try
                    {
                        var sc = new ServiceController(ServiceName);
                        sc.Start();
                        Logger.Log("Started " + ServiceName + "; exiting the user instance.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("Couldn't start the service. Will try to continue as not a service.");
                        Logger.Log(ex);
                        ShowServiceModeErrorTooltip = true;
                        serviceMode = false;
                        Setting.Values.UseService = false;
                    }
                }

                if (firstArg != string.Empty)
                {
                    if (MachineStuff.CheckSecondInstance(Common.RunWithNoAdminRight))
                    {
                        Logger.Log("*** Second instance, exiting...");
                        return;
                    }

                    string myDesktop = WinAPI.GetMyDesktop();

                    if (firstArg.Equals("winlogon", StringComparison.OrdinalIgnoreCase))
                    {
                        // Executed by service, running on logon desktop
                        Logger.Log("*** Running on " + firstArg + " desktop");
                        Common.RunOnLogonDesktop = true;
                    }
                    else if (args[1].Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log("*** Running on " + firstArg + " desktop");
                    }
                    else if (args[1].Equals(myDesktop, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log("*** Running on " + myDesktop);
                        if (myDesktop.Equals("Screen-saver", StringComparison.OrdinalIgnoreCase))
                        {
                            Common.RunOnScrSaverDesktop = true;
                            Setting.Values.LastX = Common.JUST_GOT_BACK_FROM_SCREEN_SAVER;
                        }
                    }
                }
                else
                {
                    if (MachineStuff.CheckSecondInstance(true))
                    {
                        Logger.Log("*** Second instance, exiting...");
                        return;
                    }
                }

                try
                {
                    Common.CurrentProcess = Process.GetCurrentProcess();
                    Common.CurrentProcess.PriorityClass = ProcessPriorityClass.RealTime;
                }
                catch (Exception e)
                {
                    Logger.Log(e);
                }

                Logger.Log(Environment.OSVersion.ToString());

                // Environment.OSVersion is unreliable from 6.2 and up, so just forcefully call the APIs and log the exception unsupported by Windows:
                int setProcessDpiAwarenessResult = -1;

                try
                {
                    setProcessDpiAwarenessResult = NativeMethods.SetProcessDpiAwareness(2);
                    Logger.Log(string.Format(CultureInfo.InvariantCulture, "SetProcessDpiAwareness: {0}.", setProcessDpiAwarenessResult));
                }
                catch (DllNotFoundException)
                {
                    Logger.Log("SetProcessDpiAwareness is unsupported in Windows 7 and lower.");
                }
                catch (EntryPointNotFoundException)
                {
                    Logger.Log("SetProcessDpiAwareness is unsupported in Windows 7 and lower.");
                }
                catch (Exception e)
                {
                    Logger.Log(e);
                }

                try
                {
                    if (setProcessDpiAwarenessResult != 0)
                    {
                        Logger.Log(string.Format(CultureInfo.InvariantCulture, "SetProcessDPIAware: {0}.", NativeMethods.SetProcessDPIAware()));
                    }
                }
                catch (Exception e)
                {
                    Logger.Log(e);
                }

                System.Threading.Thread mainUIThread = Thread.CurrentThread;
                Common.UIThreadID = mainUIThread.ManagedThreadId;
                Thread.UpdateThreads(mainUIThread);

                StartInputCallbackThread();

                Application.EnableVisualStyles();
                _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.SetCompatibleTextRenderingDefault(false);

                InitAndCleanup.Init();

                if (runningAsSystem)
                {
                    // settings.json here only changes through the elevated --apply-config command.
                    LiteTray.WatchServiceSettings();
                }

                Core.Helper.WndProcCounter++;

                var formScreen = new FrmScreen();

                Application.Run(formScreen);
            }
            catch (Exception e)
            {
                Logger.Log(e);
            }
        }

        internal static void StartInputCallbackThread()
        {
            Thread inputCallback = new(new ThreadStart(InputCallbackThread), "InputCallback Thread");
            inputCallback.SetApartmentState(ApartmentState.STA);
            inputCallback.Priority = ThreadPriority.Highest;
            inputCallback.Start();
        }

        private static void InputCallbackThread()
        {
            // SuppressFlow fixes an issue on service mode, where the helper process can't get enough permissions to be started again.
            // More details can be found on: https://github.com/microsoft/PowerToys/pull/36892
            using var asyncFlowControl = ExecutionContext.SuppressFlow();

            Common.InputCallbackThreadID = Thread.CurrentThread.ManagedThreadId;
            while (!InitAndCleanup.InitDone)
            {
                Thread.Sleep(100);
            }

            Application.Run(new FrmInputCallback());
        }

        internal static void StartService()
        {
            if (Common.RunWithNoAdminRight)
            {
                return;
            }

            try
            {
                // Kill all but me
                Process me = Process.GetCurrentProcess();
                Process[] ps = Process.GetProcessesByName(Common.BinaryName);
                foreach (Process pp in ps)
                {
                    if (pp.Id != me.Id)
                    {
                        Logger.Log(string.Format(CultureInfo.InvariantCulture, "Killing process {0}.", pp.Id));
                        pp.KillProcess();
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log(e);
            }

            Service.StartMouseWithoutBordersService();
        }

        internal static string User { get; set; }
    }
}
