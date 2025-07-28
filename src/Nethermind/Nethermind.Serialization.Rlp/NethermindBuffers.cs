// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Serialization.Rlp;

public class NethermindBuffers
{
    /// <summary>
    /// General <see cref="IByteBufferAllocator"/> used for general purpose deserialization.
    /// This is separate from discovery and devp2p buffer allocator.
    /// </summary>
    public static IByteBufferAllocator Default = PooledByteBufferAllocator.Default;
}
