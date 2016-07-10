// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Http
{
    public class Frame<TContext> : Frame
    {
        private readonly IHttpApplication<TContext> _application;

        public Frame(IHttpApplication<TContext> application, IConnectionInformation connectionInformation, ServiceContext serviceContext)
            : base(connectionInformation, serviceContext)
        {
            _application = application;
        }

        /// <summary>
        /// Primary loop which consumes socket input, parses it for protocol framing, and invokes the
        /// application delegate for as long as the socket is intended to remain open.
        /// The resulting Task from this loop is preserved in a field which is used when the server needs
        /// to drain and close all currently active connections.
        /// </summary>
        public override async Task RequestProcessingAsync()
        {
            try
            {
                while (!_requestProcessingStopping)
                {
                    while (!_requestProcessingStopping && TakeStartLine(InputChannel) != RequestLineStatus.Done)
                    {
                        if (InputChannel.Completed)
                        {
                            // We need to attempt to consume start lines and headers even after
                            // InputChannel.Completed is set to true to ensure we don't close a
                            // connection without giving the application a chance to respond to a request
                            // sent immediately before the a FIN from the client.
                            var requestLineStatus = TakeStartLine(InputChannel);

                            if (requestLineStatus == RequestLineStatus.Empty)
                            {
                                return;
                            }

                            if (requestLineStatus != RequestLineStatus.Done)
                            {
                                RejectRequest($"Malformed request: {requestLineStatus}");
                            }

                            break;
                        }

                        await InputChannel;
                    }

                    InitializeHeaders();

                    while (!_requestProcessingStopping && !TakeMessageHeaders(InputChannel, FrameRequestHeaders))
                    {
                        if (InputChannel.Completed)
                        {
                            // We need to attempt to consume start lines and headers even after
                            // FrameInputChannel.Completed is set to true to ensure we don't close a
                            // connection without giving the application a chance to respond to a request
                            // sent immediately before the a FIN from the client.
                            if (!TakeMessageHeaders(InputChannel, FrameRequestHeaders))
                            {
                                RejectRequest($"Malformed request: invalid headers.");
                            }

                            break;
                        }

                        await InputChannel;
                    }

                    if (!_requestProcessingStopping)
                    {
                        var messageBody = MessageBody.For(HttpVersion, FrameRequestHeaders, this);
                        _keepAlive = messageBody.RequestKeepAlive;

                        InitializeStreams(messageBody);

                        _abortedCts = null;
                        _manuallySetRequestAbortToken = null;

                        var context = _application.CreateContext(this);
                        try
                        {
                            await _application.ProcessRequestAsync(context).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            ReportApplicationError(ex);
                        }
                        finally
                        {
                            // Trigger OnStarting if it hasn't been called yet and the app hasn't
                            // already failed. If an OnStarting callback throws we can go through
                            // our normal error handling in ProduceEnd.
                            // https://github.com/aspnet/KestrelHttpServer/issues/43
                            if (!HasResponseStarted && _applicationException == null && _onStarting != null)
                            {
                                await FireOnStarting();
                            }

                            PauseStreams();

                            if (_onCompleted != null)
                            {
                                await FireOnCompleted();
                            }

                            _application.DisposeContext(context, _applicationException);
                        }

                        // If _requestAbort is set, the connection has already been closed.
                        if (Volatile.Read(ref _requestAborted) == 0)
                        {
                            ResumeStreams();

                            if (_keepAlive)
                            {
                                // Finish reading the request body in case the app did not.
                                await messageBody.Consume();
                            }

                            await ProduceEnd();
                        }

                        StopStreams();

                        if (!_keepAlive)
                        {
                            // End the connection for non keep alive as data incoming may have been thrown off
                            return;
                        }
                    }

                    Reset();
                }
            }
            catch (TaskCanceledException)
            {
                // Explicitly aborted 
            }
            catch (Exception ex)
            {
                ServiceContext.Log.LogWarning(0, ex, "Connection processing ended abnormally");
            }
            finally
            {
                try
                {
                    await TryProduceInvalidRequestResponse();

                    _abortedCts = null;

                    // If _requestAborted is set, the connection has already been closed.
                    if (Volatile.Read(ref _requestAborted) == 0)
                    {
                        OutputChannel.CompleteWriting();
                    }
                }
                catch (Exception ex)
                {
                    ServiceContext.Log.LogWarning(0, ex, "Connection shutdown abnormally");
                }

                InputChannel.CompleteReading();
            }
        }
    }
}