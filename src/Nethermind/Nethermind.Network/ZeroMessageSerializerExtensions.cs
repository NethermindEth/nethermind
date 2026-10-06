// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Network
{
    public static class ZeroMessageSerializerExtensions
    {
        public static byte[] Serialize<T>(this IZeroMessageSerializer<T> serializer, T message) where T : MessageBase
        {
            using PooledBuffer buffer = PooledBuffer.Rent(serializer.GetLength(message, out _));
            serializer.Serialize(buffer.Span, message);
            return buffer.ReadOnlySpan.ToArray();
        }

        public static T Deserialize<T>(this IZeroMessageSerializer<T> serializer, byte[] message) where T : MessageBase =>
            serializer.Deserialize(message, out _);
    }
}
