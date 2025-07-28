// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Common.Internal;

namespace Nethermind.Serialization.Rlp;

public static class NethermindBuffers
{
    /// <summary>
    /// General <see cref="IByteBufferAllocator"/> used for general purpose deserialization.
    /// This is separate from discovery and devp2p buffer allocator.
    /// </summary>
    public static IByteBufferAllocator Default = PooledByteBufferAllocator.Default;

    public static IByteBufferAllocator CreateAllocator(int arenaOrder, uint arenaCount)
    {
        return new PooledByteBufferAllocator(
            preferDirect: PlatformDependent.DirectBufferPreferred,
            nHeapArena: (int)arenaCount,
            nDirectArena: (int)arenaCount,
            pageSize: PooledByteBufferAllocator.DefaultPageSize,
            maxOrder: arenaOrder,
            tinyCacheSize: PooledByteBufferAllocator.DefaultTinyCacheSize,
            smallCacheSize: PooledByteBufferAllocator.DefaultSmallCacheSize,
            normalCacheSize: PooledByteBufferAllocator.DefaultNormalCacheSize
        );
    }
}
