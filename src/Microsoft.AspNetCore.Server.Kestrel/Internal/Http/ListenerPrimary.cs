// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Internal.Http
{
    /// <summary>
    /// A primary listener waits for incoming connections on a specified socket. Incoming
    /// connections may be passed to a secondary listener to handle.
    /// </summary>
    public abstract class ListenerPrimary : Listener
    {
        private readonly List<UvPipeHandle> _dispatchPipes = new List<UvPipeHandle>();
        private int _dispatchIndex;
        private string _pipeName;
        private IntPtr _fileCompletionInfoPtr;
        private IntPtr _reattachFileCompletionInfoPtr;
        private bool _tryDetachFromIOCP = PlatformApis.IsWindows;

        // this message is passed to write2 because it must be non-zero-length,
        // but it has no other functional significance
        private readonly ArraySegment<ArraySegment<byte>> _dummyMessage =
            new ArraySegment<ArraySegment<byte>>(new[] {new ArraySegment<byte>(new byte[] {1, 2, 3, 4})});

        private MemoryPoolBlock _dummyBlock;
        private MemoryPoolIterator _dummyIter;

        protected ListenerPrimary(ServiceContext serviceContext) : base(serviceContext)
        {
        }

        private UvPipeHandle ListenPipe { get; set; }

        public async Task StartAsync(
            string pipeName,
            ServerAddress address,
            KestrelThread thread)
        {
            _pipeName = pipeName;

            if (_fileCompletionInfoPtr == IntPtr.Zero)
            {
                var fileCompletionInfo = new FILE_COMPLETION_INFORMATION() {Key = IntPtr.Zero, Port = IntPtr.Zero};
                _fileCompletionInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(fileCompletionInfo));
                Marshal.StructureToPtr(fileCompletionInfo, _fileCompletionInfoPtr, false);
            }

            if (_reattachFileCompletionInfoPtr == IntPtr.Zero)
            {
                _reattachFileCompletionInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FILE_COMPLETION_INFORMATION>());
            }

            await StartAsync(address, thread).ConfigureAwait(false);

            await Thread.PostAsync(state => ((ListenerPrimary) state).PostCallback(),
                this).ConfigureAwait(false);
        }

        private void PostCallback()
        {
            _dummyBlock = Thread.Memory.Lease();
            _dummyIter = new MemoryPoolIterator(_dummyBlock);

            ListenPipe = new UvPipeHandle(Log);
            ListenPipe.Init(Thread.Loop, Thread.QueueCloseHandle, false);
            ListenPipe.Bind(_pipeName);
            ListenPipe.Listen(Constants.ListenBacklog,
                (pipe, status, error, state) => ((ListenerPrimary) state).OnListenPipe(pipe, status, error), this);
        }

        private void OnListenPipe(UvStreamHandle pipe, int status, Exception error)
        {
            if (status < 0)
            {
                return;
            }

            var dispatchPipe = new UvPipeHandle(Log);

            try
            {
                dispatchPipe.Init(Thread.Loop, Thread.QueueCloseHandle, true);
                pipe.Accept(dispatchPipe);
            }
            catch (UvException ex)
            {
                dispatchPipe.Dispose();
                Log.LogError(0, ex, "ListenerPrimary.OnListenPipe");
                return;
            }

            _dispatchPipes.Add(dispatchPipe);
        }

        protected override void DispatchConnection(UvStreamHandle socket)
        {
            var index = _dispatchIndex++ % (_dispatchPipes.Count + 1);
            if (index == _dispatchPipes.Count)
            {
                Console.WriteLine("Dispatching to Primary");
                base.DispatchConnection(socket);
            }
            else
            {
                var dispatchPipe = _dispatchPipes[index];
                var write = new UvWriteReq(Log);
                write.Init(Thread.Loop);

                //try
                //{
                //    // Verify pipe is open
                //    write.Write(dispatchPipe, _dummyIter, _dummyIter, 1,
                //        (write2, status, ex, state) =>
                //        {
                //            write2.Dispose();
                //        }, null);
                //}
                //catch (UvException ex)
                //{
                //    write.Dispose();

                //    // Assume the pipe is dead, so remove the pipe from _dispatchPipes.
                //    // Even if all named pipes are removed, ListenerPrimary will still dispatch to itself.
                //    Log.LogError(0, ex, "ListenerPrimary.DispatchConnection failed. Removing pipe connection.");
                //    dispatchPipe.Dispose();
                //    _dispatchPipes.Remove(dispatchPipe);

                //    Console.WriteLine("continue;");
                //    // Try to dispatch connection again
                //    DispatchConnection(socket);
                //    return;
                //}

                //write = new UvWriteReq(Log);
                //write.Init(Thread.Loop);

                try
                {
                    Console.WriteLine("Detaching from IOCP");
                    //DetachFromIOCP(socket);

                    write.Write2(
                        dispatchPipe,
                        _dummyMessage,
                        socket,
                        (write2, status, error, state) =>
                        {
                            write2.Dispose();
                            ((UvStreamHandle)state).Dispose();
                        },
                        socket);
                }
                catch (UvException ex)
                {
                    write.Dispose();

                    // Assume the pipe is dead, so remove the pipe from _dispatchPipes.
                    // Even if all named pipes are removed, ListenerPrimary will still dispatch to itself.
                    Log.LogError(0, ex, "ListenerPrimary.DispatchConnection failed. Removing pipe connection.");
                    dispatchPipe.Dispose();
                    _dispatchPipes.Remove(dispatchPipe);

                    //ReattachToIOCP(socket);
                    //new Connection(this, socket).Start();
                    //socket.Dispose();

                    // We'd rather not send a FIN, but the socket will remain idle if we just call uv_close.
                    var shutdownReq = new UvShutdownReq(Log);
                    shutdownReq.Init(Thread.Loop);
                    shutdownReq.Shutdown(socket, (req, status, socket2) =>
                    {
                        req.Dispose();
                        ((UvStreamHandle)socket2).Dispose();
                    }, socket);
                }
            }
        }

        private void DetachFromIOCP(UvHandle handle)
        {
            if (!_tryDetachFromIOCP)
            {
                return;
            }

            // https://msdn.microsoft.com/en-us/library/windows/hardware/ff728840(v=vs.85).aspx
            const int FileReplaceCompletionInformation = 61;
            // https://msdn.microsoft.com/en-us/library/cc704588.aspx
            const uint STATUS_INVALID_INFO_CLASS = 0xC0000003;

            var statusBlock = new IO_STATUS_BLOCK();
            var socket = IntPtr.Zero;
            Thread.Loop.Libuv.uv_fileno(handle, ref socket);

            var status = NtSetInformationFile(socket, out statusBlock, _fileCompletionInfoPtr,
                (uint) Marshal.SizeOf<FILE_COMPLETION_INFORMATION>(), FileReplaceCompletionInformation);

            if (status == STATUS_INVALID_INFO_CLASS)
            {
                // Replacing IOCP information is only supported on Windows 8.1 or newer
                _tryDetachFromIOCP = false;
            }

            Console.WriteLine("DettachToIOCP status: {0}", status);
        }

        private void ReattachToIOCP(UvHandle handle)
        {
            if (!_tryDetachFromIOCP)
            {
                return;
            }

            // https://msdn.microsoft.com/en-us/library/windows/hardware/ff728840(v=vs.85).aspx
            //const int FileReplaceCompletionInformation = 61;
            // https://msdn.microsoft.com/en-us/library/cc704588.aspx

            //var statusBlock = new IO_STATUS_BLOCK();
            var socket = IntPtr.Zero;
            Thread.Loop.Libuv.uv_fileno(handle, ref socket);

            //var fileCompletionInfo = new FILE_COMPLETION_INFORMATION() { Port = Marshal.PtrToStructure<IntPtr>(Thread.Loop.InternalGetHandle() + 56), Key = socket };

            //Console.WriteLine("FILE_COMPLETION_INFORMATION(Port = {0}, Key = {1})", fileCompletionInfo.Port, fileCompletionInfo.Key);

            var status = CreateIoCompletionPort(socket,
                Marshal.PtrToStructure<IntPtr>(Thread.Loop.InternalGetHandle() + 56), (UIntPtr)socket.ToInt64(), 0);

            //Marshal.StructureToPtr(fileCompletionInfo, _reattachFileCompletionInfoPtr, false);

            //var status = NtSetInformationFile(socket, out statusBlock, _fileCompletionInfoPtr,
            //    (uint) Marshal.SizeOf<FILE_COMPLETION_INFORMATION>(), FileReplaceCompletionInformation);

            Console.WriteLine("ReattachToIOCP status: {0}", status);
        }

        private struct IO_STATUS_BLOCK
        {
            uint status;
            ulong information;
        }

        private struct FILE_COMPLETION_INFORMATION
        {
            public IntPtr Port;
            public IntPtr Key;
        }

        [DllImport("NtDll.dll")]
        private static extern uint NtSetInformationFile(IntPtr FileHandle,
                out IO_STATUS_BLOCK IoStatusBlock, IntPtr FileInformation, uint Length,
                int FileInformationClass);

        [DllImport("Kernel32.dll")]
        private static extern IntPtr CreateIoCompletionPort(IntPtr FileHandle,
           IntPtr ExistingCompletionPort, UIntPtr CompletionKey,
           uint NumberOfConcurrentThreads);

        public override async Task DisposeAsync()
        {
            // Call base first so the ListenSocket gets closed and doesn't
            // try to dispatch connections to closed pipes.
            await base.DisposeAsync().ConfigureAwait(false);

            if (_fileCompletionInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_fileCompletionInfoPtr);
                _fileCompletionInfoPtr = IntPtr.Zero;
            }

            if (_reattachFileCompletionInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_reattachFileCompletionInfoPtr);
                _reattachFileCompletionInfoPtr = IntPtr.Zero;
            }

            if (Thread.FatalError == null && ListenPipe != null)
            {
                Console.WriteLine("ListnerPrimary.Dispose PostAsync");
                await Thread.PostAsync(state =>
                {
                    var listener = (ListenerPrimary)state;
                    Console.WriteLine("ListnerPrimary.Dispose ListenPipe");
                    listener.ListenPipe.Dispose();

                    foreach (var dispatchPipe in listener._dispatchPipes)
                    {
                        Console.WriteLine("ListnerPrimary.Dispose dispatchPipe");
                        dispatchPipe.Dispose();
                    }

                    Thread.Memory.Return(_dummyBlock);
                }, this).ConfigureAwait(false);
            }
        }
    }
}
