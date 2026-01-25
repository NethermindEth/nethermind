// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DbMetrics = Nethermind.Db.Metrics;
using EvmMetrics = Nethermind.Evm.Metrics;

namespace Nethermind.Core.Test;

/// <summary>
/// Captures a snapshot of thread-local metrics for delta calculation during tests.
/// Used to verify that specific operations increment the expected metrics.
/// </summary>
public class MetricsSnapshot
{
    // State access metrics
    public long AccountReads { get; init; }
    public long AccountWrites { get; init; }
    public long AccountDeleted { get; init; }
    public long StorageReads { get; init; }
    public long StorageWrites { get; init; }
    public long StorageDeleted { get; init; }
    public long CodeReads { get; init; }
    public long CodeWrites { get; init; }
    public long CodeBytesRead { get; init; }
    public long CodeBytesWritten { get; init; }

    // EIP-7702 delegation tracking
    public long Eip7702DelegationsSet { get; init; }
    public long Eip7702DelegationsCleared { get; init; }

    // Timing metrics (in ticks)
    public long StateReadTime { get; init; }
    public long StateHashTime { get; init; }
    public long CommitTime { get; init; }

    // Cache statistics
    public long AccountCacheHits { get; init; }
    public long AccountCacheMisses { get; init; }
    public long StorageCacheHits { get; init; }
    public long StorageCacheMisses { get; init; }
    public long CodeCacheHits { get; init; }
    public long CodeCacheMisses { get; init; }

    // EVM operation counts
    public long SloadOps { get; init; }
    public long SstoreOps { get; init; }
    public long CallOps { get; init; }
    public long CreateOps { get; init; }
    public long OpCodes { get; init; }

    /// <summary>
    /// Captures the current state of all thread-local metrics.
    /// </summary>
    public static MetricsSnapshot Capture() => new()
    {
        // State access metrics
        AccountReads = EvmMetrics.ThreadLocalAccountReads,
        AccountWrites = EvmMetrics.ThreadLocalAccountWrites,
        AccountDeleted = EvmMetrics.ThreadLocalAccountDeleted,
        StorageReads = EvmMetrics.ThreadLocalStorageReads,
        StorageWrites = EvmMetrics.ThreadLocalStorageWrites,
        StorageDeleted = EvmMetrics.ThreadLocalStorageDeleted,
        CodeReads = EvmMetrics.ThreadLocalCodeReads,
        CodeWrites = EvmMetrics.ThreadLocalCodeWrites,
        CodeBytesRead = EvmMetrics.ThreadLocalCodeBytesRead,
        CodeBytesWritten = EvmMetrics.ThreadLocalCodeBytesWritten,

        // EIP-7702 delegation tracking
        Eip7702DelegationsSet = EvmMetrics.ThreadLocalEip7702DelegationsSet,
        Eip7702DelegationsCleared = EvmMetrics.ThreadLocalEip7702DelegationsCleared,

        // Timing metrics
        StateReadTime = EvmMetrics.ThreadLocalStateReadTime,
        StateHashTime = EvmMetrics.ThreadLocalStateHashTime,
        CommitTime = EvmMetrics.ThreadLocalCommitTime,

        // Cache statistics (using Db.Metrics for state tree caches)
        AccountCacheHits = DbMetrics.ThreadLocalStateTreeCacheHits,
        AccountCacheMisses = DbMetrics.ThreadLocalStateTreeReads,
        StorageCacheHits = DbMetrics.ThreadLocalStorageTreeCacheHits,
        StorageCacheMisses = DbMetrics.ThreadLocalStorageTreeReads,
        CodeCacheHits = EvmMetrics.ThreadLocalCodeDbCache,
        CodeCacheMisses = EvmMetrics.ThreadLocalCodeReads,

        // EVM operation counts
        SloadOps = EvmMetrics.ThreadLocalSLoadOpcode,
        SstoreOps = EvmMetrics.ThreadLocalSStoreOpcode,
        CallOps = EvmMetrics.ThreadLocalCalls,
        CreateOps = EvmMetrics.ThreadLocalCreates,
        OpCodes = EvmMetrics.ThreadLocalOpCodes,
    };

    /// <summary>
    /// Calculates the difference between this snapshot (before) and another snapshot (after).
    /// </summary>
    public MetricsDelta DeltaTo(MetricsSnapshot after) => new()
    {
        // State access metrics
        AccountReads = after.AccountReads - AccountReads,
        AccountWrites = after.AccountWrites - AccountWrites,
        AccountDeleted = after.AccountDeleted - AccountDeleted,
        StorageReads = after.StorageReads - StorageReads,
        StorageWrites = after.StorageWrites - StorageWrites,
        StorageDeleted = after.StorageDeleted - StorageDeleted,
        CodeReads = after.CodeReads - CodeReads,
        CodeWrites = after.CodeWrites - CodeWrites,
        CodeBytesRead = after.CodeBytesRead - CodeBytesRead,
        CodeBytesWritten = after.CodeBytesWritten - CodeBytesWritten,

        // EIP-7702 delegation tracking
        Eip7702DelegationsSet = after.Eip7702DelegationsSet - Eip7702DelegationsSet,
        Eip7702DelegationsCleared = after.Eip7702DelegationsCleared - Eip7702DelegationsCleared,

        // Timing metrics
        StateReadTime = after.StateReadTime - StateReadTime,
        StateHashTime = after.StateHashTime - StateHashTime,
        CommitTime = after.CommitTime - CommitTime,

        // Cache statistics
        AccountCacheHits = after.AccountCacheHits - AccountCacheHits,
        AccountCacheMisses = after.AccountCacheMisses - AccountCacheMisses,
        StorageCacheHits = after.StorageCacheHits - StorageCacheHits,
        StorageCacheMisses = after.StorageCacheMisses - StorageCacheMisses,
        CodeCacheHits = after.CodeCacheHits - CodeCacheHits,
        CodeCacheMisses = after.CodeCacheMisses - CodeCacheMisses,

        // EVM operation counts
        SloadOps = after.SloadOps - SloadOps,
        SstoreOps = after.SstoreOps - SstoreOps,
        CallOps = after.CallOps - CallOps,
        CreateOps = after.CreateOps - CreateOps,
        OpCodes = after.OpCodes - OpCodes,
    };
}

/// <summary>
/// Represents the delta between two MetricsSnapshots.
/// All values are the difference: after - before.
/// </summary>
public class MetricsDelta
{
    // State access metrics
    public long AccountReads { get; init; }
    public long AccountWrites { get; init; }
    public long AccountDeleted { get; init; }
    public long StorageReads { get; init; }
    public long StorageWrites { get; init; }
    public long StorageDeleted { get; init; }
    public long CodeReads { get; init; }
    public long CodeWrites { get; init; }
    public long CodeBytesRead { get; init; }
    public long CodeBytesWritten { get; init; }

    // EIP-7702 delegation tracking
    public long Eip7702DelegationsSet { get; init; }
    public long Eip7702DelegationsCleared { get; init; }

    // Timing metrics (in ticks)
    public long StateReadTime { get; init; }
    public long StateHashTime { get; init; }
    public long CommitTime { get; init; }

    // Cache statistics
    public long AccountCacheHits { get; init; }
    public long AccountCacheMisses { get; init; }
    public long StorageCacheHits { get; init; }
    public long StorageCacheMisses { get; init; }
    public long CodeCacheHits { get; init; }
    public long CodeCacheMisses { get; init; }

    // EVM operation counts
    public long SloadOps { get; init; }
    public long SstoreOps { get; init; }
    public long CallOps { get; init; }
    public long CreateOps { get; init; }
    public long OpCodes { get; init; }
}
