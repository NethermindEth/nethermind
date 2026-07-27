// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.State;

/// <summary>
/// Per-scope accumulator for world-state metric counters, flushed into the global <see cref="Metrics"/> /
/// <c>Db.Metrics</c> by the owning <c>WorldState</c> at commit and scope end.
/// </summary>
/// <remarks>
/// A scope is only ever touched by one thread, so increments are plain non-atomic adds - avoiding the
/// per-access cross-thread <see cref="System.Threading.Interlocked"/> contention during prewarm /
/// parallel-BAL. Write counters mirror the <see cref="ExecutionMetricsFlag"/> gating of their globals.
/// </remarks>
public sealed class LocalMetrics
{
    // Field order matches the global declaration order so the flush walks memory forward.
    public long StateTreeCacheHits;
    public long StateTreeReads;
    public long StateTreeWrites;
    public long StateSkippedWrites;
    public long StorageTreeCache;
    public long StorageTreeReads;
    // Pre-block (prewarmer-shared) cache probes, split from the per-block change-dict layer above:
    // hits+misses = first-in-block touches only, so hit rate here = prewarm coverage.
    public long PreBlockAccountHits;
    public long PreBlockAccountMisses;
    public long PreBlockStorageHits;
    public long PreBlockStorageMisses;
    // Storage write/skipped counters are deliberately absent: reported from parallel worker finalizers,
    // so they go straight to the atomic global Db.Metrics (see PersistentStorageProvider.ReportMetrics).

    // Execution write counters - gated by ExecutionMetricsFlag, matching Metrics.Increment*.
    public long AccountWrites;
    public long AccountDeleted;
    public long StorageWrites;
    public long CodeWrites;
    public long CodeBytesWritten;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementStateTreeReads() => StateTreeReads++;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementStateTreeCacheHits() => StateTreeCacheHits++;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementStateTreeWrites(long count) => StateTreeWrites += count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementStateSkippedWrites(long count) => StateSkippedWrites += count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementStorageTreeCache() => StorageTreeCache++;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementStorageTreeReads() => StorageTreeReads++;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementPreBlockAccountHits() => PreBlockAccountHits++;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementPreBlockAccountMisses() => PreBlockAccountMisses++;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementPreBlockStorageHits() => PreBlockStorageHits++;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementPreBlockStorageMisses() => PreBlockStorageMisses++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementStorageWrites()
    {
        if (ExecutionMetricsFlag.IsActive) StorageWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementAccountWrites()
    {
        if (ExecutionMetricsFlag.IsActive) AccountWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementAccountDeleted()
    {
        if (ExecutionMetricsFlag.IsActive) AccountDeleted++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementCodeWrites()
    {
        if (ExecutionMetricsFlag.IsActive) CodeWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementCodeBytesWritten(int bytes)
    {
        if (ExecutionMetricsFlag.IsActive) CodeBytesWritten += bytes;
    }

    public void Reset()
    {
        StateTreeCacheHits = 0;
        StateTreeReads = 0;
        StateTreeWrites = 0;
        StateSkippedWrites = 0;
        StorageTreeCache = 0;
        StorageTreeReads = 0;
        PreBlockAccountHits = 0;
        PreBlockAccountMisses = 0;
        PreBlockStorageHits = 0;
        PreBlockStorageMisses = 0;
        AccountWrites = 0;
        AccountDeleted = 0;
        StorageWrites = 0;
        CodeWrites = 0;
        CodeBytesWritten = 0;
    }
}
