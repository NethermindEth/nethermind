// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Network.Rlpx
{
    /// <summary>
    /// A received message, as the transport hands it to the session and its protocol handlers.
    /// </summary>
    public abstract class ZeroPacket
    {
        public string? Protocol { get; set; }
        public byte PacketType { get; set; }

        /// <summary>The message bytes, after the packet type.</summary>
        /// <remarks>
        /// Valid only while the packet is being handled: the transport may reuse the memory afterwards. A consumer
        /// that keeps the bytes takes <see cref="LeaseContent"/>.
        /// </remarks>
        public abstract Memory<byte> Content { get; }

        /// <summary>Returns an owner that keeps <see cref="Content"/> valid until it is disposed.</summary>
        /// <remarks>The owner's memory need not start at <see cref="Content"/>, so read the bytes from <see cref="Content"/>.</remarks>
        public abstract IMemoryOwner<byte> LeaseContent();
    }
}
