// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Metric;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// Publishes the metrics of a native manager to <see cref="Metrics"/>: the counters by their change since the last
/// publish, so several managers add up as C# ones do, and the histogram observations replayed into their observers.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Metrics.CompactorStallTime"/> is left out of the replay: <see cref="Fork.RustFlatDbManager.AddSnapshot"/>
/// observes the stall it is handed back, on the block processing thread, as C# does.
/// </para>
/// <para>
/// The persisted-snapshot tier's <c>{tier, size}</c> gauges are published by their change per label since the last
/// publish, a label the native tier no longer reports going back to where it started; its catalog writes reach the
/// <c>TotalWrites</c> of the catalog database through <see cref="RustCatalogStore.PublishWrites"/>.
/// </para>
/// <para>
/// The native contents count in the pooled-resource gauges of the C# contents they replace: a block's content from
/// its creation until the last snapshot holding it is released, as a C# one from its rent to its return, and a
/// compacted content likewise. No native content is pooled, so the cached ones stay 0 and every content is created.
/// </para>
/// </remarks>
/// <param name="catalog">The catalog of the manager's persisted-snapshot tier; null without the tier.</param>
internal sealed unsafe class RustMetricsBridge(RustCatalogStore? catalog)
{
    private const int ObservationBatch = 256;
    private const int TierGaugeBatch = 64;

    private const ushort FlatPersistenceTime = 0;
    private const ushort FlatPersistenceSnapshotSize = 1;
    private const ushort CompactTime = 2;
    private const ushort SnapshotBundleBlockNumberDepth = 4;
    private const ushort InMemorySlotFilterBuildTime = 5;
    private const ushort PersistedSnapshotConvertTime = 6;
    private const ushort PersistedSnapshotSize = 7;
    private const ushort PersistedSnapshotCompactedSize = 8;
    private const ushort PersistedSnapshotCompactTime = 9;
    private const ushort ReadOnlySnapshotBundleSkipTime = 10;

    /// <summary>The label id of <c>CompactSizeLabel(1)</c>; <c>CompactSizeLabel(2^n)</c> is this plus <c>n</c>.</summary>
    private const ushort CompactSizeLabelBase = 16;

    private static readonly StringLabel?[] Labels =
    [
        null, new("state_nodes"), new("storage_nodes"), new("in_memory"), new("persisted"),
        new("account"), new("slot"), new("state_rlp"), new("storage_rlp"),
    ];

    private static readonly ResourcePool.PooledResourceLabel[] ContentLabels =
        [.. Enumerable.Range(0, FdbCounters.ContentUsages).Select(static usage => new ResourcePool.PooledResourceLabel(((ResourcePool.Usage)usage).ToString(), "SnapshotContent"))];

    private static readonly ResourcePool.PooledResourceLabel[] SortedContentLabels =
        [.. Enumerable.Range(0, FdbCounters.SortedContentUsages).Select(static compact => new ResourcePool.PooledResourceLabel((ResourcePool.Usage.Compact2 + compact).ToString(), "SortedSnapshotContent"))];

    private readonly Lock _lock = new();
    private FdbCounters _published;
    private Dictionary<PersistedSnapshotLabel, FfiTierGaugeRow> _publishedTierGauges = [];

    /// <summary>Publishes what changed in the metrics of <paramref name="manager"/>, which the caller keeps alive, since the last call.</summary>
    public void Publish(nint manager)
    {
        lock (_lock)
        {
            PublishCounters(NativeMethods.fdb_stats(manager));
            if (catalog is not null) PublishTierLabels(manager);
            ReplayObservations(manager);
        }
    }

    private void PublishCounters(FdbCounters* counters)
    {
        FdbCounters current = default;
        current.SnapshotCount = Volatile.Read(ref counters->SnapshotCount);
        current.CompactedSnapshotCount = Volatile.Read(ref counters->CompactedSnapshotCount);
        current.SnapshotMemory = Volatile.Read(ref counters->SnapshotMemory);
        current.CompactedSnapshotMemory = Volatile.Read(ref counters->CompactedSnapshotMemory);
        current.TotalSnapshotMemory = Volatile.Read(ref counters->TotalSnapshotMemory);
        current.SnapshotBundleSize = Volatile.Read(ref counters->SnapshotBundleSize);
        current.SnapshotBundlePersistedSnapshotSize = Volatile.Read(ref counters->SnapshotBundlePersistedSnapshotSize);
        current.SnapshotBundlePersistedSnapshotMemory = Volatile.Read(ref counters->SnapshotBundlePersistedSnapshotMemory);
        current.InMemorySlotFilterMemory = Volatile.Read(ref counters->InMemorySlotFilterMemory);
        current.InMemorySlotFilterBuilds = Volatile.Read(ref counters->InMemorySlotFilterBuilds);
        current.InMemorySlotFilterBuildFailures = Volatile.Read(ref counters->InMemorySlotFilterBuildFailures);
        for (int usage = 0; usage < FdbCounters.ContentUsages; usage++)
        {
            current.ContentActive[usage] = Volatile.Read(ref counters->ContentActive[usage]);
            current.ContentCreated[usage] = Volatile.Read(ref counters->ContentCreated[usage]);
            PublishPooled(ContentLabels[usage], current.ContentActive[usage] - _published.ContentActive[usage], current.ContentCreated[usage] - _published.ContentCreated[usage]);
        }

        for (int compact = 0; compact < FdbCounters.SortedContentUsages; compact++)
        {
            current.SortedContentActive[compact] = Volatile.Read(ref counters->SortedContentActive[compact]);
            current.SortedContentCreated[compact] = Volatile.Read(ref counters->SortedContentCreated[compact]);
            PublishPooled(SortedContentLabels[compact], current.SortedContentActive[compact] - _published.SortedContentActive[compact], current.SortedContentCreated[compact] - _published.SortedContentCreated[compact]);
        }

        Metrics.SnapshotCount += current.SnapshotCount - _published.SnapshotCount;
        Metrics.CompactedSnapshotCount += current.CompactedSnapshotCount - _published.CompactedSnapshotCount;
        Metrics.SnapshotMemory += current.SnapshotMemory - _published.SnapshotMemory;
        Metrics.CompactedSnapshotMemory += current.CompactedSnapshotMemory - _published.CompactedSnapshotMemory;
        Metrics.TotalSnapshotMemory += current.TotalSnapshotMemory - _published.TotalSnapshotMemory;
        Metrics.InMemorySlotFilterMemory += current.InMemorySlotFilterMemory - _published.InMemorySlotFilterMemory;
        Metrics.InMemorySlotFilterBuilds += current.InMemorySlotFilterBuilds - _published.InMemorySlotFilterBuilds;
        Metrics.InMemorySlotFilterBuildFailures += current.InMemorySlotFilterBuildFailures - _published.InMemorySlotFilterBuildFailures;

        current.PersistedSnapshotBloomMemory = Volatile.Read(ref counters->PersistedSnapshotBloomMemory);
        current.PersistedSnapshotBloomCount = Volatile.Read(ref counters->PersistedSnapshotBloomCount);
        current.ArenaFileCount = Volatile.Read(ref counters->ArenaFileCount);
        current.ArenaAllocatedBytes = Volatile.Read(ref counters->ArenaAllocatedBytes);
        current.BlobFileCount = Volatile.Read(ref counters->BlobFileCount);
        current.BlobAllocatedBytes = Volatile.Read(ref counters->BlobAllocatedBytes);
        current.ArenaReservationCount = Volatile.Read(ref counters->ArenaReservationCount);
        current.ArenaReservationBytes = Volatile.Read(ref counters->ArenaReservationBytes);
        current.PersistedSnapshotPrunes = Volatile.Read(ref counters->PersistedSnapshotPrunes);
        current.PageTrackerEvictionsDispatched = Volatile.Read(ref counters->PageTrackerEvictionsDispatched);
        current.PageTrackerEvictionsInlineFallback = Volatile.Read(ref counters->PageTrackerEvictionsInlineFallback);
        current.PageTrackerPagesRefreshed = Volatile.Read(ref counters->PageTrackerPagesRefreshed);
        current.PersistedSnapshotCompactions = Volatile.Read(ref counters->PersistedSnapshotCompactions);
        current.CatalogPuts = Volatile.Read(ref counters->CatalogPuts);
        current.PageTrackerResidentBytes = Volatile.Read(ref counters->PageTrackerResidentBytes);
        current.PageTrackerMetadataBytes = Volatile.Read(ref counters->PageTrackerMetadataBytes);

        PublishTierTotals(current, _published);
        Interlocked.Add(ref Metrics._persistedSnapshotPrunes, current.PersistedSnapshotPrunes - _published.PersistedSnapshotPrunes);
        Interlocked.Add(ref Metrics._pageTrackerEvictionsDispatched, current.PageTrackerEvictionsDispatched - _published.PageTrackerEvictionsDispatched);
        Interlocked.Add(ref Metrics._pageTrackerEvictionsInlineFallback, current.PageTrackerEvictionsInlineFallback - _published.PageTrackerEvictionsInlineFallback);
        Interlocked.Add(ref Metrics._pageTrackerPagesRefreshed, current.PageTrackerPagesRefreshed - _published.PageTrackerPagesRefreshed);
        // A plain property in C#, so only adjusted under this bridge's lock.
        Metrics.PersistedSnapshotCompactions += current.PersistedSnapshotCompactions - _published.PersistedSnapshotCompactions;
        catalog?.PublishWrites(current.CatalogPuts - _published.CatalogPuts);

        // The page tracker gauges and the bundle gauges are set by C# rather than adjusted.
        if (current.PageTrackerResidentBytes != _published.PageTrackerResidentBytes) Metrics.PageTrackerResidentBytes = current.PageTrackerResidentBytes;
        if (current.PageTrackerMetadataBytes != _published.PageTrackerMetadataBytes) Metrics.PageTrackerMetadataBytes = current.PageTrackerMetadataBytes;

        if (current.SnapshotBundleSize != _published.SnapshotBundleSize) Metrics.SnapshotBundleSize = current.SnapshotBundleSize;
        if (current.SnapshotBundlePersistedSnapshotSize != _published.SnapshotBundlePersistedSnapshotSize)
            Metrics.SnapshotBundlePersistedSnapshotSize = current.SnapshotBundlePersistedSnapshotSize;
        if (current.SnapshotBundlePersistedSnapshotMemory != _published.SnapshotBundlePersistedSnapshotMemory)
            Metrics.SnapshotBundlePersistedSnapshotMemory = current.SnapshotBundlePersistedSnapshotMemory;

        _published = current;
    }

    /// <summary>
    /// Takes back every persisted-snapshot tier gauge published, as the tier of the destroyed manager released what
    /// they count, and C# takes them back as it disposes its tier.
    /// </summary>
    public void PublishTierReleased()
    {
        if (catalog is null) return;

        lock (_lock)
        {
            FdbCounters released = _published;
            released.PersistedSnapshotBloomMemory = 0;
            released.PersistedSnapshotBloomCount = 0;
            released.ArenaFileCount = 0;
            released.ArenaAllocatedBytes = 0;
            released.BlobFileCount = 0;
            released.BlobAllocatedBytes = 0;
            released.ArenaReservationCount = 0;
            released.ArenaReservationBytes = 0;
            released.PageTrackerResidentBytes = 0;
            released.PageTrackerMetadataBytes = 0;
            PublishTierTotals(released, _published);
            PublishTierLabels([]);
            Metrics.PageTrackerResidentBytes = 0;
            Metrics.PageTrackerMetadataBytes = 0;
            _published = released;
        }
    }

    /// <summary>Adjusts the tier's bloom, file and reservation gauges by their change from <paramref name="published"/> to <paramref name="current"/>.</summary>
    private static void PublishTierTotals(in FdbCounters current, in FdbCounters published)
    {
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, current.PersistedSnapshotBloomMemory - published.PersistedSnapshotBloomMemory);
        Interlocked.Add(ref Metrics._persistedSnapshotBloomCount, current.PersistedSnapshotBloomCount - published.PersistedSnapshotBloomCount);
        Interlocked.Add(ref Metrics._arenaFileCount, current.ArenaFileCount - published.ArenaFileCount);
        Interlocked.Add(ref Metrics._arenaAllocatedBytes, current.ArenaAllocatedBytes - published.ArenaAllocatedBytes);
        Interlocked.Add(ref Metrics._blobFileCount, current.BlobFileCount - published.BlobFileCount);
        Interlocked.Add(ref Metrics._blobAllocatedBytes, current.BlobAllocatedBytes - published.BlobAllocatedBytes);
        Interlocked.Add(ref Metrics._arenaReservationCount, current.ArenaReservationCount - published.ArenaReservationCount);
        Interlocked.Add(ref Metrics._arenaReservationBytes, current.ArenaReservationBytes - published.ArenaReservationBytes);
    }

    private void PublishTierLabels(nint manager)
    {
        FfiTierGaugeRow* stackRows = stackalloc FfiTierGaugeRow[TierGaugeBatch];
        nuint count;
        FdbStatus status = NativeMethods.fdb_persisted_tier_gauges(manager, stackRows, TierGaugeBatch, &count);
        FfiTierGaugeRow[]? heapRows = null;
        // Labels can appear between the calls, so a retry may need more room again.
        while (status == FdbStatus.BufferTooSmall)
        {
            heapRows = new FfiTierGaugeRow[count];
            fixed (FfiTierGaugeRow* heap = heapRows)
            {
                status = NativeMethods.fdb_persisted_tier_gauges(manager, heap, (nuint)heapRows.Length, &count);
            }
        }

        StatusMap.ThrowIfFailed(status);
        PublishTierLabels(heapRows is null ? new(stackRows, (int)count) : heapRows.AsSpan(0, (int)count));
    }

    /// <summary>Adjusts the <c>{tier, size}</c> gauges by the change of each label since the last publish.</summary>
    private void PublishTierLabels(ReadOnlySpan<FfiTierGaugeRow> rows)
    {
        Dictionary<PersistedSnapshotLabel, FfiTierGaugeRow> current = new(rows.Length);
        foreach (FfiTierGaugeRow row in rows)
        {
            PersistedSnapshotLabel label = new(((SnapshotTier)row.Tier).MetricTierLabel(), (long)row.Size);
            current[label] = row;
            _publishedTierGauges.Remove(label, out FfiTierGaugeRow published);
            PublishTierGauge(label, row.Count - published.Count, row.Memory - published.Memory, row.Active - published.Active);
        }

        foreach ((PersistedSnapshotLabel label, FfiTierGaugeRow gone) in _publishedTierGauges)
        {
            PublishTierGauge(label, -gone.Count, -gone.Memory, -gone.Active);
        }

        _publishedTierGauges = current;
    }

    private static void PublishTierGauge(PersistedSnapshotLabel label, long count, long memory, long active)
    {
        if (count != 0) Metrics.PersistedSnapshotCount.AddBy(label, count);
        if (memory != 0) Metrics.PersistedSnapshotMemory.AddBy(label, memory);
        if (active != 0) Metrics.ActivePersistedSnapshotCount.AddBy(label, active);
    }

    private static void PublishPooled(ResourcePool.PooledResourceLabel label, long active, long created)
    {
        if (active == 0 && created == 0) return;

        Metrics.ActivePooledResource.AddBy(label, active);
        Metrics.CreatedPooledResource.AddBy(label, created);
        Metrics.CachedPooledResource.TryAdd(label, 0);
    }

    private static void ReplayObservations(nint manager)
    {
        FdbObservation* observations = stackalloc FdbObservation[ObservationBatch];
        nuint count;
        do
        {
            StatusMap.ThrowIfFailed(NativeMethods.fdb_drain_observations(manager, observations, ObservationBatch, &count));
            for (nuint i = 0; i < count; i++)
            {
                FdbObservation observation = observations[i];
                switch (observation.Metric)
                {
                    case FlatPersistenceTime:
                        Metrics.FlatPersistenceTime.Observe(ToTicks(observation.Value));
                        break;
                    case FlatPersistenceSnapshotSize:
                        Metrics.FlatPersistenceSnapshotSize.Observe(observation.Value, LabelOf(observation));
                        break;
                    case CompactTime:
                        Metrics.CompactTime.Observe(ToTicks(observation.Value));
                        break;
                    case SnapshotBundleBlockNumberDepth:
                        Metrics.SnapshotBundleBlockNumberDepth.Observe(observation.Value, LabelOf(observation));
                        break;
                    case InMemorySlotFilterBuildTime:
                        Metrics.InMemorySlotFilterBuildTime.Observe(ToTicks(observation.Value));
                        break;
                    case PersistedSnapshotConvertTime:
                        Metrics.PersistedSnapshotConvertTime.Observe(ToTicks(observation.Value));
                        break;
                    case PersistedSnapshotSize:
                        Metrics.PersistedSnapshotSize.Observe(observation.Value);
                        break;
                    case PersistedSnapshotCompactedSize:
                        Metrics.PersistedSnapshotCompactedSize.Observe(observation.Value, LabelOf(observation));
                        break;
                    case PersistedSnapshotCompactTime:
                        Metrics.PersistedSnapshotCompactTime.Observe(ToTicks(observation.Value), LabelOf(observation));
                        break;
                    case ReadOnlySnapshotBundleSkipTime:
                        Metrics.ReadOnlySnapshotBundleSkipTime.Observe(ToTicks(observation.Value), LabelOf(observation));
                        break;
                }
            }
        } while (count == ObservationBatch);
    }

    private static IMetricLabels? LabelOf(in FdbObservation observation) =>
        observation.Label >= CompactSizeLabelBase ? new CompactSizeLabel(1 << (observation.Label - CompactSizeLabelBase))
        : observation.Label < Labels.Length ? Labels[observation.Label]
        : null;

    /// <summary>Native times are nanoseconds; C# observes <see cref="Stopwatch"/> ticks.</summary>
    public static double ToTicks(double nanoseconds) => nanoseconds * Stopwatch.Frequency / 1_000_000_000d;
}
