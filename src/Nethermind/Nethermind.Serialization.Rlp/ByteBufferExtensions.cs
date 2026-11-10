// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp
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

        public static void WriteBytes(this IByteBuffer buffer, scoped ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                buffer.WriteByte(bytes[i]);
            }
        }

        /// <summary>Starts to write exactly <paramref name="length"/> bytes of RLP at the writer index of <paramref name="buffer"/>.</summary>
        /// <remarks>
        /// The writer writes straight into the array of the buffer. Write the items, then call
        /// <see cref="CompleteRlpWrite"/> to move the writer index.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The buffer has no array.</exception>
        public static RlpWriter StartRlpWrite(this IByteBuffer buffer, int length)
        {
            buffer.EnsureWritable(length);
            if (!buffer.HasArray) ThrowNoArray();
            return RlpWriter.ContentWriter(buffer.Array.AsSpan(buffer.ArrayOffset + buffer.WriterIndex, length));
        }

        /// <summary>Ends a write that <see cref="StartRlpWrite"/> started, and moves the writer index past it.</summary>
        /// <exception cref="InvalidOperationException"><paramref name="writer"/> wrote another number of bytes than <paramref name="length"/>.</exception>
        public static void CompleteRlpWrite(this IByteBuffer buffer, scoped in RlpWriter writer, int length)
        {
            Rlp.CheckLength(in writer, length);
            buffer.SetWriterIndex(buffer.WriterIndex + length);
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

        public static Span<byte> AsSpan(this IByteBuffer buffer, int? startIndex = null)
        {
            if (!buffer.HasArray) throw new InvalidOperationException("Byte buffer does not have array backing");
            int startIdx = startIndex ?? buffer.ReaderIndex;
            return buffer.Array.AsSpan()
                .Slice(buffer.ArrayOffset + startIdx, buffer.WriterIndex - startIdx);
        }

        public static Memory<byte> AsMemory(this IByteBuffer buffer, int? startIndex = null)
        {
            if (!buffer.HasArray) throw new InvalidOperationException("Byte buffer does not have array backing");
            int startIdx = startIndex ?? buffer.ReaderIndex;
            return buffer.Array.AsMemory()
                .Slice(buffer.ArrayOffset + startIdx, buffer.WriterIndex - startIdx);
        }

        [DoesNotReturn]
        private static void ThrowNoArray() => throw new InvalidOperationException("Byte buffer does not have array backing");
    }
}
