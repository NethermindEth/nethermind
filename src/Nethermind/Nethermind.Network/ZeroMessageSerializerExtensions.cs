// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    public static class ZeroMessageSerializerExtensions
    {
        public static byte[] Serialize<T>(this IZeroMessageSerializer<T> serializer, T message) where T : MessageBase
        {
            IByteBuffer byteBuffer = UnpooledByteBufferAllocator.Default.Buffer(
                serializer is IZeroInnerMessageSerializer<T> zeroInnerMessageSerializer
                    ? zeroInnerMessageSerializer.GetLength(message, out _)
                    : 64);
            try
            {
                serializer.Serialize(byteBuffer, message);
                return byteBuffer.ReadAllBytesAsArray();

            }
            finally
            {
                byteBuffer.SafeRelease();
            }
        }

        public static T Deserialize<T>(this IZeroMessageSerializer<T> serializer, byte[] message) where T : MessageBase
        {
            IByteBuffer buffer = UnpooledByteBufferAllocator.Default.Buffer(message.Length);
            try
            {
                buffer.WriteBytes(message);
                return serializer.Deserialize(buffer);
            }
            finally
            {
                buffer.SafeRelease();
            }
        }
    }
}
