// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Codecs.Http.WebSockets
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using DotNetty.Codecs.Base64;
    using DotNetty.Buffers;
    using DotNetty.Common;

    static class WebSocketUtil
    {
        static readonly Random Random = new Random();

        static readonly ThreadLocalMD5 LocalMd5 = new ThreadLocalMD5();

        sealed class ThreadLocalMD5 : FastThreadLocal<MD5>
        {
            protected override MD5 GetInitialValue() => MD5.Create();
        }

        static readonly ThreadLocalSha1 LocalSha1 = new ThreadLocalSha1();

        sealed class ThreadLocalSha1 : FastThreadLocal<SHA1>
        {
            protected override SHA1 GetInitialValue() => SHA1.Create();
        }

        internal static byte[] Md5(byte[] data) => LocalMd5.Value.ComputeHash(data);

        internal static byte[] Sha1(byte[] data) => LocalSha1.Value.ComputeHash(data);

        internal static string Base64String(byte[] data)
        {
            IByteBuffer encodedData = Unpooled.WrappedBuffer(data);
            IByteBuffer encoded = Base64.Encode(encodedData);
            string encodedString = encoded.ToString(Encoding.UTF8);
            encoded.Release();
            return encodedString;
        }

        internal static byte[] RandomBytes(int size)
        {
            var bytes = new byte[size];
            Random.NextBytes(bytes);
            return bytes;
        }

        internal static int RandomNumber(int minimum, int maximum)
        {
            return unchecked(Random.Next() * maximum + minimum);
        }

        // Math.Random()
        internal static int RandomNext() => Random.Next();
    }
}
