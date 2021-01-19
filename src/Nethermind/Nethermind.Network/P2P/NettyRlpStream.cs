//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P
{
    public class NettyRlpStream : RlpStream
    {
        private readonly IByteBuffer _buffer;

        private int _initialPosition;

        public NettyRlpStream(IByteBuffer buffer)
        {
            _buffer = buffer;
            _initialPosition = buffer.ReaderIndex;
        }

        public override void Write(Span<byte> bytesToWrite)
        {
            _buffer.EnsureWritable(bytesToWrite.Length, true);

            Span<byte> target =
                _buffer.Array.AsSpan(_buffer.ArrayOffset + _buffer.WriterIndex, bytesToWrite.Length);
            bytesToWrite.CopyTo(target);
            int newWriterIndex = _buffer.WriterIndex + bytesToWrite.Length;

            _buffer.SetWriterIndex(newWriterIndex);
        }

        public override void WriteByte(byte byteToWrite)
        {
            _buffer.EnsureWritable(1, true);
            _buffer.WriteByte(byteToWrite);
        }

        protected override void WriteZero(int length)
        {
            _buffer.EnsureWritable(length, true);
            _buffer.WriteZero(length);
        }

        public override byte ReadByte()
        {
            return _buffer.ReadByte();
        }

        protected override Span<byte> Read(int length)
        {
            Span<byte> span = _buffer.Array.AsSpan(_buffer.ArrayOffset + _buffer.ReaderIndex, length);
            _buffer.SkipBytes(span.Length);
            return span;
        }

        public override byte PeekByte()
        {
            byte result = _buffer.ReadByte();
            _buffer.SetReaderIndex(_buffer.ReaderIndex - 1);
            return result;
        }

        protected override byte PeekByte(int offset)
        {
            _buffer.MarkReaderIndex();
            _buffer.SkipBytes(offset);
            byte result = _buffer.ReadByte();
            _buffer.ResetReaderIndex();
            return result;
        }

        protected override void SkipBytes(int length)
        {
            _buffer.SkipBytes(length);
        }

        public override int Position
        {
            get => _buffer.ReaderIndex - _initialPosition;
            set => _buffer.SetReaderIndex(_initialPosition + value);
        }

        public override int Length => _buffer.ReadableBytes + (_buffer.ReaderIndex - _initialPosition);

        protected override string Description => "|NettyRlpStream|description missing|";
    }
}
