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
using Nethermind.Core.Extensions;

namespace Nethermind.Network
{
    public static class ByteBufferExtensions
    {
        public static byte[] ReadAllBytesAsArray(this IByteBuffer buffer)
        {
            byte[] bytes = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(bytes);
            return bytes;
        }

        public static Span<byte> ReadAllBytesAsSpan(this IByteBuffer buffer)
        {
            if (buffer.HasArray)
            {
                Span<byte> bytes = buffer.Array.AsSpan(buffer.ArrayOffset + buffer.ReaderIndex, buffer.ReadableBytes);
                buffer.SetReaderIndex(buffer.WriterIndex);
                return bytes;
            }
            else
            {
                return buffer.ReadAllBytesAsArray().AsSpan();
            }
        }

        public static Memory<byte> ReadAllBytesAsMemory(this IByteBuffer buffer)
        {
            if (buffer.HasArray)
            {
                Memory<byte> bytes = buffer.Array.AsMemory(buffer.ArrayOffset + buffer.ReaderIndex, buffer.ReadableBytes);
                buffer.SetReaderIndex(buffer.WriterIndex);
                return bytes;
            }
            else
            {
                return buffer.ReadAllBytesAsArray().AsMemory();
            }
        }

        public static string ReadAllHex(this IByteBuffer buffer) => buffer.ReadAllBytesAsSpan().ToHexString();

        public static void WriteBytes(this IByteBuffer buffer, ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                buffer.WriteByte(bytes[i]);
            }
        }
    }
}
