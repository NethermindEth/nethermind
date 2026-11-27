// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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

        /// <summary>Serializes a message into <paramref name="writer"/>, advancing it by exactly the message's length.</summary>
        /// <remarks>Requests the span for the whole message with a single <see cref="IBufferWriter{T}.GetSpan"/> call.</remarks>
        /// <exception cref="InvalidOperationException">The serializer wrote other than the length it reported.</exception>
        void Serialize<T>(T message, IBufferWriter<byte> writer) where T : MessageBase;

        T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase;

        /// <summary>Deserializes a received message from the content of <paramref name="packet"/>.</summary>
        /// <remarks>An <see cref="IOwningMessageSerializer{T}"/> gets a lease on the content, which its message may keep.</remarks>
        T Deserialize<T>(ZeroPacket packet) where T : MessageBase;
    }
}
