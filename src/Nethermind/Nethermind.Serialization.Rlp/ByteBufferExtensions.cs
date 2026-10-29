// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
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

        /// <summary>Writes <paramref name="item"/> at the writer index of <paramref name="buffer"/>, and moves the writer index past it.</summary>
        public static void WriteRlp<T>(this IByteBuffer buffer, IRlpDecoder<T> decoder, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int length = decoder.GetLength(item, rlpBehaviors);
            RlpWriter writer = buffer.StartRlpWrite(length);
            decoder.Encode(ref writer, item, rlpBehaviors);
            buffer.CompleteRlpWrite(in writer, length);
        }

        // Decodes with the default RlpLimit item cap; preserved for existing callers.
        public static RlpByteArrayList DecodeRlpByteArrayList(this IByteBuffer byteBuffer) =>
            DecodeRlpByteArrayList(byteBuffer, null);

        /// <param name="limit">Caps the decoded item count. See <see cref="RlpByteArrayList.DecodeList"/>.</param>
        public static RlpByteArrayList DecodeRlpByteArrayList(this IByteBuffer byteBuffer, RlpLimit? limit)
        {
            NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
            RlpReader ctx = new(memoryOwner.Memory.Span);
            RlpByteArrayList? list = null;

            try
            {
                list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner, limit);
                memoryOwner = null;
                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.BytesRead);
                return list;
            }
            catch
            {
                list?.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }

        /// <summary>Reads RLP at the reader index of <paramref name="buffer"/>, and moves the reader index past the bytes that were read.</summary>
        [return: MaybeNull]
        public static T DeserializeRlp<T>(this IByteBuffer buffer, RefRlpReaderFunc<T> deserialize)
        {
            RlpReader reader = new(buffer.AsSpan());
            try
            {
                return deserialize(ref reader);
            }
            finally
            {
                buffer.SetReaderIndex(buffer.ReaderIndex + reader.BytesRead);
            }
        }

        /// <inheritdoc cref="DeserializeRlp{T}(IByteBuffer, RefRlpReaderFunc{T})"/>
        [return: MaybeNull]
        public static T DeserializeRlp<T>(this IByteBuffer buffer, IRlpDecoder<T> decoder, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpReader reader = new(buffer.AsSpan());
            try
            {
                return decoder.Decode(ref reader, rlpBehaviors);
            }
            finally
            {
                buffer.SetReaderIndex(buffer.ReaderIndex + reader.BytesRead);
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
