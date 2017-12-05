// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Libuv.Tests
{
    using System;
    using System.Net;
    using System.Threading.Tasks;
    using DotNetty.Handlers.Logging;
    using DotNetty.Transport.Bootstrapping;
    using DotNetty.Transport.Channels;
    using DotNetty.Transport.Libuv.Native;
    using Xunit;

    public sealed class ConnectTests : IDisposable
    {
        static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        readonly IEventLoopGroup serverGroup;
        readonly IEventLoopGroup clientGroup;

        public ConnectTests()
        {
            this.serverGroup = new EventLoopGroup(1);
            this.clientGroup = new EventLoopGroup(1);
        }

        [Fact]
        public void Connect()
        {
            var address = new IPEndPoint(IPAddress.IPv6Loopback, 0);

            ServerBootstrap sb = new ServerBootstrap()
                .Group(this.serverGroup)
                .Channel<TcpServerChannel>()
                .ChildHandler(new ActionChannelInitializer<TcpChannel>(channel =>
                {
                    channel.Pipeline.AddLast("server logger", new LoggingHandler($"{nameof(ConnectTests)}"));
                }));

            Bootstrap cb = new Bootstrap()
                .Group(this.clientGroup)
                .Channel<TcpChannel>()
                .Handler(new ActionChannelInitializer<TcpChannel>(channel =>
                {
                    channel.Pipeline.AddLast("client logger", new LoggingHandler($"{nameof(ConnectTests)}"));
                }));

            // start server
            Task<IChannel> server = sb.BindAsync(address);
            Assert.True(server.Wait(DefaultTimeout));
            IChannel serverChannel = server.Result;
            Assert.NotNull(serverChannel.LocalAddress);
            var endPoint = (IPEndPoint)serverChannel.LocalAddress;

            // connect to server
            Task<IChannel> client = cb.ConnectAsync(endPoint);
            Assert.True(client.Wait(DefaultTimeout));
            IChannel clientChannel = client.Result;
            Assert.NotNull(clientChannel.LocalAddress);
            Assert.Equal(endPoint, clientChannel.RemoteAddress);
        }

        [Fact]
        public void MultipleConnect()
        {
            var address = new IPEndPoint(IPAddress.IPv6Loopback, 0);
            ServerBootstrap sb = new ServerBootstrap()
                .Group(this.serverGroup)
                .Channel<TcpServerChannel>()
                .ChildHandler(new ChannelHandlerAdapter());

            // start server
            Task<IChannel> server = sb.BindAsync(address);
            Assert.True(server.Wait(DefaultTimeout));
            IChannel serverChannel = server.Result;
            Assert.NotNull(serverChannel.LocalAddress);
            var endPoint = (IPEndPoint)serverChannel.LocalAddress;

            Bootstrap cb = new Bootstrap()
                .Group(this.clientGroup)
                .Channel<TcpChannel>()
                .Handler(new ChannelHandlerAdapter());

            var client = (Task<IChannel>)cb.RegisterAsync();
            Assert.True(client.Wait(DefaultTimeout));
            IChannel cc = client.Result;
            Task task = cc.ConnectAsync(endPoint);
            Assert.True(task.Wait(DefaultTimeout));

            // Attempt to connect again
            task = cc.ConnectAsync(endPoint);
            var exception = Assert.Throws<AggregateException>(() => task.Wait(DefaultTimeout));
            var aggregatedException = (AggregateException)exception.InnerExceptions[0];
            Assert.IsType<OperationException>(aggregatedException.InnerExceptions[0]);
            var operationException = (OperationException)aggregatedException.InnerExceptions[0];
            Assert.Equal("EISCONN", operationException.Name); // socket is already connected
        }

        public void Dispose()
        {
            this.clientGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.Zero).Wait(DefaultTimeout);
            this.serverGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.Zero).Wait(DefaultTimeout);
        }
    }
}
