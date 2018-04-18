﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// ReSharper disable ConvertToAutoProperty
namespace DotNetty.Codecs.Http.WebSockets
{
    using System;
    using System.Diagnostics.Contracts;
    using System.Threading.Tasks;
    using DotNetty.Common.Utilities;
    using DotNetty.Transport.Channels;

    public abstract class WebSocketClientHandshaker
    {
        static readonly ClosedChannelException CLOSED_CHANNEL_EXCEPTION = new ClosedChannelException();

        static readonly string HTTP_SCHEME_PREFIX = HttpScheme.Http + "://";
        static readonly string HTTPS_SCHEME_PREFIX = HttpScheme.Https + "://";

        readonly Uri uri;

        readonly WebSocketVersion version;

        volatile bool handshakeComplete;

        readonly string expectedSubprotocol;

        volatile string actualSubprotocol;

        protected readonly HttpHeaders customHeaders;

        readonly int maxFramePayloadLength;

        protected WebSocketClientHandshaker(
            Uri uri,
            WebSocketVersion version,
            string subprotocol,
            HttpHeaders customHeaders,
            int maxFramePayloadLength)
        {
            this.uri = uri;
            this.version = version;
            this.expectedSubprotocol = subprotocol;
            this.customHeaders = customHeaders;
            this.maxFramePayloadLength = maxFramePayloadLength;
        }

        public Uri Uri => this.uri;

        public WebSocketVersion Version => this.version;

        public int MaxFramePayloadLength => this.maxFramePayloadLength;

        public bool IsHandshakeComplete => this.handshakeComplete;

        void SetHandshakeComplete() => this.handshakeComplete = true;

        public string ExpectedSubprotocol => this.expectedSubprotocol;

        public string ActualSubprotocol
        { 
            get => this.actualSubprotocol;
            private set => this.actualSubprotocol = value;
        }

        protected abstract void Verify(IFullHttpResponse response);

        protected abstract IWebSocketFrameDecoder NewWebsocketDecoder();

        protected abstract IWebSocketFrameEncoder NewWebSocketEncoder();

        public Task CloseAsync(IChannel channel, CloseWebSocketFrame frame)
        {
            Contract.Requires(channel != null);
            return channel.WriteAndFlushAsync(frame);
        }

        static string RawPath(Uri wsUrl)
        {
            string path = wsUrl.AbsolutePath;
            string query = wsUrl.Query;
            if (!string.IsNullOrEmpty(query))
            {
                path = path + '?' + query;
            }

            return string.IsNullOrEmpty(path) ? "/" : path;
        }
        static string WebsocketHostValue(Uri wsUrl)
        {
            int port = wsUrl.Port;
            if (port == -1)
            {
                return wsUrl.Host;
            }
            string host = wsUrl.Host;
            if (port == HttpScheme.Http.Port)
            {
                return HttpScheme.Http.Name.ContentEquals(wsUrl.Scheme)
                    || WebSocketScheme.WS.Name.ContentEquals(wsUrl.Scheme)
                        ? host : NetUtil.ToSocketAddressString(host, port);
            }
            if (port == HttpScheme.Https.Port)
            {
                return HttpScheme.Https.Name.ToString().Equals(wsUrl.Scheme)
                    || WebSocketScheme.WSS.Name.ToString().Equals(wsUrl.Scheme) 
                        ? host : NetUtil.ToSocketAddressString(host, port);
            }

            // if the port is not standard (80/443) its needed to add the port to the header.
            // See http://tools.ietf.org/html/rfc6454#section-6.2
            return NetUtil.ToSocketAddressString(host, port);
        }

        static string WebsocketOriginValue(Uri wsUrl)
        {
            string scheme = wsUrl.Scheme;
            string schemePrefix;
            int port = wsUrl.Port;
            int defaultPort;

            if (WebSocketScheme.WSS.Name.ContentEquals(scheme)
                || HttpScheme.Https.Name.ContentEquals(scheme)
                || (scheme == null && port == WebSocketScheme.WSS.Port))
            {

                schemePrefix = HTTPS_SCHEME_PREFIX;
                defaultPort = WebSocketScheme.WSS.Port;
            }
            else
            {
                schemePrefix = HTTP_SCHEME_PREFIX;
                defaultPort = WebSocketScheme.WS.Port;
            }

            // Convert uri-host to lower case (by RFC 6454, chapter 4 "Origin of a URI")
            string host = wsUrl.Host.ToLower();

            if (port != defaultPort && port != -1)
            {
                // if the port is not standard (80/443) its needed to add the port to the header.
                // See http://tools.ietf.org/html/rfc6454#section-6.2
                return schemePrefix + NetUtil.ToSocketAddressString(host, port);
            }
            return schemePrefix + host;
        }
    }
}
