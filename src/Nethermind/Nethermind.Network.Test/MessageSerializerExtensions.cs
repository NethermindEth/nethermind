// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
