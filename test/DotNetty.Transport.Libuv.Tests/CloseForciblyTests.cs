// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Libuv.Tests
{
    using System;
    using System.Net;
    using System.Threading.Tasks;
    using DotNetty.Transport.Bootstrapping;
    using DotNetty.Transport.Channels;
    using Xunit;

    public class CloseForciblyTests : IDisposable
    {
        static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        readonly IEventLoopGroup group;

        public CloseForciblyTests()
        {
            this.group = new EventLoopGroup(1);
        }

        [Fact]
        public void CloseForcibly()
        {
            ServerBootstrap sb = new ServerBootstrap()
                .Group(this.group)
                .Channel<TcpServerChannel>();
            Bootstrap cb = new Bootstrap()
                .Group(this.group)
                .Channel<TcpChannel>();
            CloseForcibly(sb, cb);
        }

        static void CloseForcibly(ServerBootstrap sb, Bootstrap cb)
        {
            sb.Handler(new InboundHandler())
              .ChildHandler(new ChannelHandlerAdapter());
            cb.Handler(new ChannelHandlerAdapter());

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
            IChannel channel = client.Result;
            Assert.NotNull(channel.LocalAddress);
            Assert.Equal(endPoint, channel.RemoteAddress);
            channel.CloseAsync().Wait(DefaultTimeout);

            sc.CloseAsync().Wait(DefaultTimeout);
        }

        sealed class InboundHandler : ChannelHandlerAdapter
        {
            public override void ChannelRead(IChannelHandlerContext context, object message)
            {
                var childChannel = (TcpChannel)message;
                childChannel.Unsafe.CloseForcibly();
            }
        }

        public void Dispose()
        {
            this.group.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.Zero).Wait(DefaultTimeout);
        }
    }
}
