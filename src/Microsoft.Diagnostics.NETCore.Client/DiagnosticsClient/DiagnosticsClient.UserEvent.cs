// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Diagnostics.NETCore.Client
{
    public sealed partial class DiagnosticsClient
    {
        public void UserEventEnableProvider()
        {
            IpcMessage request = CreateUserEventEnableProviderMessage();
            IpcMessage response = IpcClient.SendMessage(_endpoint, request);
            ValidateResponseMessage(response, nameof(UserEventEnableProvider));
        }

        private static IpcMessage CreateUserEventEnableProviderMessage()
        {
            byte[] payload = SerializePayload("/tmp/user_events_socket");
            return new IpcMessage(DiagnosticsServerCommandSet.UserEvent, (byte)UserEventCommandId.EnableProvider, payload);
        }
        // private static IpcMessage CreateWriteDumpMessage(DumpType dumpType, string dumpPath, bool logDumpGeneration)
        // {
        //     if (string.IsNullOrEmpty(dumpPath))
        //     {
        //         throw new ArgumentNullException($"{nameof(dumpPath)} required");
        //     }

        //     byte[] payload = SerializePayload(dumpPath, (uint)dumpType, logDumpGeneration);
        //     return new IpcMessage(DiagnosticsServerCommandSet.Dump, (byte)DumpCommandId.GenerateCoreDump, payload);
        // }
    }
}
