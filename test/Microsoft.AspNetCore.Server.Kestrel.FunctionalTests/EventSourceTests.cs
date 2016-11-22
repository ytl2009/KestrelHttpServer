// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;
using Microsoft.AspNetCore.Testing;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.FunctionalTests
{
    public class EventSourceTests
    {
        [Fact]
        public async Task LogsConnection()
        {
            var listener = new TestEventListener();
            listener.EnableEvents(KestrelEventSource.Log, EventLevel.Informational);

            var host = new WebHostBuilder()
                .UseUrls("http://127.0.0.1:0")
                .UseKestrel()
                .Configure(app =>
                {
                    app.Run(context =>
                    {
                        var id = context.Features.Get<IHttpConnectionFeature>().ConnectionId;
                        context.Response.ContentLength = id.Length;
                        return context.Response.WriteAsync(id);
                    });
                })
                .Build();

            string connectionId;
            using (host)
            {
                host.Start();

                connectionId = await HttpClientSlim.GetStringAsync($"http://127.0.0.1:{host.GetPort()}/")
                    .TimeoutAfter(TimeSpan.FromSeconds(10));
            }

            // capture list here as other tests executing in parallel may log events
            var events = listener.EventData.ToList();
#if NET451
            // collection may contain connection events from other tests
            var start = Assert.Single(listener.EventData, e => (e.Payload.FirstOrDefault() as string) == connectionId);
#else
            var start = Assert.Single(listener.EventData, e => GetProperty(e, "connectionId") == connectionId);
            Assert.Equal("ConnectionStart", start.EventName);
            Assert.All(new[] {"serverAddress", "connectionId", "remoteEndPoint", "localEndPoint"},
                p => Assert.Contains(p, start.PayloadNames));
            Assert.Equal($"http://127.0.0.1:{host.GetPort()}", GetProperty(start, "serverAddress"));
            Assert.Equal(connectionId, GetProperty(start, "connectionId"));
#endif
            Assert.Same(KestrelEventSource.Log, start.EventSource);
        }

#if !NET451
        private string GetProperty(EventWrittenEventArgs data, string propName)
            => data.Payload[data.PayloadNames.IndexOf(propName)] as string;
#endif

        private class TestEventListener : EventListener
        {
            private List<EventWrittenEventArgs> _events = new List<EventWrittenEventArgs>();
            
            public IEnumerable<EventWrittenEventArgs> EventData => _events;

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                _events.Add(eventData);
            }
        }
    }
}