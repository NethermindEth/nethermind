// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>The native pool behind PBT node-group payloads, which the trie node cache retains long-term.</summary>
/// <remarks>
/// Mainnet groups cluster between ~200 B (single account branches) and ~1.5 KB (dense trie heads), so
/// the buckets run 32 B apart up to 1 KiB and 64 B apart to 2 KiB, bounding the rounding loss to a few
/// percent across that whole band; the top bucket holds the largest payload a group may take, so no
/// payload falls through to a dedicated allocation.
/// </remarks>
public static class PbtNodeGroupMemory
{
    private const int Quantum = 32;
    private const int BucketsPerDoubling = 16;
    private const int MaxRetainedBytesPerBucket = 1024 * 1024;

    public static NativeBucketedMemoryPool CreateNativePool() =>
        new(NativeBucketedMemoryPool.GenerateBucketSizes(Quantum, BucketsPerDoubling, PbtNodeGroupCodec.MaxPayloadLength), MaxRetainedBytesPerBucket);

    /// <summary>The native pool, or the pooled one when <see cref="IPbtConfig.NativeNodeGroupMemory"/> is off.</summary>
    public static IRefCountingMemoryProvider CreateProvider(IPbtConfig config) =>
        config.NativeNodeGroupMemory ? CreateNativePool() : PooledRefCountingMemoryProvider.Instance;
}
