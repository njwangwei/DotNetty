// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Libuv
{
    using System;
    using System.Net;
    using System.Threading.Tasks;
    using DotNetty.Buffers;
    using DotNetty.Common.Concurrency;
    using DotNetty.Common.Internal.Logging;
    using DotNetty.Common.Utilities;
    using DotNetty.Transport.Channels;
    using DotNetty.Transport.Libuv.Native;

    public abstract class NativeChannel : AbstractChannel
    {
        static readonly IInternalLogger Logger = InternalLoggerFactory.GetInstance<NativeChannel>();

        [Flags]
        protected enum StateFlags
        {
            Open = 1,
            ReadScheduled = 1 << 1,
            WriteScheduled = 1 << 2,
            Active = 1 << 3
        }

        volatile StateFlags state;
        TaskCompletionSource connectPromise;

        protected NativeChannel(IChannel parent) : base(parent)
        {
            this.state = StateFlags.Open;
        }

        public override bool Open => this.IsInState(StateFlags.Open);

        public override bool Active => this.IsInState(StateFlags.Active);

        protected override bool IsCompatible(IEventLoop eventLoop) => eventLoop is LoopExecutor;

        protected bool IsInState(StateFlags stateToCheck) => (this.state & stateToCheck) == stateToCheck;

        protected void SetState(StateFlags stateToSet) => this.state |= stateToSet;

        protected StateFlags ResetState(StateFlags stateToReset)
        {
            StateFlags oldState = this.state;
            if ((oldState & stateToReset) != 0)
            {
                this.state = oldState & ~stateToReset;
            }
            return oldState;
        }

        protected bool TryResetState(StateFlags stateToReset)
        {
            StateFlags oldState = this.state;
            if ((oldState & stateToReset) != 0)
            {
                this.state = oldState & ~stateToReset;
                return true;
            }
            return false;
        }

        void DoConnect(EndPoint remoteAddress, EndPoint localAddress)
        {
            ConnectRequest request = null;
            try
            {
                if (localAddress != null)
                {
                    this.DoBind(localAddress);
                }
                request = new TcpConnect((INativeUnsafe)this.Unsafe, (IPEndPoint)remoteAddress);
            }
            catch
            {
                request?.Dispose();
                throw;
            }
        }

        void DoFinishConnect()
        {
            this.OnConnected();
            this.Pipeline.FireChannelActive();
        }

        protected override void DoClose()
        {
            TaskCompletionSource promise = this.connectPromise;
            if (promise != null)
            {
                promise.TrySetException(new ClosedChannelException());
                this.connectPromise = null;
            }
        }

        protected void OnConnected()
        {
            this.SetState(StateFlags.Active);
            this.CacheLocalAddress();
            this.CacheRemoteAddress();
        }

        protected abstract void DoScheduleRead();

        internal abstract IntPtr GetLoopHandle();

        protected abstract class NativeChannelUnsafe : AbstractUnsafe, INativeUnsafe
        {
            protected NativeChannelUnsafe(NativeChannel channel) : base(channel)
            {
            }

            public override Task ConnectAsync(EndPoint remoteAddress, EndPoint localAddress)
            {
                var ch = (NativeChannel)this.channel;
                if (!ch.Open)
                {
                    return this.CreateClosedChannelExceptionTask();
                }

                try
                {
                    if (ch.connectPromise != null)
                    {
                        throw new InvalidOperationException("connection attempt already made");
                    }

                    ch.connectPromise = new TaskCompletionSource(remoteAddress);
                    ch.DoConnect(remoteAddress, localAddress);
                    return ch.connectPromise.Task;
                }
                catch (Exception ex)
                {
                    this.CloseIfClosed();
                    return TaskEx.FromException(this.AnnotateConnectException(ex, remoteAddress));
                }
            }

            // Callback from libuv connect request in libuv thread
            void INativeUnsafe.FinishConnect(ConnectRequest request)
            {
                var ch = (NativeChannel)this.channel;
                TaskCompletionSource promise = ch.connectPromise;
                try
                {
                    if (request.Error != null)
                    {
                        promise.TrySetException(request.Error);
                        this.CloseIfClosed();
                    }
                    else
                    {
                        ch.DoFinishConnect();
                        promise.TryComplete();
                    }
                }
                catch (Exception exception)
                {
                    promise.TrySetException(exception);
                    this.CloseIfClosed();
                }
                finally
                {
                    request.Dispose();
                    ch.connectPromise = null;
                }
            }

            public abstract IntPtr UnsafeHandle { get; }

            // Callback from libuv allocate in libuv thread
            ReadOperation INativeUnsafe.PrepareRead()
            {
                var ch = (NativeChannel)this.channel;
                IChannelConfiguration config = ch.Configuration;
                IByteBufferAllocator allocator = config.Allocator;

                IRecvByteBufAllocatorHandle allocHandle = this.RecvBufAllocHandle;
                allocHandle.Reset(config);
                IByteBuffer buffer = allocHandle.Allocate(allocator);
                allocHandle.AttemptedBytesRead = buffer.WritableBytes;

                return new ReadOperation(this, buffer);
            }

            // Callback from libuv read in libuv thread
            void INativeUnsafe.FinishRead(ReadOperation operation)
            {
                var ch = (NativeChannel)this.channel;
                IChannelPipeline pipeline = ch.Pipeline;
                Exception error = operation.Error;

                bool close = error != null || operation.EndOfStream;
                IRecvByteBufAllocatorHandle allocHandle = this.RecvBufAllocHandle;
                IByteBuffer buffer = operation.Buffer;
                allocHandle.LastBytesRead = operation.Status;
                if (allocHandle.LastBytesRead <= 0)
                {
                    // nothing was read -> release the buffer.
                    buffer.SafeRelease();
                }
                else
                {
                    buffer.SetWriterIndex(buffer.WriterIndex + operation.Status);
                    allocHandle.IncMessagesRead(1);
                    pipeline.FireChannelRead(buffer);
                }

                allocHandle.ReadComplete();
                pipeline.FireChannelReadComplete();

                if (error != null)
                {
                    pipeline.FireExceptionCaught(error);
                }
                if (close)
                {
                    this.SafeClose();
                }
            }

            async void SafeClose()
            {
                try
                {
                    await this.CloseAsync();
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (Logger.DebugEnabled)
                    {
                        Logger.Debug($"Failed to close channel {this.channel} cleanly.", ex);
                    }
                }
            }

            // Callback from libuv write request in libuv thread
            void INativeUnsafe.FinishWrite(WriteRequest writeRequest)
            {
                try
                {
                    if (writeRequest.Error != null)
                    {
                        ChannelOutboundBuffer input = this.OutboundBuffer;
                        input?.FailFlushed(writeRequest.Error, true);
                        this.channel.Pipeline.FireExceptionCaught(writeRequest.Error);
                    }
                }
                finally
                {
                    writeRequest.Release();
                }
            }

            internal void ScheduleRead()
            {
                var ch = (NativeChannel)this.channel;
                if (ch.EventLoop.InEventLoop)
                {
                    ch.DoScheduleRead();
                }
                else
                {
                    ch.EventLoop.Execute(p => ((NativeChannel)p).DoScheduleRead(), ch);
                }
            }
        }
    }

    interface INativeUnsafe
    {
        IntPtr UnsafeHandle { get; }

        void FinishConnect(ConnectRequest request);

        ReadOperation PrepareRead();

        void FinishRead(ReadOperation readOperation);

        void FinishWrite(WriteRequest writeRequest);
    }
    
    interface IServerNativeUnsafe
    {
        void SetOptions(TcpListener tcpListener);

        void Accept(RemoteConnection connection);

        void Accept(NativeHandle handle);
    }
}
