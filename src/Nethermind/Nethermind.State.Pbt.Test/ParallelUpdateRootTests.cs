// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    public async Task Concurrent_group_overlays_keep_fetch_and_parse_counters_local()
    {
        (byte[] Key, byte[]? Value)[] initial = Entries(seed: 73, count: 256);
        Task<(ValueHash256 Root, TrieUpdaterMetrics Metrics)>[] tasks =
        [
            Task.Run(() => ApplyWithMetrics(initial)),
            Task.Run(() => ApplyWithMetrics(initial)),
            Task.Run(() => ApplyWithMetrics(initial)),
            Task.Run(() => ApplyWithMetrics(initial)),
        ];

        (ValueHash256 Root, TrieUpdaterMetrics Metrics)[] results = await Task.WhenAll(tasks);
        foreach ((ValueHash256 root, TrieUpdaterMetrics metrics) in results)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(results[0].Root));
                Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(1), "only the root group is read when building an empty tree");
                Assert.That(metrics.GroupFrameResolutions, Is.LessThan(256), "frame resolutions follow group crossings, not logical nodes");
                Assert.That(metrics.GroupParses, Is.LessThanOrEqualTo(metrics.PhysicalGroupFetches));
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

    [Test]
    public void Partition_folds_preserve_canonical_and_physical_records_across_reopen(
        [Values(0, 1, 3)] int populatedZones,
        [Values(false, true)] bool compressed,
        [Values(false, true)] bool singleLeafPerZone,
        [Values(false, true)] bool zeroDeletes)
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
        byte[] insertedKey = Bytes.FromHexString("01ABCD");
        ApplyAndCompare([(insertedKey, null)]);
        ApplyAndCompare([(insertedKey, Value(0xA5))]);
        ApplyAndCompare([(insertedKey, null)]);
        List<(byte[] Key, byte[]? Value)> deletes = [];
        foreach ((byte[] key, _) in entries) deletes.Add((key, null));
        foreach ((byte[] key, _) in ZoneEntries(2, compressed)[..1]) deletes.Add((key, null));
        ApplyAndCompare([.. deletes]);
        Assert.That(root, Is.EqualTo(default(ValueHash256)));

        void ApplyAndCompare((byte[] Key, byte[]? Value)[] mutations)
        {
            (byte[] Key, byte[]? Value)[] writes = new (byte[], byte[]?)[mutations.Length];
            for (int index = 0; index < mutations.Length; index++)
                writes[index] = (mutations[index].Key, zeroDeletes && mutations[index].Value is null ? new byte[32] : mutations[index].Value);
            root = TrieUpdater.UpdateRoot(store, root, PreparePartitions(writes));
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
                Assert.That(TrieUpdater.UpdateRoot(reopened, root, PreparePartitions(writes)), Is.EqualTo(root));
                Assert.That(PhysicalRecords(reopened), Is.EqualTo(PhysicalRecords(store)));
            }
        }
    }

    [TestCase(0x00)]
    [TestCase(0x01)]
    public void Storage_singleton_survives_small_partition_expansion_promotion_and_reopen(byte smallZone)
    {
        byte[] storageKey = new byte[66];
        storageKey[0] = 0xFF;
        storageKey[^1] = 0xA5;
        byte[] smallKey = new byte[34];
        smallKey[0] = smallZone;
        smallKey[^1] = 0x5A;
        using PbtNodeGroupStore store = new();
        using PbtTreeHarness sequential = new();
        EipReferenceTree oracle = new();
        ValueHash256 root = default;
        ApplyAndCompare(store, [(storageKey, Value(1))]);
        ValueHash256 singletonRoot = root;
        ApplyAndCompare(store, [(smallKey, Value(2))]);
        ApplyAndCompare(store, [(smallKey, null)]);
        Assert.That(root, Is.EqualTo(singletonRoot));
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
        ApplyAndCompare(reopened, [(smallKey, Value(3))]);
        ApplyAndCompare(reopened, [(smallKey, null)]);
        Assert.That(root, Is.EqualTo(singletonRoot));

        void ApplyAndCompare(PbtNodeGroupStore target, (byte[] Key, byte[]? Value)[] changes)
        {
            using PbtPartitionBatches partitions = PreparePartitions(changes);
            root = TrieUpdater.UpdateRoot(target, root, partitions);
            sequential.ApplyBatch(changes);
            foreach ((byte[] key, byte[]? value) in changes)
            {
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(sequential.RootHash));
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                Assert.That(PhysicalRecords(target), Is.EqualTo(PhysicalRecords(sequential.PhysicalPayloads)));
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
        using PbtPartitionBatches prepared = PreparePartitions(changes);
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

    private static PbtPartitionBatches PreparePartitions((byte[] Key, byte[]? Value)[] changes) =>
        PbtStoreTestExtensions.PreparePartitions(changes);

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
        private readonly HashSet<IPbtNodePath> _writtenGroups = [];
        private int _arrivedWorkers;
        private int _activeReads;
        internal PbtNodeGroupStore Inner { get; } = new();
        internal bool Coordinate { get; set; }
        internal bool FailWorker { get; set; }
        internal int Writes { get; set; }
        internal int ArrivedWorkers => _arrivedWorkers;
        internal int ActiveReads => _activeReads;
        internal bool DuplicateWrites { get; private set; }

        public RefCountingMemory? GetNodeGroup(IPbtNodePath groupKey)
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
                if (FailWorker && groupKey.BitDepth > 12 && groupKey.GetByte(0) == 0x01 && groupKey.GetByte(1) >= 0x10)
                    throw new InvalidDataException("Injected worker failure after folding the first nibble.");
                return Inner.GetNodeGroup(groupKey);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public void SetNodeGroup(IPbtNodePath groupKey, RefCountingMemory? payload)
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

    private static (ValueHash256 Root, TrieUpdaterMetrics Metrics) ApplyWithMetrics(
        (byte[] Key, byte[]? Value)[] entries)
    {
        using PbtNodeGroupStore store = new();
        using PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
        foreach ((byte[] key, byte[]? value) in entries)
            batch.Set(new PbtStorageFullKey(key), new ValueHash256(value!));
        TrieUpdaterMetrics metrics = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, batch.Build(), metrics);
        return (root, metrics);
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
}
