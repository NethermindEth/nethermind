// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public static void MarkIndex(this IByteBuffer buffer)
        {
            buffer.MarkReaderIndex();
            buffer.MarkWriterIndex();
        }

        public static void ResetIndex(this IByteBuffer buffer)
        {
            buffer.ResetReaderIndex();
            buffer.ResetWriterIndex();
        }
    }
}
