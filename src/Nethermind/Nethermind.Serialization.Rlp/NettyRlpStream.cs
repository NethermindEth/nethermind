// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;

namespace Nethermind.Serialization.Rlp
{
    public sealed class NettyRlpStream : RlpStream, IDisposable
    {
        private readonly IByteBuffer _buffer;

        private readonly int _initialPosition;

        public NettyRlpStream(IByteBuffer buffer)
        {
            _buffer = buffer;
            _initialPosition = buffer.ReaderIndex;
        }

        public override void Write(ReadOnlySpan<byte> bytesToWrite)
        {
            _buffer.EnsureWritable(bytesToWrite.Length);

            Span<byte> target =
                _buffer.Array.AsSpan(_buffer.ArrayOffset + _buffer.WriterIndex, bytesToWrite.Length);
            bytesToWrite.CopyTo(target);
            int newWriterIndex = _buffer.WriterIndex + bytesToWrite.Length;

            _buffer.SetWriterIndex(newWriterIndex);
        }

        public override void WriteByte(byte byteToWrite)
        {
            _buffer.EnsureWritable(1);
            _buffer.WriteByte(byteToWrite);
        }

        protected override void WriteZero(int length)
        {
            _buffer.EnsureWritable(length);
            _buffer.WriteZero(length);
        }

        public override int Position
        {
            get => _buffer.ReaderIndex - _initialPosition;
            set => _buffer.SetReaderIndex(_initialPosition + value);
        }

        public override int Length => _buffer.ReadableBytes + (_buffer.ReaderIndex - _initialPosition);

        protected override string Description => "|NettyRlpStream|description missing|";

        /// <summary>
        /// Note: this include already read bytes, not just the remaining one.
        /// </summary>
        /// <returns></returns>
        public Span<byte> AsSpan() => _buffer.AsSpan(_initialPosition);

        public Memory<byte> AsMemory() => _buffer.AsMemory(_initialPosition);

        public void Dispose()
        {
            _buffer.SafeRelease();
        }
    }
}
