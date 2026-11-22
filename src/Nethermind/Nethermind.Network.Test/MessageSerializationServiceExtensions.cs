// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Test;

public static class MessageSerializationServiceExtensions
{
    /// <summary>
    /// Serializes a message whose serializer is an <see cref="IMessageSerializer{T}"/> into a new array.
    /// </summary>
    public static byte[] SerializeToArray<T>(this IMessageSerializationService service, T message) where T : MessageBase
    {
        using ArrayPoolSpan<byte> packet = service.Serialize(message);
        return ((ReadOnlySpan<byte>)packet).ToArray();
    }

    /// <summary>
    /// Serializes a message into a new buffer as <see cref="PacketSender"/> sends it: its packet type followed by its content.
    /// </summary>
    public static IByteBuffer ZeroSerialize<T>(this IMessageSerializationService service, T message) where T : P2PMessage
        => PacketSender.Serialize(service, message, NethermindBuffers.Default);
}
