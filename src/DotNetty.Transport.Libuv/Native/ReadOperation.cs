// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// ReSharper disable ConvertToAutoProperty
// ReSharper disable ConvertToAutoPropertyWithPrivateSetter
// ReSharper disable ConvertToAutoPropertyWhenPossible
namespace DotNetty.Transport.Libuv.Native
{
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using DotNetty.Buffers;

    public sealed class ReadOperation : IDisposable
    {
        readonly IByteBuffer buffer;
        readonly INativeUnsafe nativeUnsafe;

        GCHandle gcHandle;
        int status;
        bool endOfStream;
        OperationException error;

        internal ReadOperation(INativeUnsafe nativeUnsafe, IByteBuffer buffer)
        {
            this.nativeUnsafe = nativeUnsafe;
            this.buffer = buffer;
            this.status = 0;
            this.endOfStream = false;
        }

        internal IByteBuffer Buffer => this.buffer;

        internal OperationException Error => this.error;

        internal int Status => this.status;

        internal bool EndOfStream => this.endOfStream;

        internal void Complete(int statusCode, OperationException operationException)
        {
            this.Release();

            this.status = statusCode;
            this.endOfStream = statusCode == NativeMethods.EOF;
            this.error = operationException;
            this.nativeUnsafe.FinishRead(this);
        }

        internal uv_buf_t GetBuffer()
        {
            Debug.Assert(!this.gcHandle.IsAllocated, $"{nameof(ReadOperation)} has already been initialized and not released yet.");

            IByteBuffer buf = this.buffer;
            this.gcHandle = GCHandle.Alloc(buf.Array, GCHandleType.Pinned);
            IntPtr arrayHandle = this.gcHandle.AddrOfPinnedObject();

            int index = buf.ArrayOffset + buf.WriterIndex;
            int length = buf.WritableBytes;

            return new uv_buf_t(arrayHandle + index, length);
        }

        void Release()
        {
            if (this.gcHandle.IsAllocated)
            {
                this.gcHandle.Free();
            }
        }

        public void Dispose() => this.Release();
    }
}
