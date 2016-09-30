// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Filter;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;

namespace Microsoft.AspNetCore.Server.Kestrel
{
    /// <summary>
    /// Provides programmatic configuration of Kestrel-specific features.
    /// </summary>
    public class KestrelServerOptions
    {
        private List<ListenDescriptor> Endpoints { get; } = new List<ListenDescriptor>();
        internal string ServerAddresses => Endpoints.Select(e => e.ToServerAddress());

        /// <summary>
        /// Gets or sets whether the <c>Server</c> header should be included in each response.
        /// </summary>
        /// <remarks>
        /// Defaults to true.
        /// </remarks>
        public bool AddServerHeader { get; set; } = true;

        /// <summary>
        /// Enables the UseKestrel options callback to resolve and use services registered by the application during startup.
        /// Typically initialized by <see cref="Hosting.WebHostBuilderKestrelExtensions.UseKestrel(Hosting.IWebHostBuilder, Action{KestrelServerOptions})"/>.
        /// </summary>
        public IServiceProvider ApplicationServices { get; set; }

        /// <summary>
        /// Gets or sets an <see cref="IConnectionFilter"/> that allows each connection <see cref="System.IO.Stream"/>
        /// to be intercepted and transformed.
        /// Configured by the <c>UseHttps()</c> and <see cref="Hosting.KestrelServerOptionsConnectionLoggingExtensions.UseConnectionLogging(KestrelServerOptions)"/>
        /// extension methods.
        /// </summary>
        /// <remarks>
        /// Defaults to null.
        /// </remarks>
        public IConnectionFilter ConnectionFilter { get; set; }

        /// <summary>
        /// <para>
        /// This property is obsolete and will be removed in a future version.
        /// Use <c>Limits.MaxRequestBufferSize</c> instead.
        /// </para>
        /// <para>
        /// Gets or sets the maximum size of the request buffer.
        /// </para>
        /// </summary>
        /// <remarks>
        /// When set to null, the size of the request buffer is unlimited.
        /// Defaults to 1,048,576 bytes (1 MB).
        /// </remarks>
        [Obsolete("This property is obsolete and will be removed in a future version. Use Limits.MaxRequestBufferSize instead.")]
        public long? MaxRequestBufferSize
        {
            get
            {
                return Limits.MaxRequestBufferSize;
            }
            set
            {
                Limits.MaxRequestBufferSize = value;
            }
        }

        /// <summary>
        /// Provides access to request limit options.
        /// </summary>
        public KestrelServerLimits Limits { get; } = new KestrelServerLimits();

        /// <summary>
        /// Set to false to enable Nagle's algorithm for all connections.
        /// </summary>
        /// <remarks>
        /// Defaults to true.
        /// </remarks>
        public bool NoDelay { get; set; } = true;

        /// <summary>
        /// The amount of time after the server begins shutting down before connections will be forcefully closed.
        /// Kestrel will wait for the duration of the timeout for any ongoing request processing to complete before
        /// terminating the connection. No new connections or requests will be accepted during this time.
        /// </summary>
        /// <remarks>
        /// Defaults to 5 seconds.
        /// </remarks>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The number of libuv I/O threads used to process requests.
        /// </summary>
        /// <remarks>
        /// Defaults to half of <see cref="Environment.ProcessorCount" /> rounded down and clamped between 1 and 16.
        /// </remarks>
        public int ThreadCount { get; set; } = ProcessorThreadCount;

        private static int ProcessorThreadCount
        {
            get
            {
                // Actual core count would be a better number
                // rather than logical cores which includes hyper-threaded cores.
                // Divide by 2 for hyper-threading, and good defaults (still need threads to do webserving).
                var threadCount = Environment.ProcessorCount >> 1;

                if (threadCount < 1)
                {
                    // Ensure shifted value is at least one
                    return 1;
                }

                if (threadCount > 16)
                {
                    // Receive Side Scaling RSS Processor count currently maxes out at 16
                    // would be better to check the NIC's current hardware queues; but xplat...
                    return 16;
                }

                return threadCount;
            }
        }

        public void Listen(IPAddress address, int port)
        {
            Listen(address, port, _ => { });
        }

        public void Listen(IPAddress address, int port, Action<ListenOptions> configure)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }
            if (port < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            var descriptor = new ListenDescriptor
            {
                Type = ListenType.IPAddress,
                IPAddress = address,
            };

            configure(descriptor.ListenOptions);
            Endpoints.Add(descriptor);
        }

        public void ListenUnixSocket(string socketPath)
        {
            ListenUnixSocket(socketPath, _ => { });
        }

        public void ListenUnixSocket(string socketPath, Action<ListenOptions> configure)
        {
            if (socketPath == null)
            {
                throw new ArgumentNullException(nameof(socketPath));
            }
            if (socketPath.Length == 0 || socketPath[0] != '/')
            {
                throw new ArgumentException("Unix socket path must be absolute.", nameof(socketPath));
            }
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var descriptor = new ListenDescriptor
            {
                Type = ListenType.SocketPath,
                SocketPath = socketPath
            };

            configure(descriptor.ListenOptions);
            Endpoints.Add(descriptor);
        }

        public void ListenPipeHandle(long pipeHandle)
        {
            ListenPipeHandle(pipeHandle, _ => { });
        }

        public void ListenPipeHandle(long pipeHandle, Action<ListenOptions> configure)
        {
            if (pipeHandle < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pipeHandle));
            }
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var descriptor = new ListenDescriptor
            {
                Type = ListenType.PipeHandle,
                FileHandle = pipeHandle
            };

            configure(descriptor.ListenOptions);
            Endpoints.Add(descriptor);
        }

        private enum ListenType
        {
            IPAddress,
            SocketPath,
            PipeHandle,
        }

        private class ListenDescriptor
        {
            public ListenType Type { get; set; }

            public IPAddress IPAddress { get; set; }

            public int Port { get; set; }

            public string SocketPath { get; set; }

            public long FileHandle { get; set; }

            public ListenOptions ListenOptions { get; } = new ListenOptions();

            public string ToServerAddress()
            {
                // Use http scheme for all addresses. If https should be used for this endpoint,
                // it can still be configured for this endpoint specifically.
                switch (Type)
                {
                    case ListenType.IPAddress:
                        return $"http://{IPAddress}:{Port}";
                    case ListenType.SocketPath:
                        return $"http://unix:{SocketPath}";
                    case ListenType.PipeHandle:
                        return $"http://{Constants.PipeDescriptorPrefix}{FileHandle.ToString(CultureInfo.InvariantCulture)}";
                    default:
                        throw new InvalidOperationException();
                }
            }
        }
    }
}
