// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public static class MessageSerializerExtensions
{
    /// <summary>
    /// Serializes a message into a new array of the length the serializer reports.
    /// </summary>
    public static byte[] SerializeToArray<T>(this IMessageSerializer<T> serializer, T message) where T : MessageBase
    {
        byte[] data = new byte[serializer.GetLength(message)];
        Assert.That(serializer.Serialize(data, message), Is.EqualTo(data.Length), "bytes written");
        return data;
    }

    /// <summary>
    /// Appends a message to <paramref name="buffer"/>.
    /// </summary>
    public static void Serialize<T>(this IMessageSerializer<T> serializer, IByteBuffer buffer, T message) where T : MessageBase
    {
        int length = serializer.GetLength(message);
        buffer.EnsureWritable(length);
        Assert.That(serializer.Serialize(buffer.Array.AsSpan(buffer.ArrayOffset + buffer.WriterIndex, length), message), Is.EqualTo(length), "bytes written");
        buffer.SetWriterIndex(buffer.WriterIndex + length);
    }

    /// <summary>
    /// Reads a message from the readable bytes of <paramref name="buffer"/> and marks them read, as
    /// <see cref="MessageSerializationService"/> does.
    /// </summary>
    public static T Deserialize<T>(this IMessageSerializer<T> serializer, IByteBuffer buffer) where T : MessageBase
    {
        T message = serializer.Deserialize(buffer.AsSpan());
        buffer.SetReaderIndex(buffer.WriterIndex);
        return message;
    }
}
