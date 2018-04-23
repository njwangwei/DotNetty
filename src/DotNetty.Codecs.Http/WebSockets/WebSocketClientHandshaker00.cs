// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Codecs.Http.WebSockets
{
    using System;
    using DotNetty.Buffers;
    using DotNetty.Common.Utilities;

    public class WebSocketClientHandshaker00 : WebSocketClientHandshaker
    {
        static readonly AsciiString WEBSOCKET = AsciiString.Cached("WebSocket");

        IByteBuffer expectedChallengeResponseBytes;

        public WebSocketClientHandshaker00(Uri webSocketUrl, WebSocketVersion version, string subprotocol,
            HttpHeaders customHeaders, int maxFramePayloadLength)
            : base(webSocketUrl, version, subprotocol, customHeaders, maxFramePayloadLength)
        {
        }

        protected override IFullHttpRequest NewHandshakeRequest()
        {
            // Make keys
            int spaces1 = WebSocketUtil.RandomNumber(1, 12);
            int spaces2 = WebSocketUtil.RandomNumber(1, 12);

            int max1 = int.MaxValue / spaces1;
            int max2 = int.MaxValue / spaces2;

            int number1 = WebSocketUtil.RandomNumber(0, max1);
            int number2 = WebSocketUtil.RandomNumber(0, max2);

            int product1 = number1 * spaces1;
            int product2 = number2 * spaces2;

            string key1 = Convert.ToString(product1);
            string key2 = Convert.ToString(product2);

            throw new NotImplementedException();
        }

        protected override void Verify(IFullHttpResponse response)
        {
            throw new NotImplementedException();
        }

        static string InsertRandomCharacters(string key)
        {
            int count = WebSocketUtil.RandomNumber(1, 12);

            var randomChars = new char[count];
            int randCount = 0;
            while (randCount < count)
            {
                int rand = unchecked(WebSocketUtil.RandomNext() * 0x7e + 0x21);
                if (0x21 < rand && rand < 0x2f || 0x3a < rand && rand < 0x7e)
                {
                    randomChars[randCount] = (char)rand;
                    randCount += 1;
                }
            }

            for (int i = 0; i < count; i++)
            {
                int split = WebSocketUtil.RandomNumber(0, key.Length);
                string part1 = key.Substring(0, split);
                string part2 = key.Substring(split);
                key = part1 + randomChars[i] + part2;
            }

            return key;
        }

        static string InsertSpaces(string key, int spaces)
        {
            for (int i = 0; i < spaces; i++)
            {
                int split = WebSocketUtil.RandomNumber(1, key.Length - 1);
                string part1 = key.Substring(0, split);
                string part2 = key.Substring(split);
                key = part1 + ' ' + part2;
            }

            return key;
        }

        protected override IWebSocketFrameDecoder NewWebsocketDecoder() => new WebSocket00FrameDecoder(this.MaxFramePayloadLength);

        protected override IWebSocketFrameEncoder NewWebSocketEncoder() => new WebSocket00FrameEncoder();
    }
}
