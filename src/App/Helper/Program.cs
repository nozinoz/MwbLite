// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// MwbLite: removed the PowerToys GPO check, ETW telemetry trace, the PowerToys runner exit watcher
// and the legacy "SvcExec"/"install"/"help-ex" commands (they launched a MouseWithoutBorders.exe or a URL).
// The main process closes this helper on exit (Helper.RunDDHelper(true) / SharedConst.QUIT_CMD).
using System;
using System.Windows.Forms;

namespace MouseWithoutBorders
{
    internal static class Program
    {
        internal static FormHelper FormHelper;

        private static FormDot dotForm;

        internal static FormDot DotForm
        {
            get
            {
                return dotForm != null && !dotForm.IsDisposed ? dotForm : (dotForm = new FormDot());
            }
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        private static void Main()
        {
            // Always runs as the interactive user (started by the main process with the user's token).
            Lite.LiteLog.Initialize(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MwbLite", "logs"),
                "helper");

            string[] args = Environment.GetCommandLineArgs();

            if (args.Length > 1 && !string.IsNullOrEmpty(args[1]))
            {
                string command = args[1];
                string arg = args.Length > 2 && !string.IsNullOrEmpty(args[2]) ? args[2] : string.Empty;

                if (command.Equals("InternalError", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(arg, Application.ProductName);
                }

                return;
            }

            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.SetCompatibleTextRenderingDefault(false);

            dotForm = new FormDot();
            Application.Run(FormHelper = new FormHelper());
        }
    }
}
