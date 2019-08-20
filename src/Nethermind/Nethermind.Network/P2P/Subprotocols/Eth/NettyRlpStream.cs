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
using DotNetty.Buffers;
using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NettyRlpStream : RlpStream
    {
        private readonly IByteBuffer _byteBuffer;

        public NettyRlpStream(IByteBuffer byteBuffer)
        {
            _byteBuffer = byteBuffer;
        }

        protected override void Write(Span<byte> bytesToWrite)
        {
            if (_byteBuffer.WritableBytes < bytesToWrite.Length)
            {
                _byteBuffer.DiscardReadBytes();
            }

            bytesToWrite.CopyTo(_byteBuffer.Array.AsSpan().Slice(_byteBuffer.ArrayOffset + _byteBuffer.WriterIndex, bytesToWrite.Length));
            int newWriterIndex = _byteBuffer.WriterIndex + bytesToWrite.Length;

            _byteBuffer.SetWriterIndex(newWriterIndex);
        }

        protected override void WriteByte(byte byteToWrite)
        {
            if (_byteBuffer.WritableBytes == 0)
            {
                _byteBuffer.DiscardReadBytes();
            }
            
            _byteBuffer.WriteByte(byteToWrite);
        }

        protected override void WriteZero(int length)
        {
            if (_byteBuffer.WritableBytes < length)
            {
                _byteBuffer.DiscardReadBytes();
            }
            
            _byteBuffer.WriteZero(length);
        }
    }
}