// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Server.Kestrel;

namespace Microsoft.AspNetCore.Hosting
{
    public static class KesterlServerOptionsSystemdExtensions
    {
        /// <summary>
        /// Open file descriptor (SD_LISTEN_FDS_START) initialized by systemd socket-based activation logic if available.
        /// </summary>
        /// <returns>
        /// The <see cref="KestrelServerOptions"/>.
        /// </returns>
        public static KestrelServerOptions UseSystemd(this KestrelServerOptions options)
        {
            return options.UseSystemd(_ => { });
        }

        /// <summary>
        /// Open file descriptor (SD_LISTEN_FDS_START) initialized by systemd socket-based activation logic if available.
        /// Specify callback to configure endpoint-specific settings.
        /// </summary>
        /// <returns>
        /// The <see cref="KestrelServerOptions"/>.
        /// </returns>
        public static KestrelServerOptions UseSystemd(this KestrelServerOptions options, Action<ListenOptions> configure)
        {
            if (string.Equals(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), Environment.GetEnvironmentVariable("LISTEN_PID"), StringComparison.Ordinal))
            {
                // SD_LISTEN_FDS_START = 3
                options.ListenHandle(3, configure);
            }

            return options;
        }
    }
}
