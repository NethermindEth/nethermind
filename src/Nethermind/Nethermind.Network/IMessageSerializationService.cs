// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
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
        IByteBuffer ZeroSerialize<T>(T message, IByteBufferAllocator? allocator = null) where T : MessageBase;
        T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase;

        /// <summary>Deserializes a received message from the content of <paramref name="packet"/>.</summary>
        /// <remarks>An <see cref="IOwningMessageSerializer{T}"/> gets a lease on the content, which its message may keep.</remarks>
        T Deserialize<T>(ZeroPacket packet) where T : MessageBase;
    }
}
