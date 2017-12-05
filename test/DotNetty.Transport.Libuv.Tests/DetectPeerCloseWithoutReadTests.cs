// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Libuv.Tests
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using DotNetty.Buffers;
    using DotNetty.Common.Concurrency;
    using DotNetty.Transport.Bootstrapping;
    using DotNetty.Transport.Channels;
    using Xunit;

    public sealed class DetectPeerCloseWithoutReadTests : IDisposable
    {
        static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        readonly IEventLoopGroup serverGroup;
        readonly IEventLoopGroup clientGroup;

        public DetectPeerCloseWithoutReadTests()
        {
            this.serverGroup = new EventLoopGroup(1);
            this.clientGroup = new EventLoopGroup(1);
        }

        [Fact]
        public void ClientCloseWithoutServerReadIsDetected()
        {
            const int ExpectedBytes = 100;

            var serverHandler = new TestHandler(ExpectedBytes);
            ServerBootstrap sb = new ServerBootstrap()
                .Group(this.serverGroup)
                .Channel<TcpServerChannel>()
                .ChildOption(ChannelOption.AutoRead, false)
                .ChildHandler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    channel.Pipeline.AddLast(serverHandler);
                }));
            var address = new IPEndPoint(IPAddress.IPv6Loopback, 0);

            // start server
            Task<IChannel> server = sb.BindAsync(address);
            Assert.True(server.Wait(DefaultTimeout));
            IChannel sc = server.Result;
            Assert.NotNull(sc.LocalAddress);
            var endPoint = (IPEndPoint)sc.LocalAddress;

            // connect to server
            Bootstrap cb = new Bootstrap()
                .Group(this.clientGroup)
                .Channel<TcpChannel>()
                .Handler(new ChannelHandlerAdapter());

            Task<IChannel> client = cb.ConnectAsync(endPoint);
            Assert.True(client.Wait(DefaultTimeout));
            IChannel clientChannel = client.Result;
            Assert.NotNull(clientChannel.LocalAddress);
            Assert.Equal(endPoint, clientChannel.RemoteAddress);

            IByteBuffer buf = clientChannel.Allocator.Buffer(ExpectedBytes);
            buf.SetWriterIndex(buf.WriterIndex + ExpectedBytes);
            clientChannel.WriteAndFlushAsync(buf).ContinueWith(_ => clientChannel.CloseAsync());

            Task<int> completion = serverHandler.Completion;
            Assert.True(completion.Wait(DefaultTimeout));
            Assert.Equal(ExpectedBytes, completion.Result);
        }

        sealed class TestHandler : SimpleChannelInboundHandler<IByteBuffer>
        {
            readonly int expectedBytesRead;
            readonly TaskCompletionSource<int> completion;
            int bytesRead;

            public TestHandler(int expectedBytesRead)
            {
                this.expectedBytesRead = expectedBytesRead;
                this.completion = new TaskCompletionSource<int>();
            }

            public Task<int> Completion => this.completion.Task;

            protected override void ChannelRead0(IChannelHandlerContext ctx, IByteBuffer msg)
            {
                if (Interlocked.Add(ref this.bytesRead, msg.ReadableBytes) >= this.expectedBytesRead)
                {
                    this.completion.TrySetResult(this.bytesRead);
                }
                // Because autoread is off, we call read to consume all data until we detect the close.
                ctx.Read();
            }

            public override void ChannelInactive(IChannelHandlerContext ctx)
            {
                this.completion.TrySetResult(this.bytesRead);
                ctx.FireChannelInactive();
            }
        }

        [Fact]
        public void ServerCloseWithoutClientReadIsDetected()
        {
            const int ExpectedBytes = 100;

            var serverHandler = new WriteHandler(ExpectedBytes);
            ServerBootstrap sb = new ServerBootstrap()
                .Group(this.serverGroup)
                .Channel<TcpServerChannel>()
                .ChildHandler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    channel.Pipeline.AddLast(serverHandler);
                }));

            var address = new IPEndPoint(IPAddress.IPv6Loopback, 0);

            // start server
            Task<IChannel> server = sb.BindAsync(address);
            Assert.True(server.Wait(DefaultTimeout));
            IChannel sc = server.Result;
            Assert.NotNull(sc.LocalAddress);
            var endPoint = (IPEndPoint)sc.LocalAddress;

            // connect to server
            var clientHandler = new TestHandler(ExpectedBytes);
            Bootstrap cb = new Bootstrap()
                .Group(this.serverGroup)
                .Channel<TcpChannel>()
                .Option(ChannelOption.AutoRead, false)
                .Handler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    channel.Pipeline.AddLast(clientHandler);
                }));

            Task<IChannel> client = cb.ConnectAsync(endPoint);
            Assert.True(client.Wait(DefaultTimeout));
            IChannel clientChannel = client.Result;
            Assert.NotNull(clientChannel.LocalAddress);
            Assert.Equal(endPoint, clientChannel.RemoteAddress);

            // Wait until server inactive to read on client
            Assert.True(serverHandler.Inactive.Wait(DefaultTimeout));
            clientChannel.Read();
            Task<int> completion = clientHandler.Completion;
            Assert.True(completion.Wait(DefaultTimeout));
            Assert.Equal(ExpectedBytes, completion.Result);
        }

        sealed class WriteHandler : ChannelHandlerAdapter
        {
            readonly int expectedBytesRead;
            readonly TaskCompletionSource completion;

            public WriteHandler(int expectedBytesRead)
            {
                this.expectedBytesRead = expectedBytesRead;
                this.completion = new TaskCompletionSource();
            }

            public Task Inactive => this.completion.Task;

            public override void ChannelActive(IChannelHandlerContext ctx)
            {
                IByteBuffer buf = ctx.Allocator.Buffer(this.expectedBytesRead);
                buf.SetWriterIndex(buf.WriterIndex + this.expectedBytesRead);
                ctx.WriteAndFlushAsync(buf).ContinueWith(_ => ctx.CloseAsync());
                ctx.FireChannelActive();
            }

            public override void ChannelInactive(IChannelHandlerContext context) => this.completion.TryComplete();
        }

        public void Dispose()
        {
            this.serverGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.Zero).Wait(DefaultTimeout);
            this.clientGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.Zero).Wait(DefaultTimeout);
        }
    }
}
