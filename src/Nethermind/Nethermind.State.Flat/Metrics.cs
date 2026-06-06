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
    [Description("Average snapshot bundle size in terms of num of snapshot")]
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
    [Description("Blob-arena trie-RLP bytes WILLNEED-prefetched per persisted-snapshot persistence")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver FlatPersistenceBlobWarmedSize { get; set; } = new NoopMetricObserver();

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
    // The four gauges/counters below are mutated delta-wise by each PersistedSnapshotRepository
    // at every add/remove site (via Interlocked.Add(ref Metrics._xxx, ...)), so callers must not
    // recompute or overwrite them — they stay correct only as long as every mutation goes through
    // the repo. Backed by fields with Volatile.Read/Write accessors to match the bloom pattern.

    internal static long _persistedSnapshotCount;

    [GaugeMetric]
    [Description("Number of persisted snapshots on disk")]
    public static long PersistedSnapshotCount
    {
        get => Volatile.Read(ref _persistedSnapshotCount);
        set => Volatile.Write(ref _persistedSnapshotCount, value);
    }

    [GaugeMetric]
    [Description("Estimated disk usage of persisted snapshots in bytes")]
    public static long PersistedSnapshotDiskBytes { get; set; }

    internal static long _persistedSnapshotMemory;

    [GaugeMetric]
    [Description("Estimated memory used by base persisted snapshots in bytes")]
    public static long PersistedSnapshotMemory
    {
        get => Volatile.Read(ref _persistedSnapshotMemory);
        set => Volatile.Write(ref _persistedSnapshotMemory, value);
    }

    internal static long _compactedPersistedSnapshotMemory;

    [GaugeMetric]
    [Description("Estimated memory used by compacted persisted snapshots in bytes")]
    public static long CompactedPersistedSnapshotMemory
    {
        get => Volatile.Read(ref _compactedPersistedSnapshotMemory);
        set => Volatile.Write(ref _compactedPersistedSnapshotMemory, value);
    }

    // Backed by a field so callers can update via Interlocked.Add(ref ...).
    internal static long _persistedSnapshotBloomMemory;

    [GaugeMetric]
    [Description("Memory used by per-snapshot blooms (address/slot/self-destruct/trie) in bytes")]
    public static long PersistedSnapshotBloomMemory
    {
        get => Volatile.Read(ref _persistedSnapshotBloomMemory);
        set => Volatile.Write(ref _persistedSnapshotBloomMemory, value);
    }

    [DetailedMetric]
    [CounterMetric]
    [Description("Number of persisted snapshot compactions performed")]
    public static long PersistedSnapshotCompactions { get; set; }

    [DetailedMetric]
    [CounterMetric]
    [Description("Number of persisted snapshot file writes")]
    public static long PersistedSnapshotWrites { get; set; }

    internal static long _persistedSnapshotPrunes;

    [DetailedMetric]
    [CounterMetric]
    [Description("Number of persisted snapshot prunes")]
    public static long PersistedSnapshotPrunes
    {
        get => Volatile.Read(ref _persistedSnapshotPrunes);
        set => Volatile.Write(ref _persistedSnapshotPrunes, value);
    }

    // Push-style gauges keyed by the typed PersistedSnapshotTier singleton so the small and
    // large pools surface separately in Prometheus; the metrics controller dispatches on
    // IMetricLabels to produce the wire-format "small"/"large" label.
    //
    // Two separate gauge families: arena files (mmap-backed metadata) versus blob files
    // (pread-only RLP). They had been mixed under a single Arena*ByTier pair, which made it
    // impossible to attribute per-tier bytes to one or the other from the dashboard.
    //
    // Bytes are reported as **allocated** (sum of `Frontier` across open files) — i.e. bytes
    // actually written, not the pre-extended sparse mmap region. Arena/Blob managers push
    // deltas on every writer.Complete + on file open/close.
    [Description("Number of arena (mmap metadata) files backing persisted snapshots, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> ArenaFileCountByTier { get; } = new();

    [Description("Allocated bytes in arena files (sum of per-file Frontier), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> ArenaAllocatedBytesByTier { get; } = new();

    [Description("Number of blob (pread RLP) files backing persisted snapshots, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> BlobFileCountByTier { get; } = new();

    [Description("Allocated bytes in blob files (sum of per-file Frontier), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> BlobAllocatedBytesByTier { get; } = new();

    [Description("Number of live PersistedSnapshot instances (refcount > 0), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> ActivePersistedSnapshotCountByTier { get; } = new();

    [Description("1 if fallocate(PUNCH_HOLE) disk reclamation is active for the tier, 0 if disabled (config off or filesystem unsupported)")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> PersistedSnapshotPunchHoleEnabledByTier { get; } = new();

    // Per-tier PageResidencyTracker gauges. ResidentBytes is refreshed by ArenaManager on a
    // 1-second System.Threading.Timer so the tracker's hot path stays untouched; the gauge
    // lags reality by at most ~1s. MetadataBytes and MaxBytes are fixed at tracker construction.
    [Description("Currently-bounded resident bytes in the page-residency tracker, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> PageTrackerResidentBytesByTier { get; } = new();

    [Description("Unmanaged metadata bytes used by the page-residency tracker (slot + meta arrays), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> PageTrackerMetadataBytesByTier { get; } = new();

    [Description("Maximum bytes the page-residency tracker can bound (configured page-cache budget), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> PageTrackerMaxBytesByTier { get; } = new();

    [DetailedMetric]
    [CounterMetric]
    [Description("Page-tracker evictions dispatched off the drain ring (madvise issued), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> PageTrackerEvictionsDispatchedByTier { get; } = new();

    [DetailedMetric]
    [CounterMetric]
    [Description("Page-tracker evictions dispatched inline because the drain ring was full, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> PageTrackerEvictionsInlineFallbackByTier { get; } = new();

    // Blob-arena PageResidencyTracker gauges. Distinct from the PageTracker*ByTier family above,
    // which the metadata ArenaManager owns: both managers register the same PersistedSnapshotTier,
    // so the blob tracker needs its own keys to avoid clobbering the metadata gauges.
    [Description("Currently-bounded resident bytes in the blob-arena page-residency tracker, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> BlobPageTrackerResidentBytesByTier { get; } = new();

    [Description("Unmanaged metadata bytes used by the blob-arena page-residency tracker (slot + meta arrays), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> BlobPageTrackerMetadataBytesByTier { get; } = new();

    [Description("Maximum bytes the blob-arena page-residency tracker can bound (configured page-cache budget), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> BlobPageTrackerMaxBytesByTier { get; } = new();

    [DetailedMetric]
    [CounterMetric]
    [Description("Blob-arena page-tracker evictions dispatched off the drain ring (madvise issued), by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> BlobPageTrackerEvictionsDispatchedByTier { get; } = new();

    [DetailedMetric]
    [CounterMetric]
    [Description("Blob-arena page-tracker evictions dispatched inline because the drain ring was full, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> BlobPageTrackerEvictionsInlineFallbackByTier { get; } = new();

    [DetailedMetric]
    [Description("Live arena reservations, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> ArenaReservationCountByTier { get; } = new();

    [DetailedMetric]
    [Description("Live arena reservation bytes, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> ArenaReservationBytesByTier { get; } = new();

    [DetailedMetric]
    [Description("Snapshot-bundle depth in blocks, by part (in_memory / persisted)")]
    [ExponentialPowerHistogramMetric(LabelNames = ["part"], Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver SnapshotBundleBlockNumberDepth { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time spent skipping accounts/slots/state-rlp/storage-rlp on a read-only snapshot bundle access, by part")]
    [ExponentialPowerHistogramMetric(LabelNames = ["part"], Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver ReadOnlySnapshotBundleSkipTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time to convert one in-memory snapshot into a persisted snapshot, by part")]
    [ExponentialPowerHistogramMetric(LabelNames = ["part"], Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PersistedSnapshotConvertTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Persisted-snapshot byte size, by tier")]
    [ExponentialPowerHistogramMetric(LabelNames = ["tier"], Start = 1, Factor = 1.5, Count = 30)]
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
