// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Diagnostics.Tracing;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;

namespace Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure
{
    [EventSource(Name = "Microsoft-AspNetCore-Server-Kestrel")]
    public sealed class KestrelEventSource : EventSource
    {
        public static readonly KestrelEventSource Log = new KestrelEventSource();

        private KestrelEventSource()
        {
        }

        [NonEvent]
        public void ConnectionStart(Connection connection)
        {
            if (IsEnabled())
            {
                ConnectionStart(
                    connection.ConnectionId,
                    connection.ListenerContext.ServerAddress.ToString(),
                    connection.RemoteEndPoint.ToString(),
                    connection.LocalEndPoint.ToString());
            }
        }

        [Event(1, Level = EventLevel.Informational)]
        private void ConnectionStart(string connectionId,
            string serverAddress,
            string remoteEndPoint,
            string localEndPoint)
        {
            WriteEvent(
                1,
                connectionId,
                serverAddress,
                remoteEndPoint,
                localEndPoint
            );
        }
    }
}