// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using DotNetty.Buffers;
using DotNetty.Common.Internal;
using Nethermind.Core.Attributes;

namespace Nethermind.Serialization.Rlp;

public static class NethermindBuffers
{
    /// <summary>
    /// General <see cref="IByteBufferAllocator"/> used for general purpose deserialization.
    /// </summary>
    public static IByteBufferAllocator Default = PooledByteBufferAllocator.Default;

    /// <summary>
    /// Allocator used for serializing and deserializing rlpx messages
    /// </summary>
    public static IByteBufferAllocator RlpxAllocator = PooledByteBufferAllocator.Default;

    /// <summary>
    /// Similar to <see cref="RlpxAllocator"/> but for discovery messages.
    /// </summary>
    public static IByteBufferAllocator DiscoveryAllocator = PooledByteBufferAllocator.Default;

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

public static class Metrics
{
    [KeyIsLabel("allocator")]
    public static ConcurrentDictionary<string, double> AllocatorArenaCount { get; } = new();

    [KeyIsLabel("allocator")]
    public static ConcurrentDictionary<string, double> AllocatorChunkSize { get; } = new();

    [KeyIsLabel("allocator")]
    public static ConcurrentDictionary<string, double> AllocatorUsedHeapMemory { get; } = new();

    [KeyIsLabel("allocator")]
    public static ConcurrentDictionary<string, double> AllocatorUsedDirectMemory { get; } = new();

    [KeyIsLabel("allocator")]
    public static ConcurrentDictionary<string, double> AllocatorActiveAllocations { get; } = new();

    [KeyIsLabel("allocator")]
    public static ConcurrentDictionary<string, double> AllocatorActiveAllocationBytes { get; } = new();

    [KeyIsLabel("allocator")]
    public static ConcurrentDictionary<string, double> AllocatorAllocations { get; } = new();

    [KeyIsLabel("allocator")]
    public static ConcurrentDictionary<string, double> AllocatorAllocationBytes { get; } = new();
}
