// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Buffers.Slab;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>The native allocator behind PBT node-group payloads, which the trie node cache retains long-term.</summary>
/// <remarks>
/// Mainnet groups cluster between ~200 B (single account branches) and ~1.5 KB (dense trie heads), so
/// the classes run 32 B apart up to 1 KiB and 64 B apart to 2 KiB, bounding the rounding loss to a few
/// percent across that whole band; the explicit top class holds the writer's largest possible payload
/// so it never falls through to a dedicated allocation.
/// </remarks>
public static class PbtNodeGroupMemory
{
    private const int Quantum = 32;
    private const int ClassesPerDoubling = 16;
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
