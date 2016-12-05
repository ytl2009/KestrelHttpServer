// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Server.Kestrel.FunctionalTests
{
    public static class TaskExtensions
    {
        public static async Task OrTimeout(this Task task, TimeSpan timeout, 
            [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
        {
            var finished = await Task.WhenAny(task, Task.Delay(timeout));
            if (!ReferenceEquals(finished, task))
            {
                throw new TimeoutException($"Task exceeded max running time of {timeout.TotalSeconds}s at {file}:{line}");
            }
        }
    }
}
