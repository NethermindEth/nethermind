// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

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
}
