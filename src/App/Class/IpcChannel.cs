// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Core;
using StreamJsonRpc;

namespace MouseWithoutBorders
{
    internal sealed class IpcChannel<T>
        where T : new()
    {
        public static T StartIpcServer(string pipeName, CancellationToken cancellationToken)
        {
            SecurityIdentifier securityIdentifier = new SecurityIdentifier(
WellKnownSidType.AuthenticatedUserSid, null);

            PipeSecurity pipeSecurity = new PipeSecurity();
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                securityIdentifier,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            using (var serverChannel = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity))
                            {
                                await serverChannel.WaitForConnectionAsync();
                                var taskRpc = JsonRpc.Attach(serverChannel, new T());
                                await taskRpc.Completion;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception e)
                    {
#if MM_HELPER
                        _ = e;
#else
                        Logger.Log(e);
#endif
                    }
                },
                cancellationToken);

            return default(T);
        }

        // MwbLite: StartVerifiedIpcServer (PowerToys Settings IPC v2, RestrictedNamedPipeServer) removed;
        // there is no Settings app to talk to. Only the main<->helper clipboard pipe remains.
    }
}
