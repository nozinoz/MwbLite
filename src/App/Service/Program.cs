// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// MwbLite: compiled into MwbLite.exe and entered through "MwbLite.exe --service" (was the separate
// PowerToys.MouseWithoutBordersService.exe). Removed the Group Policy checks; service name is
// "MwbLite.Service"; logging goes to the MwbLite log file instead of the Windows Event Log.
using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MouseWithoutBordersService
{
    public delegate void StopService();

#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name
    public static class CmdArgs
#pragma warning restore SA1649 // File name should match first type name
#pragma warning restore SA1402 // File may only contain a single type
    {
        public static string[] Value { get; set; }

        public static StopService StopServiceDelegate { get; set; }
    }

    internal sealed class Program
    {
        /// <param name="args">Full command line; args[1] is "--service".</param>
        internal static void RunService(string[] args)
        {
            // Worker reads args[1] as the user's local app data path (unused by MwbLite); drop "--service".
            CmdArgs.Value = args.Take(1).Concat(args.Skip(2)).ToArray();
            var builder = Host.CreateDefaultBuilder();

            var host = builder
            .UseWindowsService(options =>
            {
                options.ServiceName = MouseWithoutBorders.Lite.LitePaths.ServiceName;
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddHostedService<Worker>();
            })
            .Build();

            // These warning are disabled because StreamJsonRpc brings in thread analyzers to the project. Enable warnings once we adopt thread analyzers globally.
#pragma warning disable VSTHRD101 // Avoid unsupported async delegates
#pragma warning disable VSTHRD110 // Observe result of async calls
            CmdArgs.StopServiceDelegate = async () => { await host.StopAsync(); };
            host.Run();
            host.StopAsync();
#pragma warning restore VSTHRD110 // Observe result of async calls
#pragma warning restore VSTHRD101 // Avoid unsupported async delegates
        }
    }
}
