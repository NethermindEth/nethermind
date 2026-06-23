// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using NonBlocking;


namespace Nethermind.State.Flat;

public static class Metrics
{
    [GaugeMetric]
    [Description("Average snapshot bundle size in terms of num of snapshot")]
    public static long SnapshotBundleSize { get; set; }

    [GaugeMetric]
    [Description("Number of persisted snapshots in the most recently assembled snapshot bundle")]
    public static long SnapshotBundlePersistedSnapshotSize { get; set; }

    [GaugeMetric]
    [Description("Total persisted-snapshot reservation bytes in the most recently assembled read-only snapshot bundle (the bytes a tip reader pays for)")]
    public static long SnapshotBundlePersistedSnapshotMemory { get; set; }

    [DetailedMetric]
    [Description("Time for persistence job")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver FlatPersistenceTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Persistence write size")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["payload"])]
    public static IMetricObserver FlatPersistenceSnapshotSize { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [CounterMetric]
    [Description("Importer entries count")]
    public static long ImporterEntriesCount { get; set; }

    [DetailedMetric]
    [CounterMetric]
    [Description("Importer entries count flat")]
    public static long ImporterEntriesCountFlat { get; set; }

    [GaugeMetric]
    [Description("Active snapshot bundles")]
    public static long ActiveSnapshotBundle { get; set; }

    [GaugeMetric]
    [Description("Number of snapshots")]
    public static long SnapshotCount { get; set; }

    [GaugeMetric]
    [Description("Number of compacted snapshots")]
    public static long CompactedSnapshotCount { get; set; }

    [GaugeMetric]
    [Description("Estimated memory used by snapshots in bytes")]
    public static long SnapshotMemory { get; set; }

    [GaugeMetric]
    [Description("Estimated memory used by compacted snapshot dictionaries in bytes")]
    public static long CompactedSnapshotMemory { get; set; }

    [GaugeMetric]
    [Description("Total estimated snapshot memory in bytes")]
    public static long TotalSnapshotMemory { get; set; }

    [DetailedMetric]
    [Description("Active pooled resources by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> ActivePooledResource { get; } = new();

    [DetailedMetric]
    [Description("Cached pooled resources by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> CachedPooledResource { get; } = new();

    [DetailedMetric]
    [Description("Created pooled resources by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> CreatedPooledResource { get; } = new();

    [DetailedMetric]
    [Description("Readonly snapshot bundle times")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["type"])]
    public static IMetricObserver ReadOnlySnapshotBundleTimes { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time spend compacting snapshots")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 1, LabelNames = [])]
    public static IMetricObserver CompactTime { get; set; } = new NoopMetricObserver();

    // --- Persisted snapshot metrics ---
    //
    // The tier-labeled gauges below are mutated delta-wise by PersistedSnapshotBucket at every
    // add/remove site (via .AddBy(tier, delta)), so callers must not recompute or overwrite them —
    // they stay correct only as long as every mutation goes through the repo.

    [GaugeMetric]
    [Description("Number of persisted snapshots on disk, by tier")]
    [KeyIsLabel("tier", "size")]
    public static ConcurrentDictionary<PersistedSnapshotLabel, long> PersistedSnapshotCount { get; } = new();

    [GaugeMetric]
    [Description("Estimated memory used by persisted snapshots in bytes, by tier")]
    [KeyIsLabel("tier", "size")]
    public static ConcurrentDictionary<PersistedSnapshotLabel, long> PersistedSnapshotMemory { get; } = new();

    // Backed by a field so callers can update via Interlocked.Add(ref ...).
    internal static long _persistedSnapshotBloomMemory;

    [GaugeMetric]
    [Description("Memory used by per-snapshot blooms (address/slot/self-destruct/trie) in bytes")]
    public static long PersistedSnapshotBloomMemory
    {
        get => Volatile.Read(ref _persistedSnapshotBloomMemory);
        set => Volatile.Write(ref _persistedSnapshotBloomMemory, value);
    }

    // Backed by a field so callers can update via Interlocked.Increment/Decrement(ref ...).
    internal static long _persistedSnapshotBloomCount;

    [DetailedMetric]
    [GaugeMetric]
    [Description("Number of live persisted-snapshot bloom filters (one per RefCountedBloomFilter; a bloom shared across snapshots counts once)")]
    public static long PersistedSnapshotBloomCount
    {
        get => Volatile.Read(ref _persistedSnapshotBloomCount);
        set => Volatile.Write(ref _persistedSnapshotBloomCount, value);
    }

    [DetailedMetric]
    [CounterMetric]
    [Description("Number of persisted snapshot compactions performed")]
    public static long PersistedSnapshotCompactions { get; set; }

    internal static long _persistedSnapshotPrunes;

    [DetailedMetric]
    [CounterMetric]
    [Description("Number of persisted snapshot prunes")]
    public static long PersistedSnapshotPrunes
    {
        get => Volatile.Read(ref _persistedSnapshotPrunes);
        set => Volatile.Write(ref _persistedSnapshotPrunes, value);
    }

    // Push-style gauges for the persisted-snapshot arena/blob storage. Two separate gauge
    // families: arena files (mmap-backed metadata) versus blob files (pread-only RLP), so
    // bytes can be attributed to one or the other from the dashboard.
    //
    // Bytes are reported as **allocated** (sum of `Frontier` across open files) — i.e. bytes
    // actually written, not the pre-extended sparse mmap region. Arena/Blob managers push
    // deltas (via Interlocked on the backing fields) on every writer.Complete + on file
    // open/close.
    internal static long _arenaFileCount;

    [GaugeMetric]
    [Description("Number of arena (mmap metadata) files backing persisted snapshots")]
    public static long ArenaFileCount
    {
        get => Volatile.Read(ref _arenaFileCount);
        set => Volatile.Write(ref _arenaFileCount, value);
    }

    internal static long _arenaAllocatedBytes;

    [GaugeMetric]
    [Description("Allocated bytes in arena files (sum of per-file Frontier)")]
    public static long ArenaAllocatedBytes
    {
        get => Volatile.Read(ref _arenaAllocatedBytes);
        set => Volatile.Write(ref _arenaAllocatedBytes, value);
    }

    internal static long _blobFileCount;

    [GaugeMetric]
    [Description("Number of blob (pread RLP) files backing persisted snapshots")]
    public static long BlobFileCount
    {
        get => Volatile.Read(ref _blobFileCount);
        set => Volatile.Write(ref _blobFileCount, value);
    }

    internal static long _blobAllocatedBytes;

    [GaugeMetric]
    [Description("Allocated bytes in blob files (sum of per-file Frontier)")]
    public static long BlobAllocatedBytes
    {
        get => Volatile.Read(ref _blobAllocatedBytes);
        set => Volatile.Write(ref _blobAllocatedBytes, value);
    }

    [GaugeMetric]
    [Description("Number of live PersistedSnapshot instances (refcount > 0), by tier")]
    [KeyIsLabel("tier", "size")]
    public static ConcurrentDictionary<PersistedSnapshotLabel, long> ActivePersistedSnapshotCount { get; } = new();

    internal static long _arenaReservationCount;

    [DetailedMetric]
    [GaugeMetric]
    [Description("Live arena reservations")]
    public static long ArenaReservationCount
    {
        get => Volatile.Read(ref _arenaReservationCount);
        set => Volatile.Write(ref _arenaReservationCount, value);
    }

    internal static long _arenaReservationBytes;

    [DetailedMetric]
    [GaugeMetric]
    [Description("Live arena reservation bytes")]
    public static long ArenaReservationBytes
    {
        get => Volatile.Read(ref _arenaReservationBytes);
        set => Volatile.Write(ref _arenaReservationBytes, value);
    }

    [DetailedMetric]
    [Description("Snapshot-bundle depth in blocks, by part (in_memory / persisted)")]
    [ExponentialPowerHistogramMetric(LabelNames = ["part"], Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver SnapshotBundleBlockNumberDepth { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time spent skipping accounts/slots/state-rlp/storage-rlp on a read-only snapshot bundle access, by part")]
    [ExponentialPowerHistogramMetric(LabelNames = ["part"], Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver ReadOnlySnapshotBundleSkipTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time to convert one in-memory snapshot into a persisted snapshot")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PersistedSnapshotConvertTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Persisted-snapshot byte size")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PersistedSnapshotSize { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Persisted-snapshot compaction output size, by compact size")]
    [ExponentialPowerHistogramMetric(LabelNames = ["size"], Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PersistedSnapshotCompactedSize { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Persisted-snapshot compaction wall-clock time, by compact size")]
    [ExponentialPowerHistogramMetric(LabelNames = ["size"], Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PersistedSnapshotCompactTime { get; set; } = new NoopMetricObserver();
}
