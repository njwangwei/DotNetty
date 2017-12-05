// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Libuv.Tests
{
    using System;
    using System.Net;
    using System.Threading.Tasks;
    using DotNetty.Buffers;
    using DotNetty.Common.Concurrency;
    using DotNetty.Transport.Bootstrapping;
    using DotNetty.Transport.Channels;
    using Xunit;
    using Xunit.Abstractions;

    public sealed class EchoTests : IDisposable
    {
        static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        readonly ITestOutputHelper output;
        readonly Random random = new Random();
        readonly byte[] data = new byte[1048576];
        readonly IEventLoopGroup group;

        public EchoTests(ITestOutputHelper output)
        {
            this.output = output;
            this.group = new EventLoopGroup(1);
        }

        [Fact]
        public void SimpleEcho() => this.Run(false, true);

        [Fact]
        public void SimpleEchoNotAutoRead() => this.Run(false, false);

        [Fact]
        public void SimpleEchoWithAdditionalExecutor() => this.Run(true, true);

        [Fact]
        public void SimpleEchoWithAdditionalExecutorNotAutoRead() => this.Run(true, false);

        void Run(bool additionalExecutor, bool autoRead)
        {
            ServerBootstrap sb = new ServerBootstrap()
                .Group(this.group)
                .Channel<TcpServerChannel>();
            Bootstrap cb = new Bootstrap()
                .Group(this.group)
                .Channel<TcpChannel>();
            this.SimpleEcho0(sb, cb, additionalExecutor, autoRead);
        }

        void SimpleEcho0(ServerBootstrap sb, Bootstrap cb, bool additionalExecutor, bool autoRead)
        {
            var sh = new EchoHandler(autoRead, this.data, this.output);
            var ch = new EchoHandler(autoRead, this.data, this.output);

            if (additionalExecutor)
            {
                sb.ChildHandler(new ActionChannelInitializer<TcpChannel>(channel =>
                {
                    channel.Pipeline.AddLast(this.group, sh);
                }));
                cb.Handler(new ActionChannelInitializer<TcpChannel>(channel =>
                {
                    channel.Pipeline.AddLast(this.group, ch);
                }));
            }
            else
            {
                sb.ChildHandler(sh);
                sb.Handler(new ErrorOutputHandler(this.output));
                cb.Handler(ch);
            }
            sb.ChildOption(ChannelOption.AutoRead, autoRead);
            cb.Option(ChannelOption.AutoRead, autoRead);

            var address = new IPEndPoint(IPAddress.Loopback, 0);

            // start server
            Task<IChannel> server = sb.BindAsync(address);
            Assert.True(server.Wait(DefaultTimeout));
            IChannel sc = server.Result;
            Assert.NotNull(sc.LocalAddress);
            var endPoint = (IPEndPoint)sc.LocalAddress;

            // connect to server
            Task<IChannel> client = cb.ConnectAsync(endPoint);
            Assert.True(client.Wait(DefaultTimeout));
            IChannel cc = client.Result;
            Assert.NotNull(cc.LocalAddress);
            Assert.Equal(endPoint, cc.RemoteAddress);

            for (int i = 0; i < this.data.Length;)
            {
                int length = Math.Min(this.random.Next(1024 * 64), this.data.Length - i);
                IByteBuffer buf = Unpooled.WrappedBuffer(this.data, i, length);
                cc.WriteAndFlushAsync(buf);
                i += length;
            }

            Assert.True(Task.WhenAll(ch.Completion, sh.Completion).Wait(DefaultTimeout));

            sh.Channel?.CloseAsync().Wait(DefaultTimeout);
            ch.Channel?.CloseAsync().Wait(DefaultTimeout);
            sc.CloseAsync().Wait(DefaultTimeout);
        }

        public void Dispose()
        {
            this.group.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.Zero).Wait(DefaultTimeout);
        }

        sealed class ErrorOutputHandler : ChannelHandlerAdapter
        {
            readonly ITestOutputHelper output;

            public ErrorOutputHandler(ITestOutputHelper output)
            {
                this.output = output;
            }

            public override void ExceptionCaught(IChannelHandlerContext ctx, Exception cause) => this.output.WriteLine(cause.StackTrace);
        }

        sealed class EchoHandler : SimpleChannelInboundHandler<IByteBuffer>
        {
            readonly bool autoRead;
            readonly byte[] expected;
            readonly ITestOutputHelper output;
            readonly TaskCompletionSource completion;

            internal IChannel Channel;
            int counter;

            public EchoHandler(bool autoRead, byte[] expected, ITestOutputHelper output)
            {
                this.autoRead = autoRead;
                this.expected = expected;
                this.output = output;
                this.completion = new TaskCompletionSource();
            }

            public override void ChannelActive(IChannelHandlerContext ctx)
            {
                this.Channel = ctx.Channel;
                if (!this.autoRead)
                {
                    ctx.Read();
                }
            }

            public Task Completion => this.completion.Task;

            protected override void ChannelRead0(IChannelHandlerContext ctx, IByteBuffer msg)
            {
                var actual = new byte[msg.ReadableBytes];
                msg.ReadBytes(actual);
                int lastIdx = this.counter;
                for (int i = 0; i < actual.Length; i++)
                {
                    Assert.Equal(this.expected[i + lastIdx], actual[i]);
                }

                if (this.Channel.Parent != null)
                {
                    this.Channel.WriteAsync(Unpooled.WrappedBuffer(actual));
                }

                this.counter += actual.Length;
                if (this.counter == this.expected.Length)
                {
                    this.completion.TryComplete();
                }
            }

            public override void ChannelReadComplete(IChannelHandlerContext ctx)
            {
                try
                {
                    ctx.Flush();
                }
                finally
                {
                    if (!this.autoRead)
                    {
                        ctx.Read();
                    }
                }
            }

            public override void ExceptionCaught(IChannelHandlerContext ctx, Exception cause)
            {
                this.output.WriteLine(cause.StackTrace);
                ctx.CloseAsync();
                this.completion.TrySetException(cause);
            }
        }
    }
}
