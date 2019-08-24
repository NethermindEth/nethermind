/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Security.Cryptography;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NettyRlpStream : RlpStream
    {
        private readonly IByteBuffer _byteBuffer;

        private int _initialPosition;
        
        public NettyRlpStream(IByteBuffer byteBuffer)
        {
            _byteBuffer = byteBuffer;
            _initialPosition = byteBuffer.ReaderIndex;
        }

        protected override void Write(Span<byte> bytesToWrite)
        {
            _byteBuffer.MakeSpace(bytesToWrite.Length);

            bytesToWrite.CopyTo(_byteBuffer.Array.AsSpan().Slice(_byteBuffer.ArrayOffset + _byteBuffer.WriterIndex, bytesToWrite.Length));
            int newWriterIndex = _byteBuffer.WriterIndex + bytesToWrite.Length;

            _byteBuffer.SetWriterIndex(newWriterIndex);
        }

        protected override void WriteByte(byte byteToWrite)
        {
            _byteBuffer.MakeSpace(1);
            _byteBuffer.WriteByte(byteToWrite);
        }

        protected override void WriteZero(int length)
        {
            _byteBuffer.MakeSpace(length);
            _byteBuffer.WriteZero(length);
        }

        public override byte ReadByte()
        {
            return _byteBuffer.ReadByte();
        }

        [Todo(Improve.Allocations, "work in progress")]
        protected override Span<byte> Read(int length)
        {
            return _byteBuffer.ReadBytes(length).ReadAllBytes().AsSpan();
        }

        protected override byte PeekByte()
        {
            byte result = _byteBuffer.ReadByte();
            _byteBuffer.SetReaderIndex(_byteBuffer.ReaderIndex - 1);
            return result;
        }

        protected override byte PeekByte(int offset)
        {
            _byteBuffer.MarkReaderIndex();
            _byteBuffer.SkipBytes(offset);
            byte result = _byteBuffer.ReadByte();
            _byteBuffer.ResetReaderIndex();
            return result;
        }

        protected override void SkipBytes(int length)
        {
            _byteBuffer.SkipBytes(length);
        }

        public override int Position
        {
            get => _byteBuffer.ReaderIndex - _initialPosition;
            set => _byteBuffer.SetReaderIndex(_initialPosition + value);
        }

        public override int Length => _byteBuffer.ReadableBytes + (_byteBuffer.ReaderIndex - _initialPosition);

        protected override string Description => "|NettyRlpStream|description missing|";
    }
}