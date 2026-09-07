// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class ParallelUpdateRootTests
{
    [Test]
    public async Task Concurrent_independent_folds_equal_serial_fold()
    {
        (byte[] Key, byte[]? Value)[] entries = Entries(seed: 8297, count: 2000);
        using PbtTreeHarness serial = new();
        ValueHash256 expectedRoot = serial.ApplyBatch(entries);
        string[] expectedRecords = serial.CanonicalRecords();

        Task<(ValueHash256 Root, string[] Records)>[] tasks = new Task<(ValueHash256, string[])>[8];
        for (int worker = 0; worker < tasks.Length; worker++)
        {
            tasks[worker] = Task.Run(() =>
            {
                using PbtTreeHarness parallel = new();
                return (parallel.ApplyBatch(entries), parallel.CanonicalRecords());
            });
        }

        (ValueHash256 Root, string[] Records)[] results = await Task.WhenAll(tasks);
        foreach ((ValueHash256 root, string[] records) in results)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(expectedRoot));
                Assert.That(records, Is.EqualTo(expectedRecords));
            }
        }
    }

    [Test]
    public async Task Concurrent_group_overlays_keep_fetch_parse_and_write_counters_local()
    {
        (byte[] Key, byte[]? Value)[] initial = Entries(seed: 73, count: 256);
        Task<(ValueHash256 Root, TrieUpdaterMetrics Metrics, int Writes)>[] tasks =
        [
            Task.Run(() => ApplyWithMetrics(initial)),
            Task.Run(() => ApplyWithMetrics(initial)),
            Task.Run(() => ApplyWithMetrics(initial)),
            Task.Run(() => ApplyWithMetrics(initial)),
        ];

        (ValueHash256 Root, TrieUpdaterMetrics Metrics, int Writes)[] results = await Task.WhenAll(tasks);
        foreach ((ValueHash256 root, TrieUpdaterMetrics metrics, int writes) in results)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(results[0].Root));
                Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(metrics.GroupFrameResolutions));
                Assert.That(metrics.GroupFrameResolutions, Is.LessThan(256), "frame resolutions follow group crossings, not logical nodes");
                Assert.That(metrics.GroupParses, Is.LessThanOrEqualTo(metrics.PhysicalGroupFetches));
                Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(writes));
            }
        }
    }

    [Test]
    public async Task Concurrent_serial_folds_match_across_mutation_sequences()
    {
        (byte[] Key, byte[]? Value)[] initial = Entries(seed: 17, count: 1000);
        (byte[] Key, byte[]? Value)[] changes = Changes(initial);
        Task<PbtTreeHarness>[] tasks =
        [
            Task.Run(() => Apply(initial, changes)),
            Task.Run(() => Apply(initial, changes)),
        ];

        PbtTreeHarness[] trees = await Task.WhenAll(tasks);
        using (trees[0])
        using (trees[1])
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trees[1].RootHash, Is.EqualTo(trees[0].RootHash));
            Assert.That(trees[1].CanonicalRecords(), Is.EqualTo(trees[0].CanonicalRecords()));
        }
    }

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(3, false)]
    [TestCase(1, true)]
    [TestCase(3, true)]
    [TestCase(1, false, true)]
    [TestCase(3, false, true)]
    public void Partition_folds_preserve_canonical_and_physical_records_across_reopen(int populatedZones, bool compressed, bool singleLeafPerZone = false)
    {
        using PbtNodeGroupStore store = new();
        using PbtTreeHarness sequential = new();
        EipReferenceTree oracle = new();
        (byte[] Key, byte[]? Value)[] entries = ZoneEntries(populatedZones, compressed);
        if (singleLeafPerZone)
        {
            List<(byte[] Key, byte[]? Value)> singleLeaves = [];
            foreach ((byte[] key, byte[]? value) in entries)
                if (key[1] == 0xF0 && key[^1] == 2) singleLeaves.Add((key, value));
            entries = [.. singleLeaves];
        }
        ValueHash256 root = default;
        ApplyAndCompare(entries);
        ApplyAndCompare(Changes(entries));
        // Code-only work leaves Account's shared ancestor and the entire Storage zone intact.
        ApplyAndCompare(ZoneEntries(2, compressed)[..1]);
        List<(byte[] Key, byte[]? Value)> deletes = [];
        foreach ((byte[] key, _) in entries) deletes.Add((key, null));
        foreach ((byte[] key, _) in ZoneEntries(2, compressed)[..1]) deletes.Add((key, null));
        ApplyAndCompare([.. deletes]);
        Assert.That(root, Is.EqualTo(default(ValueHash256)));

        void ApplyAndCompare((byte[] Key, byte[]? Value)[] mutations)
        {
            root = TrieUpdater.UpdateRoot(store, root, PreparePartitions(mutations));
            sequential.ApplyBatch(mutations);
            foreach ((byte[] key, byte[]? value) in mutations)
            {
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(sequential.RootHash));
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                Assert.That(PhysicalRecords(store), Is.EqualTo(PhysicalRecords(sequential.PhysicalPayloads)));
                Assert.That(TrieUpdater.UpdateRoot(reopened, root, PreparePartitions(mutations)), Is.EqualTo(root));
                Assert.That(PhysicalRecords(reopened), Is.EqualTo(PhysicalRecords(store)));
            }
        }
    }

    [TestCase(8297)]
    [TestCase(9341)]
    public void Random_partition_mutations_match_reference_after_each_fold(int seed)
    {
        Random random = new(seed);
        using PbtNodeGroupStore store = new();
        using PbtTreeHarness sequential = new();
        EipReferenceTree oracle = new();
        (byte[] Key, byte[]? Value)[] entries = ZoneEntries(3, false);
        foreach ((byte[] key, _) in entries) random.NextBytes(key.AsSpan(2));
        ValueHash256 root = default;
        for (int round = 0; round < 40; round++)
        {
            (byte[] Key, byte[]? Value)[] changes = new (byte[], byte[]?)[100];
            for (int index = 0; index < changes.Length; index++)
            {
                byte[] key = entries[random.Next(entries.Length)].Key;
                byte[]? value = random.Next(3) == 0 ? null : Value((byte)random.Next(1, 256));
                changes[index] = (key, value);
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }
            root = TrieUpdater.UpdateRoot(store, root, PreparePartitions(changes));
            sequential.ApplyBatch(changes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(sequential.RootHash), $"round {round}");
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"round {round}");
                Assert.That(PhysicalRecords(store), Is.EqualTo(PhysicalRecords(sequential.PhysicalPayloads)), $"round {round}");
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Zone_workers_write_disjoint_groups_and_join_before_returning(bool failWorker)
    {
        (byte[] Key, byte[]? Value)[] initial = ZoneEntries(3, false);
        using CoordinatedStore store = new();
        using PbtTreeHarness sequential = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, PreparePartitions(initial));
        sequential.ApplyBatch(initial);
        string[] initialRecords = PhysicalRecords(store.Inner);
        (byte[] Key, byte[]? Value)[] changes = Changes(initial);
        Dictionary<PbtPartition, PbtWriteBatch> prepared = PreparePartitions(changes);
        store.Coordinate = true;
        store.FailWorker = failWorker;
        store.Writes = 0;
        if (failWorker)
        {
            Assert.Throws<AggregateException>(() => TrieUpdater.UpdateRoot(store, root, prepared));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(store.Writes, Is.GreaterThan(0), "partial writes belong to the caller on failure");
                Assert.That(PhysicalRecords(store.Inner), Is.Not.EqualTo(initialRecords));
                Assert.That(store.ArrivedWorkers, Is.EqualTo(3));
                Assert.That(store.ActiveReads, Is.Zero, "all workers joined before failure returns");
            }
            Assert.That(store.DuplicateWrites, Is.False, "each group has one owner");
            return;
        }
        ValueHash256 result = TrieUpdater.UpdateRoot(store, root, prepared);
        sequential.ApplyBatch(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.ArrivedWorkers, Is.EqualTo(3), "all three zone folds reached the barrier concurrently");
            Assert.That(result, Is.EqualTo(sequential.RootHash));
            Assert.That(PhysicalRecords(store.Inner), Is.EqualTo(PhysicalRecords(sequential.PhysicalPayloads)));
            Assert.That(store.ActiveReads, Is.Zero);
            Assert.That(store.DuplicateWrites, Is.False, "each group has one owner");
            Assert.Throws<InvalidOperationException>(() => TrieUpdater.UpdateRoot(store, result, prepared));
        }
    }

    private static Dictionary<PbtPartition, PbtWriteBatch> PreparePartitions((byte[] Key, byte[]? Value)[] changes)
    {
        Dictionary<PbtPartition, PbtWriteBatch> prepared = [];
        foreach (PbtPartition partition in new[] { PbtPartition.Account, PbtPartition.Code, PbtPartition.Storage })
        {
            using PbtWriteBatchBuilder batch = new(2);
            foreach ((byte[] key, byte[]? value) in changes)
            {
                PbtFullKey fullKey = new(key);
                if (PbtWriteBatchSet.PartitionOf(fullKey) == (int)partition)
                    batch.SetLeaf(fullKey, value is null ? null : new ValueHash256(value));
            }
            if (batch.Count != 0) prepared.Add(partition, batch.Build());
        }
        return prepared;
    }

    private static (byte[] Key, byte[]? Value)[] ZoneEntries(int populatedZones, bool compressed)
    {
        List<(byte[] Key, byte[]? Value)> entries = [];
        byte[] zones = [0x01, 0x00, 0xFF];
        for (int partition = 0; partition < populatedZones; partition++)
        for (int shard = 15; shard >= 0; shard--)
        for (int suffix = 3; suffix >= 0; suffix--)
        {
            byte[] key = new byte[zones[partition] == 0xFF ? 66 : 34];
            key[0] = zones[partition];
            key[compressed ? 12 : 1] = (byte)(shard << 4);
            key[^1] = (byte)(suffix ^ 1);
            entries.Add((key, Value((byte)(suffix + 1))));
        }
        return [.. entries];
    }

    private static string[] PhysicalRecords(PbtNodeGroupStore store) => PhysicalRecords(store.ExportPhysicalPayloads());

    private static string[] PhysicalRecords(IEnumerable<PbtPhysicalPayload> payloads)
    {
        List<string> records = [];
        foreach (PbtPhysicalPayload payload in payloads)
            records.Add(Convert.ToHexString(payload.Key.Span) + Convert.ToHexString(payload.Payload.Span));
        records.Sort(StringComparer.Ordinal);
        return [.. records];
    }

    private sealed class CoordinatedStore : IPbtStore, IDisposable
    {
        private readonly Barrier _barrier = new(3);
        private readonly HashSet<PbtNodePath> _writtenGroups = [];
        private int _arrivedWorkers;
        private int _activeReads;
        internal PbtNodeGroupStore Inner { get; } = new();
        internal bool Coordinate { get; set; }
        internal bool FailWorker { get; set; }
        internal int Writes { get; set; }
        internal int ArrivedWorkers => _arrivedWorkers;
        internal int ActiveReads => _activeReads;
        internal bool DuplicateWrites { get; private set; }

        public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey)
        {
            Interlocked.Increment(ref _activeReads);
            try
            {
                if (Coordinate && groupKey.BitDepth == 8)
                {
                    Interlocked.Increment(ref _arrivedWorkers);
                    if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("The independent zone folds did not overlap.");
                }
                if (FailWorker && groupKey.BitDepth > 12 && groupKey.Path[0] == 0x01 && groupKey.Path[1] >= 0x10)
                    throw new InvalidDataException("Injected worker failure after folding the first nibble.");
                return Inner.GetNodeGroup(groupKey);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload)
        {
            lock (_writtenGroups)
            {
                if (Writes == 0) _writtenGroups.Clear();
                DuplicateWrites |= !_writtenGroups.Add(groupKey);
                Writes++;
                Inner.SetNodeGroup(groupKey, payload);
            }
        }

        public void Dispose()
        {
            _barrier.Dispose();
            Inner.Dispose();
        }
    }

    private static PbtTreeHarness Apply(params (byte[] Key, byte[]? Value)[][] batches)
    {
        PbtTreeHarness tree = new();
        foreach ((byte[] Key, byte[]? Value)[] batch in batches) tree.ApplyBatch(batch);
        return tree;
    }

    private static (ValueHash256 Root, TrieUpdaterMetrics Metrics, int Writes) ApplyWithMetrics(
        (byte[] Key, byte[]? Value)[] entries)
    {
        using CountingStore store = new();
        using PbtWriteBatchBuilder batch = new(0);
        foreach ((byte[] key, byte[]? value) in entries)
            batch.Set(new PbtFullKey(key), new ValueHash256(value!));
        TrieUpdaterMetrics metrics = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, batch.Build(), metrics);
        return (root, metrics, store.Writes);
    }

    private static (byte[] Key, byte[]? Value)[] Entries(int seed, int count)
    {
        Random random = new(seed);
        (byte[] Key, byte[]? Value)[] entries = new (byte[], byte[]?)[count];
        for (int index = 0; index < entries.Length; index++)
        {
            byte[] key = new byte[2 + random.Next(63)];
            key[0] = (byte)index;
            random.NextBytes(key.AsSpan(1));
            byte[] value = new byte[32];
            random.NextBytes(value);
            entries[index] = (key, value);
        }
        return entries;
    }

    private static (byte[] Key, byte[]? Value)[] Changes((byte[] Key, byte[]? Value)[] initial)
    {
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < initial.Length; index += 3)
        {
            changes.Add((initial[index].Key, index % 2 == 0 ? null : Value((byte)index)));
        }
        return [.. changes];
    }

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }

    private sealed class CountingStore : IPbtStore, IDisposable
    {
        private readonly PbtNodeGroupStore _inner = new();

        internal int Writes { get; private set; }

        public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey) => _inner.GetNodeGroup(groupKey);

        public void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload)
        {
            Writes += _inner.CountNodeChanges(groupKey, payload);
            _inner.SetNodeGroup(groupKey, payload);
        }

        public void Dispose() => _inner.Dispose();
    }
}
