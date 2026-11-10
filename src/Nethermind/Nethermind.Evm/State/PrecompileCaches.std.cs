// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Threading;
using PrecompileMetrics = Nethermind.Evm.Precompiles.Metrics;

namespace Nethermind.Evm.State;

public sealed partial class PrecompileCaches
{
    private const string ProbeBlockHit = "block_hit";
    private const string ProbeSurvivingHit = "surviving_hit";
    private const string ProbeMiss = "miss";

    partial void PublishMetrics()
    {
        if (!ExecutionMetricsFlag.IsActive) return;

        PrecompileMetrics.PrecompileCacheSurvivingEntries = _survivingCache.Count;
        foreach (KeyValuePair<AddressAsKey, Partition> partition in _partitions)
            partition.Value.PublishMetrics();
    }

    public sealed partial class Partition
    {
        private string _metricLabel = null!;

        // Counted in fields and published on block clear, so the read path takes no dictionary lookup.
        // Probes run on every cache lookup, so they are counted only with detailed metrics, and striped because every
        // prewarm worker probes the same partition and a shared word would serialize them.
        private readonly StripedLong _blockHits = new();
        private readonly StripedLong _survivingHits = new();
        private readonly StripedLong _misses = new();

        // Admission-side counters run only after a precompile executed, so padding is enough.
        // _admitted mirrors the entry count, which ConcurrentDictionary.Count would read by taking every lock.
        private CacheLinePaddedLong _rejectedFull;
        private CacheLinePaddedLong _admitted;

        partial void InitMetrics(string name)
        {
            _metricLabel = name;
            if (ExecutionMetricsFlag.IsActive) PrecompileMetrics.PrecompileCachePartitionMaxBytes[name] = MaxBytes;
        }

        partial void CountBlockHit() => CountProbe(_blockHits);
        partial void CountSurvivingHit() => CountProbe(_survivingHits);
        partial void CountMiss() => CountProbe(_misses);
        partial void CountRejectedFull() => Increment(ref _rejectedFull);
        partial void CountAdmitted() => Increment(ref _admitted);
        partial void ResetAdmitted() => Volatile.Write(ref _admitted.Value, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CountProbe(StripedLong counter)
        {
            if (ExecutionMetricsFlag.IsActive && PrecompileMetrics.DetailedMetricsEnabled) counter.Increment();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Increment(ref CacheLinePaddedLong counter)
        {
            if (ExecutionMetricsFlag.IsActive) Interlocked.Increment(ref counter.Value);
        }

        internal void PublishMetrics()
        {
            if (PrecompileMetrics.DetailedMetricsEnabled)
            {
                PrecompileMetrics.PrecompileCacheProbes[(_metricLabel, ProbeBlockHit)] = _blockHits.Sum;
                PrecompileMetrics.PrecompileCacheProbes[(_metricLabel, ProbeSurvivingHit)] = _survivingHits.Sum;
                PrecompileMetrics.PrecompileCacheProbes[(_metricLabel, ProbeMiss)] = _misses.Sum;
            }

            PrecompileMetrics.PrecompileCacheRejectedFull[_metricLabel] = Volatile.Read(ref _rejectedFull.Value);
            PrecompileMetrics.PrecompileCacheUsedBytes[_metricLabel] = UsedBytes;
            PrecompileMetrics.PrecompileCacheEntries[_metricLabel] = Volatile.Read(ref _admitted.Value);
        }
    }
}
