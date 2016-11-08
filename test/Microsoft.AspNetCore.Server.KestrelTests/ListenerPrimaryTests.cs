// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Microsoft.AspNetCore.Server.KestrelTests
{
    public class ListenerPrimaryTests
    {
        [Fact]
        public async Task ListenerPrimarySkipsDeadDispatchPipes()
        {
            var libuv = new Libuv();

            var trace = new TestKestrelTrace();

            var serviceContextPrimary = new TestServiceContext
            {
                Log = trace,
                FrameFactory = context =>
                {
                    return new Frame<DefaultHttpContext>(new TestApplication(c =>
                    {
                        return c.Response.WriteAsync("Primary");
                    }), context);
                }
            };

            var serviceContextSecondary = new ServiceContext
            {
                Log = serviceContextPrimary.Log,
                AppLifetime = serviceContextPrimary.AppLifetime,
                DateHeaderValueManager = serviceContextPrimary.DateHeaderValueManager,
                ServerOptions = serviceContextPrimary.ServerOptions,
                ThreadPool = serviceContextPrimary.ThreadPool,
                FrameFactory = context =>
                {
                    return new Frame<DefaultHttpContext>(new TestApplication(c =>
                    {
                        return c.Response.WriteAsync("Secondary"); ;
                    }), context);
                }
            };

            using (var kestrelEngine = new KestrelEngine(libuv, serviceContextPrimary))
            {
                var address = ServerAddress.FromUrl("http://127.0.0.1:0/");
                var pipeName = (libuv.IsWindows ? @"\\.\pipe\kestrel_" : "/tmp/kestrel_") + Guid.NewGuid().ToString("n");

                var kestrelThreadPrimary = new KestrelThread(kestrelEngine);
                await kestrelThreadPrimary.StartAsync();

                var listenerPrimary = new TcpListenerPrimary(serviceContextPrimary);
                await listenerPrimary.StartAsync(pipeName, address, kestrelThreadPrimary);

                Console.WriteLine(address.ToString());

                // Until a secondary listener is added, connections get dispatched via the primarly listener.
                Assert.Equal("Primary", await HttpClientSlim.GetStringAsync(address.ToString()));
                Assert.Equal("Primary", await HttpClientSlim.GetStringAsync(address.ToString()));

                // Add secondary listener
                var kestrelThreadSecondary = new KestrelThread(kestrelEngine);
                await kestrelThreadSecondary.StartAsync();

                var listenerSecondary = new TcpListenerSecondary(serviceContextSecondary);
                await listenerSecondary.StartAsync(pipeName, address, kestrelThreadSecondary);

                // Once a secondary listener is added, connections start getting dispatched to it.
                Assert.Equal("Secondary", await HttpClientSlim.GetStringAsync(address.ToString()));

                // But connections will still get round-robined to the primary listener.
                Assert.Equal("Primary", await HttpClientSlim.GetStringAsync(address.ToString()));
                Assert.Equal("Secondary", await HttpClientSlim.GetStringAsync(address.ToString()));
                Assert.Equal("Primary", await HttpClientSlim.GetStringAsync(address.ToString()));

                await listenerSecondary.DisposeAsync();
                await kestrelThreadSecondary.StopAsync(TimeSpan.FromSeconds(1));

                // The next request will fail since it failed to dispatch to the secondary listener
                await Assert.ThrowsAsync<HttpRequestException>(() => HttpClientSlim.GetStringAsync(address.ToString()));
                //Assert.Equal("Primary", await HttpClientSlim.GetStringAsync(address.ToString()));

                // Once the secondary listener dies, it is removed from the dispatch queue.
                Assert.Equal("Primary", await HttpClientSlim.GetStringAsync(address.ToString()));
                Assert.Equal("Primary", await HttpClientSlim.GetStringAsync(address.ToString()));
                Assert.Equal("Primary", await HttpClientSlim.GetStringAsync(address.ToString()));

                await listenerPrimary.DisposeAsync();
                await kestrelThreadPrimary.StopAsync(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(1, trace.TestLogger.TotalErrorsLogged);
            var writeException = trace.TestLogger.Messages.First(m => m.LogLevel == LogLevel.Error).Exception;
            Assert.IsType<UvException>(writeException);
            Assert.Contains("EPIPE", writeException.Message);
        }

        private class TestApplication : IHttpApplication<DefaultHttpContext>
        {
            private readonly Func<DefaultHttpContext, Task> _app;

            public TestApplication(Func<DefaultHttpContext, Task> app)
            {
                _app = app;
            }

            public DefaultHttpContext CreateContext(IFeatureCollection contextFeatures)
            {
                return new DefaultHttpContext(contextFeatures);
            }

            public Task ProcessRequestAsync(DefaultHttpContext context)
            {
                return _app(context);
            }

            public void DisposeContext(DefaultHttpContext context, Exception exception)
            {
            }
        }
    }
}
