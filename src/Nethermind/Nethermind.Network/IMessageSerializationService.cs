// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network
{
    public interface IMessageSerializationService
    {
        /// <summary>
        /// Serializes a message into a pooled buffer of exactly its length.
        /// </summary>
        /// <returns>The serialized message, which the caller disposes to return it to the pool.</returns>
        ArrayPoolSpan<byte> Serialize<T>(T message) where T : MessageBase;

        /// <summary>Returns the length of a message serialized by <see cref="Serialize{T}(T, Span{byte})"/>.</summary>
        int GetLength<T>(T message) where T : MessageBase;

        /// <summary>Serializes a message into <paramref name="destination"/>, which is exactly its length.</summary>
        /// <exception cref="InvalidOperationException">The serializer wrote other than the length it reported.</exception>
        void Serialize<T>(T message, Span<byte> destination) where T : MessageBase;

        T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase;

        /// <summary>Deserializes a received message from the content of <paramref name="packet"/>.</summary>
        /// <remarks>An <see cref="IOwningMessageSerializer{T}"/> gets a lease on the content, which its message may keep.</remarks>
        T Deserialize<T>(ZeroPacket packet) where T : MessageBase;
    }
}
