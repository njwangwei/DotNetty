// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Libuv.Native
{
    using System;
    using System.Diagnostics;
    using System.Net;
    using DotNetty.Buffers;
    using DotNetty.Common.Internal;

    sealed class Tcp : TcpHandle
    {
        static readonly uv_alloc_cb AllocateCallback = OnAllocateCallback;
        static readonly uv_read_cb ReadCallback = OnReadCallback;

        INativeUnsafe unsafeChannel;
        readonly ILinkedQueue<ReadOperation> pendingReads;

        public Tcp(Loop loop) : base(loop)
        {
            this.pendingReads = PlatformDependent.NewSpscLinkedQueue<ReadOperation>();
        }

        public void ReadStart(INativeUnsafe channel)
        {
            Debug.Assert(channel != null);

            this.Validate();
            int result = NativeMethods.uv_read_start(this.Handle, AllocateCallback, ReadCallback);
            NativeMethods.ThrowIfError(result);

            this.unsafeChannel = channel;
        }

        public void ReadStop()
        {
            if (this.Handle == IntPtr.Zero)
            {
                return;
            }

            // This function is idempotent and may be safely called on a stopped stream.
            int result = NativeMethods.uv_read_stop(this.Handle);
            NativeMethods.ThrowIfError(result);
        }

        static void OnReadCallback(IntPtr handle, IntPtr nread, ref uv_buf_t buf)
        {
            var tcp = GetTarget<Tcp>(handle);
            int status = (int)nread.ToInt64();

            OperationException error = null;
            if (status < 0 && status != NativeMethods.EOF)
            {
                error = NativeMethods.CreateError((uv_err_code)status);
            }

            ReadOperation operation = tcp.pendingReads.Poll();
            if (operation == null)
            {
                if (error == null)
                {
                    Logger.Warn($"Tcp {tcp.Handle} read operation completed prematurely.");
                    return;
                }

                // It is possible if the client connection resets  
                // causing errors where there are no pending read
                // operations, in this case we just notify the channel
                // for errors
                operation = new ReadOperation(tcp.unsafeChannel, Unpooled.Empty);
            }

            try
            {
                operation.Complete(status, error);
            }
            catch (Exception exception)
            {
                Logger.Warn($"Tcp {tcp.Handle} read callbcak failed.", exception);
            }
        }

        public void Write(WriteRequest request)
        {
            this.Validate();
            try
            {
                int result = NativeMethods.uv_write(
                    request.Handle,
                    this.Handle,
                    request.Bufs,
                    request.BufferCount,
                    WriteRequest.WriteCallback);
                NativeMethods.ThrowIfError(result);
            }
            catch
            {
                request.Release();
                throw;
            }
        }

        protected override void OnClosed()
        {
            base.OnClosed();

            while (true)
            {
                ReadOperation operation = this.pendingReads.Poll();
                if (operation == null)
                {
                    break;
                }

                operation.Dispose();
            }
            this.unsafeChannel = null;
        }

        void OnAllocateCallback(out uv_buf_t buf)
        {
            ReadOperation operation = this.unsafeChannel.PrepareRead();
            this.pendingReads.Offer(operation);
            buf = operation.GetBuffer();
        }

        static void OnAllocateCallback(IntPtr handle, IntPtr suggestedSize, out uv_buf_t buf)
        {
            var tcp = GetTarget<Tcp>(handle);
            tcp.OnAllocateCallback(out buf);
        }

        public IPEndPoint GetPeerEndPoint()
        {
            this.Validate();
            return NativeMethods.TcpGetPeerName(this.Handle);
        }
    }
}
