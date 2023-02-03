// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Extensions;

namespace Nethermind.Network
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
    }
}
