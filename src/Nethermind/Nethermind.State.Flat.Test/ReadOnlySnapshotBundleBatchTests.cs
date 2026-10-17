// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Metric;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ReadOnlySnapshotBundleBatchTests
{
    [Test]
    public void GetSlots_WithMetricsAndSnapshotHits_PreservesBatchRoutingAndValues(
        [Values] bool detailedMetrics, [Values] bool snapshotHits, [Values(0, 1, 7, 8, 16, 64, 65, 72, 129)] int count)
    {
        ResourcePool pool = new(new FlatDbConfig());
        (Address Address, int SelfDestructIdx, UInt256 Slot)[] requests = new (Address, int, UInt256)[count];
        UInt256?[] expected = new UInt256?[count];
        UInt256?[] actual = new UInt256?[count];
        List<StorageCell> expectedReads = [];
        for (int i = 0; i < count; i++)
        {
            Address address = (i & 1) == 0 ? TestItem.AddressA : TestItem.AddressB;
            UInt256 slot = (UInt256)(ulong)(count - i);
            requests[i] = (address, -1, slot);
            if (snapshotHits && i % 4 == 0)
            {
                UInt256? value = i % 8 == 0 ? null : UInt256.Zero;
                expected[i] = value;
            }
            else
            {
                expectedReads.Add(new StorageCell(address, in slot));
                expected[i] = SlotValue(address, in slot);
            }
        }
        SlotBatchReader reader = new();
        Snapshot snapshot = FlatTestHelpers.MakeSnapshot(pool, c =>
        {
            for (int i = 0; i < count; i++)
                if (snapshotHits && i % 4 == 0)
                    c.Storages[(requests[i].Address, requests[i].Slot)] = expected[i];
        });
        using ReadOnlySnapshotBundle bundle = new(FlatTestHelpers.SnapshotList(snapshot), reader,
            detailedMetrics, PersistedSnapshotStack.Empty(detailedMetrics));
        bundle.GetSlots(requests, actual);
        int expectedBatches = 0;
        int expectedScalars = 0;
        for (int start = 0; start < count; start += 64)
        {
            int misses = 0;
            for (int i = start; i < Math.Min(start + 64, count); i++)
                if (!snapshotHits || i % 4 != 0) misses++;
            if (misses >= 8) expectedBatches++;
            else expectedScalars += misses;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected), "missing, zero, full-width values and snapshot hits must retain their positions");
            Assert.That(reader.Reads, Is.EqualTo(expectedReads), "only misses must reach persistence, once and in order");
            Assert.That(reader.BatchCalls, Is.EqualTo(expectedBatches), "detailed metrics must not disable eligible batches");
            Assert.That(reader.ScalarCalls, Is.EqualTo(expectedScalars), "small miss sets must retain scalar reads");
        }
    }

    [Test]
    [NonParallelizable]
    public void GetSlots_RecordsOneObservationForEachBatch([Values] bool detailedMetrics)
    {
        const int count = 8;
        (Address Address, int SelfDestructIdx, UInt256 Slot)[] requests = new (Address, int, UInt256)[count];
        for (int i = 0; i < count; i++) requests[i] = (TestItem.AddressA, -1, (UInt256)(ulong)i);
        UInt256?[] actual = new UInt256?[count];
        IMetricObserver previousTimes = Metrics.ReadOnlySnapshotBundleTimes;
        IMetricObserver previousSizes = Metrics.ReadOnlySnapshotBundleReadBatchSize;
        IMetricObserver times = Substitute.For<IMetricObserver>();
        IMetricObserver sizes = Substitute.For<IMetricObserver>();
        try
        {
            Metrics.ReadOnlySnapshotBundleTimes = times;
            Metrics.ReadOnlySnapshotBundleReadBatchSize = sizes;
            SlotBatchReader reader = new();
            using ReadOnlySnapshotBundle bundle = new(new SnapshotPooledList(0), reader, detailedMetrics, PersistedSnapshotStack.Empty(detailedMetrics));
            bundle.GetSlots(requests, actual);
            int observations = detailedMetrics ? 1 : 0;
            Assert.That(reader.BatchCalls, Is.EqualTo(1));
            times.Received(observations).Observe(Arg.Any<double>(), Arg.Any<IMetricLabels>());
            times.Received(observations).Observe(Arg.Any<double>(), Arg.Is<IMetricLabels>(labels => labels.Labels.Length == 1 && labels.Labels[0] == "storage_batch"));
            sizes.Received(observations).Observe(Arg.Any<double>(), Arg.Any<IMetricLabels>());
            sizes.Received(observations).Observe(count, Arg.Is<IMetricLabels>(labels => labels.Labels.Length == 1 && labels.Labels[0] == "storage_batch"));
        }
        finally
        {
            Metrics.ReadOnlySnapshotBundleTimes = previousTimes;
            Metrics.ReadOnlySnapshotBundleReadBatchSize = previousSizes;
        }
    }

    [Test]
    [NonParallelizable]
    public void GetSlots_SmallMissSet_PreservesScalarMetricLabels([Values(0, 1, 2)] int slot)
    {
        IMetricObserver previous = Metrics.ReadOnlySnapshotBundleTimes;
        IMetricObserver times = Substitute.For<IMetricObserver>();
        try
        {
            Metrics.ReadOnlySnapshotBundleTimes = times;
            SlotBatchReader reader = new();
            using ReadOnlySnapshotBundle bundle = new(new SnapshotPooledList(0), reader, true, PersistedSnapshotStack.Empty(true));
            (Address Address, int SelfDestructIdx, UInt256 Slot)[] requests = [(TestItem.AddressA, -1, (UInt256)(ulong)slot)];
            UInt256?[] actual = new UInt256?[1];
            bundle.GetSlots(requests, actual);
            string label = slot == 2 ? "storage_persistence" : "storage_persistence_null";
            Assert.That(reader.ScalarCalls, Is.EqualTo(1));
            times.Received(1).Observe(Arg.Any<double>(), Arg.Any<IMetricLabels>());
            times.Received(1).Observe(Arg.Any<double>(), Arg.Is<IMetricLabels>(labels => labels.Labels.Length == 1 && labels.Labels[0] == label));
        }
        finally
        {
            Metrics.ReadOnlySnapshotBundleTimes = previous;
        }
    }

    private static UInt256? SlotValue(Address address, in UInt256 slot) => (slot.u0 % 3) switch
    {
        0 => null,
        1 => UInt256.Zero,
        _ => UInt256.MaxValue - slot - (address == TestItem.AddressA ? UInt256.One : (UInt256)2)
    };

    private sealed class SlotBatchReader : NoopPersistenceReader, IPersistence.IPersistenceReader, IBatchPersistenceReader
    {
        public List<StorageCell> Reads { get; } = [];
        public int BatchCalls { get; private set; }
        public int ScalarCalls { get; private set; }

        bool IPersistence.IPersistenceReader.TryGetSlot(Address address, in UInt256 slot, ref UInt256 value)
        {
            ScalarCalls++;
            UInt256? result = Read(address, in slot);
            value = result.GetValueOrDefault();
            return result.HasValue;
        }

        public void GetSlots(ReadOnlySpan<StorageCell> cells, Span<UInt256?> values)
        {
            BatchCalls++;
            for (int i = 0; i < cells.Length; i++) values[i] = Read(cells[i].Address, in cells[i].Index);
        }

        private UInt256? Read(Address address, in UInt256 slot)
        {
            Reads.Add(new StorageCell(address, in slot));
            return SlotValue(address, in slot);
        }
    }
}
