// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;

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

        public static bool TryWriteRlpByteArrayList(this IByteBuffer byteBuffer, IByteArrayList list)
        {
            if (list is not IRlpWrapper rlpWrapper) return false;
            byteBuffer.WriteRlpWrapper(rlpWrapper);
            return true;
        }

        public static void WriteRlpWrapper(this IByteBuffer byteBuffer, IRlpWrapper rlpWrapper)
        {
            byteBuffer.EnsureWritable(rlpWrapper.RlpLength);
            ByteBufferRlpWriter writer = new(byteBuffer);
            rlpWrapper.Write(ref writer);
        }

        public static void WriteRlpByteArrayList(this IByteBuffer byteBuffer, IByteArrayList list)
        {
            if (byteBuffer.TryWriteRlpByteArrayList(list))
                return;

            int contentLength = 0;
            for (int i = 0; i < list.Count; i++)
            {
                contentLength += Rlp.LengthOf(list[i]);
            }

            int length = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(length);
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(contentLength);
            for (int i = 0; i < list.Count; i++)
            {
                writer.Encode(list[i]);
            }
        }

        public static RlpByteArrayList DecodeRlpByteArrayList(this IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
            RlpReader ctx = new(memoryOwner.Memory, true);
            int startPos = ctx.Position;
            RlpByteArrayList? list = null;

            try
            {
                list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner);
                memoryOwner = null;
                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));
                return list;
            }
            catch
            {
                list?.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }

        public static T DeserializeRlp<T>(this IByteBuffer buffer, DecodeRlpValue<T> deserialize)
        {
            RlpReader ctx = new(buffer.AsSpan());
            try
            {
                return deserialize(ref ctx);
            }
            finally
            {
                buffer.SetReaderIndex(buffer.ReaderIndex + ctx.Position);
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
    }
}
