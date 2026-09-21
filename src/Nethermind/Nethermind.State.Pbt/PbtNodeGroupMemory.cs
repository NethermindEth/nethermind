// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Buffers.Slab;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>The native allocator behind PBT node-group payloads, which the trie node cache retains long-term.</summary>
/// <remarks>
/// Four classes per doubling keep a typical ~1.2 KB group within a few percent of its class instead of
/// a power-of-two bucket, and the explicit top class holds the writer's largest possible payload so it
/// never falls through to a dedicated allocation.
/// </remarks>
public static class PbtNodeGroupMemory
{
    private const int Quantum = 64;
    private const int ClassesPerDoubling = 4;
    private const int MaxGeneratedClass = 64 * 1024;
    private const int MaxPayloadClass = (PbtNodeGroupCodec.MaxPayloadLength + Quantum - 1) / Quantum * Quantum;

    public static SlabAllocatorOptions CreateOptions()
    {
        int[] generated = SlabAllocatorOptions.GenerateSizeClasses(Quantum, ClassesPerDoubling, MaxGeneratedClass);
        int[] classes = new int[generated.Length + 1];
        generated.CopyTo(classes, 0);
        classes[^1] = MaxPayloadClass;
        return new SlabAllocatorOptions(classes, SlabAllocatorOptions.DefaultPageSize, Quantum, SlabAllocatorOptions.DefaultThreadCacheMaxCount);
    }

    public static SlabRefCountingMemoryProvider CreateSlabProvider() => new(new SlabMemoryAllocator(CreateOptions()));

    /// <summary>The slab provider, or the pooled one when <see cref="IPbtConfig.NativeNodeGroupMemory"/> is off.</summary>
    public static IRefCountingMemoryProvider CreateProvider(IPbtConfig config) =>
        config.NativeNodeGroupMemory ? CreateSlabProvider() : PooledRefCountingMemoryProvider.Instance;
}
