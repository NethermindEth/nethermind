// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.State;

/// <summary>
/// Per-scope, single-threaded accumulator for execution metric counters incremented from the
/// world-state layer (state/storage trie reads, cache hits, writes, and the execution write
/// counters).
/// </summary>
/// <remarks>
/// A world-state scope is only ever touched by one thread at a time, so increments here are plain
/// non-atomic adds. This avoids the cross-thread <see cref="System.Threading.Interlocked"/>
/// contention that arises when many prewarm / parallel-BAL workers update the same global counter
/// line. The owning <c>WorldState</c> folds these into the global <see cref="Metrics"/> /
/// <c>Db.Metrics</c> counters once per boundary (per-tx commit and scope end) via its flush, then
/// calls <see cref="Reset"/>. The execution write counters mirror the
/// <see cref="ExecutionMetricsFlag"/> gating of their <see cref="Metrics"/> counterparts so a
/// <c>NETHERMIND_NO_EXECUTION_METRICS</c> build still elides them.
/// </remarks>
public sealed class LocalMetrics
{
    // Field order mirrors the declaration order of the static Db.Metrics / Evm.Metrics counters so
    // the flush adds across them in progressive memory order rather than jumping back and forth.

    // Db state/storage trie counters — always recorded (not gated).
    public long StateTreeCacheHits;
    public long StateTreeReads;
    public long StateTreeWrites;
    public long StateSkippedWrites;
    public long StorageTreeCache;
    public long StorageTreeReads;
    // Note: storage trie write/skipped counters are NOT here - they are reported from
    // ParallelUnbalancedWork worker finalizers (PersistentStorageProvider.ReportMetrics) and so go
    // straight to the atomic global Db.Metrics, which a non-atomic per-scope accumulator cannot.

    // Execution write counters — gated by ExecutionMetricsFlag, matching Metrics.Increment*.
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
        AccountWrites = 0;
        AccountDeleted = 0;
        StorageWrites = 0;
        CodeWrites = 0;
        CodeBytesWritten = 0;
    }
}
