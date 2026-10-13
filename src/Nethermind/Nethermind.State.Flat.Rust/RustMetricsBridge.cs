// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Attributes;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// Publishes the metrics of a native manager to <see cref="Metrics"/>: the counters by their change since the last
/// publish, so several managers add up as C# ones do, and the histogram observations replayed into their observers.
/// </summary>
/// <remarks>
/// <see cref="Metrics.CompactorStallTime"/> is left out of the replay: <see cref="RustFlatDbManager.AddSnapshot"/>
/// observes the stall it is handed back, on the block processing thread, as C# does.
/// </remarks>
internal sealed unsafe class RustMetricsBridge
{
    private const int ObservationBatch = 256;

    private const ushort FlatPersistenceTime = 0;
    private const ushort FlatPersistenceSnapshotSize = 1;
    private const ushort CompactTime = 2;
    private const ushort SnapshotBundleBlockNumberDepth = 4;
    private const ushort InMemorySlotFilterBuildTime = 5;

    private static readonly StringLabel?[] Labels =
        [null, new("state_nodes"), new("storage_nodes"), new("in_memory"), new("persisted")];

    private readonly Lock _lock = new();
    private FdbCounters _published;

    /// <summary>Publishes what changed in the metrics of <paramref name="manager"/>, which the caller keeps alive, since the last call.</summary>
    public void Publish(nint manager)
    {
        lock (_lock)
        {
            PublishCounters(NativeMethods.fdb_stats(manager));
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

        Metrics.SnapshotCount += current.SnapshotCount - _published.SnapshotCount;
        Metrics.CompactedSnapshotCount += current.CompactedSnapshotCount - _published.CompactedSnapshotCount;
        Metrics.SnapshotMemory += current.SnapshotMemory - _published.SnapshotMemory;
        Metrics.CompactedSnapshotMemory += current.CompactedSnapshotMemory - _published.CompactedSnapshotMemory;
        Metrics.TotalSnapshotMemory += current.TotalSnapshotMemory - _published.TotalSnapshotMemory;
        Metrics.InMemorySlotFilterMemory += current.InMemorySlotFilterMemory - _published.InMemorySlotFilterMemory;
        Metrics.InMemorySlotFilterBuilds += current.InMemorySlotFilterBuilds - _published.InMemorySlotFilterBuilds;
        Metrics.InMemorySlotFilterBuildFailures += current.InMemorySlotFilterBuildFailures - _published.InMemorySlotFilterBuildFailures;

        // The bundle gauges describe the last bundle assembled, which C# sets rather than adjusts.
        if (current.SnapshotBundleSize != _published.SnapshotBundleSize) Metrics.SnapshotBundleSize = current.SnapshotBundleSize;
        if (current.SnapshotBundlePersistedSnapshotSize != _published.SnapshotBundlePersistedSnapshotSize)
            Metrics.SnapshotBundlePersistedSnapshotSize = current.SnapshotBundlePersistedSnapshotSize;
        if (current.SnapshotBundlePersistedSnapshotMemory != _published.SnapshotBundlePersistedSnapshotMemory)
            Metrics.SnapshotBundlePersistedSnapshotMemory = current.SnapshotBundlePersistedSnapshotMemory;

        _published = current;
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
                }
            }
        } while (count == ObservationBatch);
    }

    private static StringLabel? LabelOf(in FdbObservation observation) =>
        observation.Label < Labels.Length ? Labels[observation.Label] : null;

    /// <summary>Native times are nanoseconds; C# observes <see cref="Stopwatch"/> ticks.</summary>
    public static double ToTicks(double nanoseconds) => nanoseconds * Stopwatch.Frequency / 1_000_000_000d;
}
