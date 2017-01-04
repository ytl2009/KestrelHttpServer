// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Adapter;

namespace Microsoft.AspNetCore.Server.Kestrel
{
    /// <summary>
    /// Describes either an <see cref="IPEndPoint"/>, Unix domain socket path, or a file descriptor for an already open
    /// socket that Kestrel should bind to or open.
    /// </summary>
    public class ListenOptions
    {
        private bool _noDelay = true;

        internal ListenOptions(IPEndPoint endPoint)
        {
            Type = ListenType.IPEndPoint;
            IPEndPoint = endPoint;
        }

        internal ListenOptions(string socketPath)
        {
            Type = ListenType.SocketPath;
            SocketPath = socketPath;
        }

        internal ListenOptions(long fileDescriptor)
        {
            Type = ListenType.FileDescriptor;
            FileDescriptor = fileDescriptor;
        }

        /// <summary>
        /// The type of interface being described: either an <see cref="IPEndPoint"/>, Unix domain socket path, or a file descriptor.
        /// </summary>
        public ListenType Type { get; }

        // IPEndPoint is mutable so port 0 can be updated to the bound port.
        public IPEndPoint IPEndPoint { get; internal set; }
        public string SocketPath { get; }
        public long FileDescriptor { get; }

        /// <summary>
        /// Enables and <see cref="IConnectionAdapter"/> to resolve and use services registered by the application during startup.
        /// Only set if accessed from the callback of a <see cref="KestrelServerOptions"/> Listen* method.
        /// </summary>
        public KestrelServerOptions KestrelServerOptions { get; internal set; }

        /// <summary>
        /// Set to false to enable Nagle's algorithm for all connections.
        /// </summary>
        /// <remarks>
        /// Defaults to true.
        /// </remarks>
        public bool NoDelay
        {
#pragma warning disable CS0618
            get { return _noDelay && (KestrelServerOptions?.NoDelay ?? true); }
#pragma warning restore CS06128
            set { _noDelay = value; }
        }

        /// <summary>
        /// Gets the <see cref="List{IConnectionAdapter}"/> that allows each connection <see cref="System.IO.Stream"/>
        /// to be intercepted and transformed.
        /// Configured by the <c>UseHttps()</c> and <see cref="Hosting.ListenOptionsConnectionLoggingExtensions.UseConnectionLogging(ListenOptions)"/>
        /// extension methods.
        /// </summary>
        /// <remarks>
        /// Defaults to empty.
        /// </remarks>
        public List<IConnectionAdapter> ConnectionAdapters { get; } = new List<IConnectionAdapter>();

        // PathBase and Scheme are hopefully only a temporary measure for back compat with IServerAddressesFeature.
        // This allows a ListenOptions to describe all the information encoded in IWebHostBuilder.UseUrls.
        internal string PathBase { get; set; }
        internal string Scheme { get; set; } = "http";

        public override string ToString()
        {
            // Use http scheme for all addresses. If https should be used for this endPoint,
            // it can still be configured for this endPoint specifically.
            switch (Type)
            {
                case ListenType.IPEndPoint:
                    return $"{Scheme}://{IPEndPoint}{PathBase}";
                case ListenType.SocketPath:
                    // ":" is used by ServerAddress to separate the socket path from PathBase.
                    return $"{Scheme}://unix:{SocketPath}:{PathBase}";
                case ListenType.FileDescriptor:
                    // This was never supported via --server.urls, so no need to include Scheme or PathBase.
                    return "http://<file handle>";
                default:
                    throw new InvalidOperationException();
            }
        }
    }
}
