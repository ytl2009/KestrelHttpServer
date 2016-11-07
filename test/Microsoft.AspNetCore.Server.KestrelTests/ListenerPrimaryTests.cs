// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Internal;
using Xunit;

namespace Microsoft.AspNetCore.Server.KestrelTests
{
    public class ListenerPrimaryTests
    {
        [Fact]
        public async Task ListenerPrimarySkipsDeadDispatchPipes()
        {
            var libuv = new Libuv();
            var primaryDispatch = new SemaphoreSlim(0, 1);
            var secondaryDispatch = new SemaphoreSlim(0, 1);

            var serviceContextPrimary = new TestServiceContext
            {
                FrameFactory = context =>
                {
                    return new Frame<DefaultHttpContext>(new TestApplication(_ =>
                    {
                        primaryDispatch.Release();
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
                    return new Frame<DefaultHttpContext>(new TestApplication(_ =>
                    {
                        secondaryDispatch.Release();
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

                // Until a secondary listener is added, connections get dispatched via the primarly listener.
                await HttpClientSlim.GetStringAsync(address.ToString());
                Assert.True(await primaryDispatch.WaitAsync(1000));
                await HttpClientSlim.GetStringAsync(address.ToString());
                Assert.True(await primaryDispatch.WaitAsync(1000));

                // Add secondary listener
                var kestrelThreadSecondary = new KestrelThread(kestrelEngine);
                await kestrelThreadSecondary.StartAsync();

                var listenerSecondary = new TcpListenerSecondary(serviceContextSecondary);
                await listenerSecondary.StartAsync(pipeName, address, kestrelThreadSecondary);

                // Once a secondary listener is added, connections start getting dispatched to it.
                await HttpClientSlim.GetStringAsync(address.ToString());
                Assert.True(await secondaryDispatch.WaitAsync(1000));

                // But connections will still get round-robined to the primary listener.
                await HttpClientSlim.GetStringAsync(address.ToString());
                Assert.True(await primaryDispatch.WaitAsync(1000));
                await HttpClientSlim.GetStringAsync(address.ToString());
                Assert.True(await secondaryDispatch.WaitAsync(1000));

                await listenerSecondary.DisposeAsync();
                await kestrelThreadSecondary.StopAsync(TimeSpan.FromSeconds(1));

                // Once the secondary listener dies, it is removed from the dispatch queue.
                await HttpClientSlim.GetStringAsync(address.ToString());
                Assert.True(await primaryDispatch.WaitAsync(1000));
                await HttpClientSlim.GetStringAsync(address.ToString());
                Assert.True(await primaryDispatch.WaitAsync(1000));

                await listenerPrimary.DisposeAsync();
                await kestrelThreadPrimary.StopAsync(TimeSpan.FromSeconds(1));
            }
        }

        private class TestApplication : IHttpApplication<DefaultHttpContext>
        {
            private readonly Action<DefaultHttpContext> _app;

            public TestApplication(Action<DefaultHttpContext> app)
            {
                _app = app;
            }

            public DefaultHttpContext CreateContext(IFeatureCollection contextFeatures)
            {
                return new DefaultHttpContext(contextFeatures);
            }

            public Task ProcessRequestAsync(DefaultHttpContext context)
            {
                _app(context);
                return TaskCache.CompletedTask;
            }

            public void DisposeContext(DefaultHttpContext context, Exception exception)
            {
            }
        }
    }
}
