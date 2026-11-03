// MwbLite: the "MwbLite Update" scheduled task, which lets the signed-in user install an update without a UAC prompt.
//
// Installing a new version needs administrator rights. Instead of asking for them every time, Setup registers an
// on-demand task (no triggers) that runs this MwbLite.exe with "--install-update" as SYSTEM. Its security descriptor
// lets only SYSTEM and Administrators change it, and any signed-in user start it. All the task can do is what
// --install-update does (LiteUpdater): install the newest release published on GitHub, checked against the SHA-256
// GitHub reports for it. It takes no input from whoever starts it.
//
// Setup (the MSI) creates the task on install and upgrade ("--register-update-task") and removes it on uninstall
// ("--unregister-update-task"). Copies that weren't installed with Setup (portable, "--install") have no task, and
// updating them asks for UAC as before.
//
// The Task Scheduler is used through its COM automation interface (Schedule.Service), late-bound, so no extra
// packages are needed. Every COM call can fail in several ways (service stopped, task disabled, no access), so the
// public methods log and return false rather than throw, except Register and Unregister, which the installer logs.
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteUpdateTask
    {
        internal const string TaskName = "MwbLite Update";

        /// <summary>The command line the task runs: the installed MwbLite.exe with this argument.</summary>
        internal const string Arguments = "--install-update";

        /// <summary>
        /// SYSTEM and Administrators: full control. Authenticated Users: read and execute, which is what starting the
        /// task takes, so the signed-in user's MwbLite can run it without elevation.
        /// </summary>
        internal const string SecurityDescriptor = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)";

        // Task Scheduler enumerations (taskschd.h).
        private const int TaskCreateOrUpdate = 6;
        private const int LogonServiceAccount = 5;
        private const int RunLevelHighest = 1;
        private const int ActionExec = 0;
        private const int InstancesIgnoreNew = 2;
        private const int StateQueued = 3;
        private const int StateRunning = 4;
        private const string LocalSystemSid = "S-1-5-18";

        private static dynamic Connect()
        {
            Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true);
            dynamic service = Activator.CreateInstance(type);
            service.Connect();
            return service;
        }

        /// <summary>A new task definition for <paramref name="exePath"/> (not registered).</summary>
        private static dynamic NewDefinition(dynamic service, string exePath)
        {
            dynamic def = service.NewTask(0);
            def.RegistrationInfo.Author = LitePaths.AppName;
            def.RegistrationInfo.Description = "Installs a new MwbLite version when MwbLite asks for it. Runs MwbLite.exe --install-update.";

            def.Principal.UserId = LocalSystemSid;
            def.Principal.LogonType = LogonServiceAccount;
            def.Principal.RunLevel = RunLevelHighest;

            def.Settings.Enabled = true;
            def.Settings.Hidden = true;
            def.Settings.AllowDemandStart = true;
            def.Settings.StartWhenAvailable = false;
            def.Settings.DisallowStartIfOnBatteries = false;
            def.Settings.StopIfGoingOnBatteries = false;
            def.Settings.MultipleInstances = InstancesIgnoreNew;
            def.Settings.ExecutionTimeLimit = "PT30M";

            dynamic action = def.Actions.Create(ActionExec);
            action.Path = exePath;
            action.Arguments = Arguments;
            action.WorkingDirectory = Path.GetDirectoryName(exePath);
            return def;
        }

        /// <summary>The XML of the definition Register would create, for tests and diagnostics. Registers nothing.</summary>
        internal static string DefinitionXml(string exePath)
        {
            dynamic service = Connect();
            dynamic def = NewDefinition(service, exePath);
            return (string)def.XmlText;
        }

        /// <summary>Creates or replaces the task (administrator).</summary>
        internal static void Register(string exePath)
        {
            dynamic service = Connect();
            dynamic folder = service.GetFolder("\\");
            dynamic def = NewDefinition(service, exePath);

            // userId "SYSTEM" with the service-account logon type needs no password.
            _ = folder.RegisterTaskDefinition(TaskName, def, TaskCreateOrUpdate, "SYSTEM", null, LogonServiceAccount, SecurityDescriptor);
            LiteLog.Write(nameof(LiteUpdateTask), $"Registered task \"{TaskName}\": {exePath} {Arguments}");
        }

        /// <summary>Deletes the task (administrator). Nothing to do if it doesn't exist.</summary>
        internal static void Unregister()
        {
            dynamic service = Connect();
            dynamic folder = service.GetFolder("\\");
            if (!Exists(folder))
            {
                return;
            }

            folder.DeleteTask(TaskName, 0);
            LiteLog.Write(nameof(LiteUpdateTask), $"Removed task \"{TaskName}\".");
        }

        private static bool Exists(dynamic folder)
        {
            try
            {
                _ = folder.GetTask(TaskName);
                return true;
            }
            catch (COMException)
            {
                return false; // 0x80070002: no such task
            }
        }

        /// <summary>True if the task is registered (and so updates need no UAC prompt).</summary>
        internal static bool Exists()
        {
            try
            {
                dynamic service = Connect();
                return Exists(service.GetFolder("\\"));
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(LiteUpdateTask), "Cannot look for the update task: " + e.Message);
                return false;
            }
        }

        /// <summary>Starts the task. False if it doesn't exist or can't be started (disabled, no access).</summary>
        internal static bool TryRun()
        {
            try
            {
                dynamic service = Connect();
                dynamic task = service.GetFolder("\\").GetTask(TaskName);
                _ = task.Run(null);
                LiteLog.Write(nameof(LiteUpdateTask), $"Started task \"{TaskName}\".");
                return true;
            }
            catch (Exception e)
            {
                // Late-bound COM reports its failures as several exception types; all mean "can't use the task".
                LiteLog.Write(nameof(LiteUpdateTask), $"Cannot start task \"{TaskName}\": {e.Message}");
                return false;
            }
        }

        /// <summary>True while the task is running (or about to).</summary>
        internal static bool IsRunning()
        {
            try
            {
                dynamic service = Connect();
                int state = service.GetFolder("\\").GetTask(TaskName).State;
                return state is StateQueued or StateRunning;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
