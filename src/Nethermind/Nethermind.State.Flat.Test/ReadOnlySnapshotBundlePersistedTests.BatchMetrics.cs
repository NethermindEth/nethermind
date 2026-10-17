// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public partial class ReadOnlySnapshotBundlePersistedTests
{
    private static IEnumerable<TestCaseData> BatchMetricsCases()
    {
        foreach (bool storage in new[] { false, true })
            foreach (bool detailedMetrics in new[] { false, true })
                foreach (int count in new[] { 1, 7, 8, 64, 65, 72, 129 })
                    yield return new TestCaseData(storage, detailedMetrics, count)
                        .SetName($"BatchRlps_{(storage ? "Storage" : "State")}_Metrics{detailedMetrics}_Count{count}_PreservesBatchRouting");
    }

    [TestCaseSource(nameof(BatchMetricsCases))]
    public void BatchRlps_WithDetailedMetrics_PreservesBatchRouting(bool storage, bool detailedMetrics, int count)
    {
        Hash256 address = Keccak.Compute("batch-metrics-address");
        TreePath[] paths = new TreePath[count];
        (TreePath Path, Hash256 Hash)[] requests = new (TreePath, Hash256)[count];
        byte[]?[] expected = new byte[]?[count];
        byte[]?[] actual = new byte[]?[count];
        Dictionary<TreePath, byte[]?> stored = [];
        for (int i = 0; i < count; i++)
        {
            Hash256 hash = Keccak.Compute($"batch-metrics-{i}");
            paths[i] = new TreePath(hash, 16);
            requests[i] = (paths[i], hash);
            byte[]? value = (i % 4) switch
            {
                0 => null,
                1 => [],
                2 => new byte[513 + i],
                _ => [(byte)i, 0xbb, 0xcc]
            };
            if (value is { Length: > 0 }) value[^1] = (byte)i;
            stored[paths[i]] = expected[i] = value;
        }
        BatchMetricsReader reader = new(storage, address, stored);
        using ReadOnlySnapshotBundle bundle = new(new SnapshotPooledList(0), reader, detailedMetrics, PersistedSnapshotStack.Empty());
        if (storage) bundle.LoadStorageRlps(address, requests, actual);
        else bundle.LoadStateRlps(requests, actual);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected), "metrics must preserve missing, empty and multi-byte values in request order");
            Assert.That(reader.Paths, Is.EqualTo(paths), "each request must reach the intended reader exactly once");
            Assert.That(reader.BatchCalls, Is.EqualTo(count / 64 + (count % 64 >= 8 ? 1 : 0)), "detailed metrics must not disable eligible batch reads");
            Assert.That(reader.ScalarCalls, Is.EqualTo(count % 64 < 8 ? count % 64 : 0), "small frontiers must retain scalar reads with either metrics setting");
        }
    }

    [NonParallelizable]
    [TestCase(false, false, TestName = "BatchStateRlps_MetricsOff_RecordsNothing")]
    [TestCase(false, true, TestName = "BatchStateRlps_MetricsOn_RecordsOneBatch")]
    [TestCase(true, false, TestName = "BatchStorageRlps_MetricsOff_RecordsNothing")]
    [TestCase(true, true, TestName = "BatchStorageRlps_MetricsOn_RecordsOneBatch")]
    public void BatchRlps_WithDetailedMetrics_RecordsOneBatchObservation(bool storage, bool detailedMetrics)
    {
        const int count = 8;
        Hash256 address = Keccak.Compute("batch-observation-address");
        (TreePath Path, Hash256 Hash)[] requests = new (TreePath, Hash256)[count];
        byte[]?[] actual = new byte[]?[count];
        Dictionary<TreePath, byte[]?> stored = [];
        for (int i = 0; i < count; i++)
        {
            Hash256 hash = Keccak.Compute($"batch-observation-{i}");
            requests[i] = (new TreePath(hash, 16), hash);
            stored[requests[i].Path] = [(byte)i, 0xcc];
        }
        IMetricObserver previousTimes = Metrics.ReadOnlySnapshotBundleTimes;
        IMetricObserver previousSizes = Metrics.ReadOnlySnapshotBundleReadBatchSize;
        IMetricObserver times = Substitute.For<IMetricObserver>();
        IMetricObserver sizes = Substitute.For<IMetricObserver>();
        try
        {
            Metrics.ReadOnlySnapshotBundleTimes = times;
            Metrics.ReadOnlySnapshotBundleReadBatchSize = sizes;
            BatchMetricsReader reader = new(storage, address, stored);
            using ReadOnlySnapshotBundle bundle = new(new SnapshotPooledList(0), reader, detailedMetrics, PersistedSnapshotStack.Empty());
            if (storage) bundle.LoadStorageRlps(address, requests, actual);
            else bundle.LoadStateRlps(requests, actual);

            string label = storage ? "storage_rlp_batch" : "state_rlp_batch";
            int observations = detailedMetrics ? 1 : 0;
            Assert.That(reader.BatchCalls, Is.EqualTo(1), "metrics must preserve the batch operation");
            times.Received(observations).Observe(Arg.Any<double>(), Arg.Any<IMetricLabels>());
            times.Received(observations).Observe(Arg.Any<double>(), Arg.Is<IMetricLabels>(labels => labels.Labels.Length == 1 && labels.Labels[0] == label));
            sizes.Received(observations).Observe(Arg.Any<double>(), Arg.Any<IMetricLabels>());
            sizes.Received(observations).Observe(count, Arg.Is<IMetricLabels>(labels => labels.Labels.Length == 1 && labels.Labels[0] == label));
        }
        finally
        {
            Metrics.ReadOnlySnapshotBundleTimes = previousTimes;
            Metrics.ReadOnlySnapshotBundleReadBatchSize = previousSizes;
        }
    }

    private sealed class BatchMetricsReader(bool storage, Hash256 address, Dictionary<TreePath, byte[]?> values)
        : NoopPersistenceReader, IPersistence.IPersistenceReader, IBatchTriePersistenceReader
    {
        public List<TreePath> Paths { get; } = [];
        public int BatchCalls { get; private set; }
        public int ScalarCalls { get; private set; }

        byte[]? IPersistence.IPersistenceReader.TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            ScalarCalls++;
            return Read(in path, null);
        }

        byte[]? IPersistence.IPersistenceReader.TryLoadStorageRlp(Hash256 storageAddress, in TreePath path, ReadFlags flags)
        {
            ScalarCalls++;
            return Read(in path, storageAddress);
        }

        public void LoadStateRlps(ReadOnlySpan<TreePath> paths, Span<byte[]?> results) => Load(paths, results, null);

        public void LoadStorageRlps(Hash256 storageAddress, ReadOnlySpan<TreePath> paths, Span<byte[]?> results) =>
            Load(paths, results, storageAddress);

        private void Load(ReadOnlySpan<TreePath> paths, Span<byte[]?> results, Hash256? storageAddress)
        {
            BatchCalls++;
            for (int i = 0; i < paths.Length; i++) results[i] = Read(in paths[i], storageAddress);
        }

        private byte[]? Read(in TreePath path, Hash256? storageAddress)
        {
            if (storage ? storageAddress != address : storageAddress is not null)
                throw new InvalidOperationException("Incorrect trie or storage address");
            Paths.Add(path);
            return values.GetValueOrDefault(path);
        }
    }
}
