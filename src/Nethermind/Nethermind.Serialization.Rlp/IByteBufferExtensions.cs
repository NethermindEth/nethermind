// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    public static class IByteBufferExtensions
    {
        public static byte[] ReadAllBytes(this IByteBuffer buffer)
        {
            byte[] bytes = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(bytes);
            return bytes;
        }

        public static string ReadAllHex(this IByteBuffer buffer)
        {
            byte[] bytes = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(bytes);
            return bytes.ToHexString();
        }

        public static void MakeSpace(this IByteBuffer output, int length, string reason = null)
        {
            if (output.WritableBytes < length)
            {
                if (output.ReaderIndex == output.WriterIndex)
                {
                    output.Clear();
                }
                else
                {
                    output.DiscardReadBytes();
                }

                if (output.WritableBytes < length)
                {
                    output.EnsureWritable(length, true);
                }
            }
        }

        /// <summary>
        /// Return readable space of this byte buffer as a span.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static Span<byte> AsSpan(this IByteBuffer buffer)
        {
            if (!buffer.HasArray) throw new InvalidOperationException("Byte buffer does not have array backing");
            return buffer.Array.AsSpan()
                .Slice(buffer.ArrayOffset + buffer.ReaderIndex, buffer.WriterIndex - buffer.ReaderIndex);
        }
    }
}
