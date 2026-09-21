// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
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
        byte[] insertedKey = PbtStoreTestExtensions.ZoneKey("01ABCD");
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
            root = TrieUpdater.UpdateRoot(store, root, PreparePartitions(writes), PbtTreeHarness.FoldQuota(), FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
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
                Assert.That(TrieUpdater.UpdateRoot(reopened, root, PreparePartitions(writes), PbtTreeHarness.FoldQuota(), FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null), Is.EqualTo(root));
                Assert.That(PhysicalRecords(reopened), Is.EqualTo(PhysicalRecords(store)));
            }
        }
    }

    [Test]
    public void Stored_prefixless_branches_are_hash_neutral_and_read_alongside_omitted_ones([Values] PbtPrefixlessBranchOmission omission)
    {
        using PbtNodeGroupStore store = new();
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] initial = RandomZoneEntries(new Random(42), 256);
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, PreparePartitions(initial), PbtTreeHarness.FoldQuota(), FoldFanOut.Default, omission, null);
        int[] storedWidths = StoredPrefixlessBranchWidths(store);
        int[] expectedWidths = omission switch
        {
            PbtPrefixlessBranchOmission.None => [2, 4, 8],
            PbtPrefixlessBranchOmission.OddLevels => [4],
            _ => [],
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(expected.ApplyBatch(initial)));
            Assert.That(storedWidths.Distinct(), Is.EquivalentTo(expectedWidths));
        }

        // Rewritten groups take the default layout while untouched ones keep theirs; both read alike.
        (byte[] Key, byte[]? Value)[] changes = Changes(initial);
        root = TrieUpdater.UpdateRoot(store, root, PreparePartitions(changes), PbtTreeHarness.FoldQuota(), FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(expected.ApplyBatch(changes)));
            // Depth-2 branches only occur in the dense top, which every batch rewrites, so OddLevels may convert entirely.
            Assert.That(StoredPrefixlessBranchWidths(store), omission switch
            {
                PbtPrefixlessBranchOmission.None => Has.Length.InRange(1, storedWidths.Length - 1),
                PbtPrefixlessBranchOmission.OddLevels => Has.Length.LessThan(storedWidths.Length),
                _ => Is.Empty,
            });
            Assert.That(LogicalRecords(store.EnumerateRecords()), Is.EqualTo(LogicalRecords(expected.Nodes)));
        }
    }

    /// <summary>The <see cref="PbtFourLevelGroupGeometry.WidthOf"/> of every stored branch that <see cref="PbtPrefixlessBranchOmission.Interior"/> would omit.</summary>
    private static int[] StoredPrefixlessBranchWidths(PbtNodeGroupStore store)
    {
        List<int> widths = [];
        foreach (PbtPhysicalPayload payload in store.ExportPhysicalPayloads())
        {
            PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(payload.Key, payload.Payload.Span);
            for (int position = 0; position < PbtFourLevelGroupGeometry.RootPosition; position++)
                if (reader.TryGetNode(position, out ReadOnlySpan<byte> encoding) && PbtNodeGroupCodec.ShouldOmit(PbtPrefixlessBranchOmission.Interior, position, encoding))
                    widths.Add(PbtFourLevelGroupGeometry.WidthOf(position));
        }
        return [.. widths];
    }

    private static string[] LogicalRecords(IReadOnlyList<PbtNodeRecord> records)
    {
        string[] encoded = new string[records.Count];
        for (int index = 0; index < records.Count; index++)
            encoded[index] = Convert.ToHexString(records[index].Path.ToEncodedArray()) + Convert.ToHexString(records[index].Encoding.Span);
        return encoded;
    }

    [Test]
    public void Sibling_prefix_jumps_restore_paths_across_mutations_and_reopen([Values] bool parallel)
    {
        using PbtNodeGroupStore store = new();
        EipReferenceTree oracle = new();
        Dictionary<string, byte[]> surviving = [];
        List<(byte[] Key, byte[]? Value)> initial = [];
        foreach (string zone in new[] { "00", "01", "FF" })
            foreach (string sibling in new[] { "0F", "10", "F0" })
                foreach (string suffix in new[] { "00", "01", "F0" })
                {
                    string padding = new('D', zone == "FF" ? 126 : 62);
                    initial.Add((Bytes.FromHexString(zone + sibling + padding + suffix), Value(1)));
                }
        ValueHash256 root = default;
        ApplyAndCompare(store, initial);
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < initial.Count; index++)
            changes.Add((initial[index].Key, index % 3 == 0 ? Value(2) : null));
        ApplyAndCompare(store, changes);
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
        ApplyAndCompare(reopened, initial);
        ApplyAndCompare(reopened, changes);

        void ApplyAndCompare(PbtNodeGroupStore target, List<(byte[] Key, byte[]? Value)> writes)
        {
            if (parallel)
            {
                using PbtPartitionBatches partitions = PreparePartitions([.. writes]);
                root = TrieUpdater.UpdateRoot(target, root, partitions, PbtTreeHarness.FoldQuota(), FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
            }
            else
            {
                using PbtWriteBatchBuilder<PbtStorageTreeKey> builder = new(0);
                foreach ((byte[] key, byte[]? value) in writes)
                {
                    if (value is null) builder.Delete(new PbtStorageTreeKey(key));
                    else builder.Set(new PbtStorageTreeKey(key), new ValueHash256(value));
                }
                root = TrieUpdater.UpdateRoot(target, root, builder.Build());
            }
            foreach ((byte[] key, byte[]? value) in writes)
            {
                if (value is null)
                {
                    oracle.Delete(key);
                    surviving.Remove(Convert.ToHexString(key));
                }
                else
                {
                    oracle.Insert(key, value);
                    surviving[Convert.ToHexString(key)] = value;
                }
            }
            using PbtTreeHarness rebuilt = new();
            List<(byte[] Key, byte[]? Value)> remaining = [];
            foreach ((string key, byte[] value) in surviving)
                remaining.Add((Bytes.FromHexString(key), value));
            rebuilt.ApplyBatch(remaining);
            IReadOnlyList<PbtNodeRecord> records = target.EnumerateRecords();
            string[] canonicalRecords = new string[records.Count];
            for (int index = 0; index < records.Count; index++)
                canonicalRecords[index] = Convert.ToHexString(records[index].Path.ToEncodedArray()) + Convert.ToHexString(records[index].Encoding.Span);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                Assert.That(root, Is.EqualTo(rebuilt.RootHash));
                Assert.That(canonicalRecords, Is.EqualTo(rebuilt.CanonicalRecords()));
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
            root = TrieUpdater.UpdateRoot(target, root, partitions, PbtTreeHarness.FoldQuota(), FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
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

    [TestCase(new[] { 2000 }, 1024, new[] { 1 })]
    [TestCase(new[] { 100, 100, 100 }, 1024, new[] { 3 })]
    [TestCase(new[] { 500, 600, 700 }, 1024, new[] { 3 })]
    [TestCase(new[] { 1024, 1024, 1024 }, 1024, new[] { 1, 2, 3 })]
    [TestCase(new[] { 500, 600, 1024, 10 }, 1024, new[] { 2, 4 })]
    [TestCase(new[] { 10, 5000, 10, 5000 }, 1024, new[] { 2, 4 })]
    [TestCase(new[] { 1, 1, 1 }, 0, new[] { 1, 2, 3 })]
    public void Bucket_runs_merge_consecutive_buckets_up_to_the_minimum(int[] counts, int minOperations, int[] expectedRunEnds)
    {
        int[] runEnds = new int[PbtFourLevelGroupGeometry.BoundarySlots];
        int runCount = TrieUpdater.PlanBucketRuns(counts, minOperations, runEnds);
        Assert.That(runEnds.AsSpan(0, runCount).ToArray(), Is.EqualTo(expectedRunEnds));
    }

    [TestCase(0, FoldFanOut.DefaultMinOperationsPerWorker)]
    [TestCase(FoldFanOut.DefaultLargeSubtreeBytes - 1, FoldFanOut.DefaultMinOperationsPerWorker)]
    [TestCase(FoldFanOut.DefaultLargeSubtreeBytes, FoldFanOut.DefaultLargeSubtreeMinOperationsPerWorker)]
    public void Worker_minimum_drops_from_the_large_subtree_size(long subtreeBytes, int expectedMinimum) =>
        Assert.That(FoldFanOut.Default.MinOperationsFor(subtreeBytes), Is.EqualTo(expectedMinimum));

    // One populated zone keeps the zone fan-out out of the picture, so any second thread is a bucket worker. Below the
    // large-subtree size a 40-operation frame folds on the calling thread alone; above it the same 40 operations form
    // two runs of the large-subtree minimum, and the store's barrier at the bucket groups proves both workers fold at once.
    [TestCase(64, false)]
    [TestCase(20000, true)]
    public void Bucket_fan_out_follows_the_stored_subtree_size(int keys, bool expectParallel)
    {
        (byte[] Key, byte[]? Value)[] initial = RandomZoneEntries(new Random(keys), keys).Where(entry => entry.Key[0] == 0x01).ToArray();
        (byte[] Key, byte[]? Value)[] changes = initial.Take(40).Select((entry, index) => (entry.Key, (byte[]?)Value((byte)(index + 1)))).ToArray();
        using BucketWorkerStore store = new();
        using PbtTreeHarness sequential = new();
        ConcurrencyController foldQuota = new(2);
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, PreparePartitions(initial), foldQuota, FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
        sequential.ApplyBatch(initial);
        store.Observe(coordinate: expectParallel);
        root = TrieUpdater.UpdateRoot(store, root, PreparePartitions(changes), foldQuota, FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
        sequential.ApplyBatch(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.ReadThreads, expectParallel ? Is.GreaterThan(1) : Is.EqualTo(1));
            Assert.That(root, Is.EqualTo(sequential.RootHash));
            Assert.That(PhysicalRecords(store.Inner), Is.EqualTo(PhysicalRecords(sequential.PhysicalPayloads)));
            Assert.That(AvailableWorkers(foldQuota), Is.EqualTo(1));
        }
    }

    // Zones with at least two worker minimums of operations fold their buckets on worker threads, each worker taking a
    // run of consecutive buckets; the single-batch harness is the serial oracle for both the root and the
    // byte-identical group payloads. 4096 changes are ~85 per depth-8 bucket, so a minimum of 64 gives every bucket
    // its own worker and 200 merges three buckets per worker.
    [TestCase(8297, 64, 40, 100, 0, FoldFanOut.DefaultMinOperationsPerWorker)]
    [TestCase(9341, 64, 40, 100, 0, FoldFanOut.DefaultMinOperationsPerWorker)]
    [TestCase(8297, 4096, 4, 4096, 0, 64)]
    [TestCase(8297, 4096, 4, 4096, 0, 200)]
    [TestCase(8297, 4096, 4, 4096, 1, 200)]
    public void Random_partition_mutations_match_reference_after_each_fold(int seed, int keysPerZone, int rounds, int changesPerRound, int foldConcurrency, int minOperationsPerWorker)
    {
        Random random = new(seed);
        using PbtNodeGroupStore store = new();
        using PbtTreeHarness sequential = new();
        EipReferenceTree oracle = new();
        (byte[] Key, byte[]? Value)[] entries = RandomZoneEntries(random, keysPerZone);
        ConcurrencyController foldQuota = new(foldConcurrency > 0 ? foldConcurrency : Environment.ProcessorCount);
        FoldFanOut fanOut = PbtTreeHarness.FanOut(minOperationsPerWorker);
        ValueHash256 root = default;
        for (int round = 0; round < rounds; round++)
        {
            (byte[] Key, byte[]? Value)[] changes = new (byte[], byte[]?)[changesPerRound];
            for (int index = 0; index < changes.Length; index++)
            {
                byte[] key = entries[random.Next(entries.Length)].Key;
                byte[]? value = random.Next(3) == 0 ? null : Value((byte)random.Next(1, 256));
                changes[index] = (key, value);
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }
            root = TrieUpdater.UpdateRoot(store, root, PreparePartitions(changes), foldQuota, fanOut, PbtPrefixlessBranchOmission.Interior, null);
            sequential.ApplyBatch(changes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(sequential.RootHash), $"round {round}");
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"round {round}");
                Assert.That(PhysicalRecords(store), Is.EqualTo(PhysicalRecords(sequential.PhysicalPayloads)), $"round {round}");
                PbtStoreTestExtensions.AssertSubtreeBytes(store.ExportPhysicalPayloads());
            }
        }
    }

    // 20000 keys per zone fan out at depth 8 and, with an 8-operation worker minimum, again at depth 12, so the join
    // and failure paths cover nested workers. A quota of three has the spare worker the zone fan-out is gated on.
    [Test]
    public void Zone_workers_write_disjoint_groups_and_join_before_returning([Values] bool failWorker, [Values(64, 20000)] int keysPerZone)
    {
        FoldFanOut fanOut = PbtTreeHarness.FanOut(8);
        const int foldConcurrency = 3;
        (byte[] Key, byte[]? Value)[] initial = RandomZoneEntries(new Random(keysPerZone), keysPerZone);
        using CoordinatedStore store = new();
        using PbtTreeHarness sequential = new();
        ConcurrencyController foldQuota = new(foldConcurrency);
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, PreparePartitions(initial), foldQuota, fanOut, PbtPrefixlessBranchOmission.Interior, null);
        sequential.ApplyBatch(initial);
        string[] initialRecords = PhysicalRecords(store.Inner);
        (byte[] Key, byte[]? Value)[] changes = Changes(initial);
        using PbtPartitionBatches prepared = PreparePartitions(changes);
        store.Coordinate = true;
        store.FailWorker = failWorker;
        store.Writes = 0;
        if (failWorker)
        {
            Assert.Throws<AggregateException>(() => TrieUpdater.UpdateRoot(store, root, prepared, foldQuota, fanOut, PbtPrefixlessBranchOmission.Interior, null));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(store.Writes, Is.GreaterThan(0), "partial writes belong to the caller on failure");
                Assert.That(PhysicalRecords(store.Inner), Is.Not.EqualTo(initialRecords));
                Assert.That(store.ArrivedWorkers, Is.EqualTo(3));
                Assert.That(store.ActiveReads, Is.Zero, "all workers joined before failure returns");
                Assert.That(AvailableWorkers(foldQuota), Is.EqualTo(foldConcurrency - 1), "failed folds return their quota");
            }
            Assert.That(store.DuplicateWrites, Is.False, "each group has one owner");
            return;
        }
        ValueHash256 result = TrieUpdater.UpdateRoot(store, root, prepared, foldQuota, fanOut, PbtPrefixlessBranchOmission.Interior, null);
        sequential.ApplyBatch(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.ArrivedWorkers, Is.EqualTo(3), "all three zone folds reached the barrier concurrently");
            Assert.That(result, Is.EqualTo(sequential.RootHash));
            Assert.That(PhysicalRecords(store.Inner), Is.EqualTo(PhysicalRecords(sequential.PhysicalPayloads)));
            Assert.That(store.ActiveReads, Is.Zero);
            Assert.That(store.DuplicateWrites, Is.False, "each group has one owner");
            Assert.That(AvailableWorkers(foldQuota), Is.EqualTo(foldConcurrency - 1), "completed folds return their quota");
            Assert.Throws<InvalidOperationException>(() => TrieUpdater.UpdateRoot(store, result, prepared, foldQuota, fanOut, PbtPrefixlessBranchOmission.Interior, null));
        }
    }

    // Reads are the only observable moment of a fold, so overlapping reads show threads folding at once: a quota
    // without a spare worker keeps every frame serial, a quota with one lets the zones overlap, and either way the
    // quota must be whole again afterwards.
    [Test]
    public void Fold_is_serial_without_spare_quota_and_returns_it([Values(1, 2, 4)] int foldConcurrency)
    {
        FoldFanOut fanOut = PbtTreeHarness.FanOut(8);
        (byte[] Key, byte[]? Value)[] initial = RandomZoneEntries(new Random(foldConcurrency), 20000);
        using OverlapCountingStore store = new();
        using PbtTreeHarness sequential = new();
        ConcurrencyController foldQuota = new(foldConcurrency);
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, PreparePartitions(initial), foldQuota, fanOut, PbtPrefixlessBranchOmission.Interior, null);
        sequential.ApplyBatch(initial);
        (byte[] Key, byte[]? Value)[] changes = Changes(initial);
        root = TrieUpdater.UpdateRoot(store, root, PreparePartitions(changes), foldQuota, fanOut, PbtPrefixlessBranchOmission.Interior, null);
        sequential.ApplyBatch(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.MaxOverlappingReads, foldConcurrency == 1 ? Is.EqualTo(1) : Is.GreaterThan(1));
            Assert.That(AvailableWorkers(foldQuota), Is.EqualTo(foldConcurrency - 1));
            Assert.That(root, Is.EqualTo(sequential.RootHash));
            Assert.That(PhysicalRecords(store.Inner), Is.EqualTo(PhysicalRecords(sequential.PhysicalPayloads)));
        }
    }

    private static int AvailableWorkers(ConcurrencyController quota)
    {
        int taken = 0;
        while (quota.TryRequestConcurrencyQuota()) taken++;
        for (int worker = 0; worker < taken; worker++) quota.ReturnConcurrencyQuota();
        return taken;
    }

    [Test]
    public void Group_hashes_match_independent_boundary_subtrees_across_mutations(
        [Values] bool parallel, [Values] bool compressed)
    {
        using HashRecordingStore store = new();
        EipReferenceTree oracle = new();
        ValueHash256 root = default;
        (byte[] Key, byte[]? Value)[] entries = ZoneEntries(3, compressed);
        int nonRootReads = 0;
        int nonRootWrites = 0;
        int nullWrites = 0;
        int survivingNullWrites = 0;
        ApplyAndCheck(entries);
        ApplyAndCheck(Changes(entries));
        List<(byte[] Key, byte[]? Value)> promotions = [];
        List<(byte[] Key, byte[]? Value)> deletions = [];
        for (int index = 0; index < entries.Length; index++)
        {
            // Retain one leaf per zone so deeper groups disappear through promotion.
            if (index % 64 != 1) promotions.Add((entries[index].Key, null));
            deletions.Add((entries[index].Key, null));
        }
        ApplyAndCheck([.. promotions]);
        ApplyAndCheck(entries);
        ApplyAndCheck([.. deletions]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(default(ValueHash256)));
            Assert.That(nonRootReads, Is.GreaterThan(0));
            Assert.That(nonRootWrites, Is.GreaterThan(0));
            Assert.That(nullWrites, Is.GreaterThan(0));
            Assert.That(survivingNullWrites, Is.GreaterThan(0), "physical group removal can retain a logical subtree");
        }

        void ApplyAndCheck((byte[] Key, byte[]? Value)[] changes)
        {
            store.Reads.Clear();
            store.Writes.Clear();
            if (parallel)
            {
                using PbtPartitionBatches partitions = PreparePartitions(changes);
                root = TrieUpdater.UpdateRoot(store, root, partitions, PbtTreeHarness.FoldQuota(), FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
            }
            else
            {
                using PbtWriteBatchBuilder<PbtStorageTreeKey> builder = new(0);
                foreach ((byte[] key, byte[]? value) in changes)
                {
                    if (value is null) builder.Delete(new PbtStorageTreeKey(key));
                    else builder.Set(new PbtStorageTreeKey(key), new ValueHash256(value));
                }
                root = TrieUpdater.UpdateRoot(store, root, builder.Build());
            }

            foreach ((PbtStorageNodePath path, ValueHash256 hash) in store.Reads)
            {
                Assert.That(hash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize(path)), $"old subtree read at {path}");
                if (path.BitDepth != 0) nonRootReads++;
            }
            foreach ((byte[] key, byte[]? value) in changes)
            {
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                foreach ((PbtStorageNodePath path, ValueHash256 hash, bool isNull) in store.Writes)
                {
                    Assert.That(hash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize(path)), $"new subtree write at {path}, null payload: {isNull}");
                    if (path.BitDepth != 0) nonRootWrites++;
                    if (isNull)
                    {
                        nullWrites++;
                        if (hash != default) survivingNullWrites++;
                    }
                }
            }
        }
    }

    private sealed class HashRecordingStore : IPbtStore, IDisposable
    {
        private readonly PbtNodeGroupStore _store = new();
        internal List<(PbtStorageNodePath Path, ValueHash256 Hash)> Reads { get; } = [];
        internal List<(PbtStorageNodePath Path, ValueHash256 Hash, bool IsNull)> Writes { get; } = [];

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash)
        {
            lock (Reads) Reads.Add((groupKey.ToPath<PbtStorageNodePath>(), hash));
            return _store.GetNodeGroup(groupKey, hash);
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash, RefCountingMemory? payload)
        {
            lock (Writes) Writes.Add((groupKey.ToPath<PbtStorageNodePath>(), hash, payload is null));
            _store.SetNodeGroup(groupKey, hash, payload);
        }

        public void Dispose() => _store.Dispose();
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

    /// <summary>Keys spread uniformly below each zone byte, so every group level fans out into all sixteen buckets.</summary>
    private static (byte[] Key, byte[]? Value)[] RandomZoneEntries(Random random, int keysPerZone)
    {
        (byte[] Key, byte[]? Value)[] entries = new (byte[], byte[]?)[3 * keysPerZone];
        byte[] zones = [0x01, 0x00, 0xFF];
        for (int index = 0; index < entries.Length; index++)
        {
            byte zone = zones[index / keysPerZone];
            byte[] key = new byte[zone == 0xFF ? 66 : 34];
            random.NextBytes(key);
            key[0] = zone;
            entries[index] = (key, Value((byte)(index % 4 + 1)));
        }
        return entries;
    }

    private static string[] PhysicalRecords(PbtNodeGroupStore store) => PhysicalRecords(store.ExportPhysicalPayloads());

    private static string[] PhysicalRecords(IEnumerable<PbtPhysicalPayload> payloads)
    {
        List<string> records = [];
        foreach (PbtPhysicalPayload payload in payloads)
            records.Add(Convert.ToHexString(payload.Key.ToEncodedArray()) + Convert.ToHexString(payload.Payload.Span));
        records.Sort(StringComparer.Ordinal);
        return [.. records];
    }

    private sealed class CoordinatedStore : IPbtStore, IDisposable
    {
        private readonly Barrier _barrier = new(3);
        private readonly HashSet<PbtStorageNodePath> _writtenGroups = [];
        private int _arrivedWorkers;
        private int _activeReads;
        internal PbtNodeGroupStore Inner { get; } = new();
        internal bool Coordinate { get; set; }
        internal bool FailWorker { get; set; }
        internal int Writes { get; set; }
        internal int ArrivedWorkers => _arrivedWorkers;
        internal int ActiveReads => _activeReads;
        internal bool DuplicateWrites { get; private set; }

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash)
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
                if (FailWorker && groupKey.BitDepth > 12 && groupKey.ToPath<PbtStorageNodePath>().GetByte(0) == 0x01 && groupKey.ToPath<PbtStorageNodePath>().GetByte(1) >= 0x10)
                    throw new InvalidDataException("Injected worker failure after folding the first nibble.");
                return Inner.GetNodeGroup(groupKey, hash);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash, RefCountingMemory? payload)
        {
            lock (_writtenGroups)
            {
                if (Writes == 0) _writtenGroups.Clear();
                DuplicateWrites |= !_writtenGroups.Add(groupKey.ToPath<PbtStorageNodePath>());
                Writes++;
                Inner.SetNodeGroup(groupKey, hash, payload);
            }
        }

        public void Dispose()
        {
            _barrier.Dispose();
            Inner.Dispose();
        }
    }

    private sealed class OverlapCountingStore : IPbtStore, IDisposable
    {
        private int _activeReads;
        private int _maxOverlappingReads;
        internal PbtNodeGroupStore Inner { get; } = new();
        internal int MaxOverlappingReads => _maxOverlappingReads;

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash)
        {
            int active = Interlocked.Increment(ref _activeReads);
            InterlockedEx.Max(ref _maxOverlappingReads, active);
            try
            {
                // Hold the read open long enough for concurrently folding threads to overlap in it.
                Thread.SpinWait(1000);
                return Inner.GetNodeGroup(groupKey, hash);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash, RefCountingMemory? payload) =>
            Inner.SetNodeGroup(groupKey, hash, payload);

        public void Dispose() => Inner.Dispose();
    }

    /// <summary>Counts the threads reading once observed; coordinating, the first two hold their first bucket-group read until both arrive.</summary>
    /// <remarks>Only reads below the zone frame take part, so the calling thread's reads before the fan-out cannot block on a worker that never starts.</remarks>
    private sealed class BucketWorkerStore : IPbtStore, IDisposable
    {
        private const int BucketGroupBitDepth = 12;
        private readonly Barrier _barrier = new(2);
        private readonly HashSet<int> _readThreads = [];
        private readonly HashSet<int> _bucketReadThreads = [];
        private bool _observing;
        private bool _coordinate;
        internal PbtNodeGroupStore Inner { get; } = new();

        internal int ReadThreads
        {
            get { lock (_readThreads) return _readThreads.Count; }
        }

        internal void Observe(bool coordinate)
        {
            _observing = true;
            _coordinate = coordinate;
        }

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash)
        {
            if (_observing)
            {
                bool firstBucketRead;
                int arrived;
                lock (_readThreads)
                {
                    _readThreads.Add(Environment.CurrentManagedThreadId);
                    firstBucketRead = groupKey.BitDepth >= BucketGroupBitDepth && _bucketReadThreads.Add(Environment.CurrentManagedThreadId);
                    arrived = _bucketReadThreads.Count;
                }
                if (_coordinate && firstBucketRead && arrived <= 2 && !_barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("A second bucket worker never read a group.");
            }
            return Inner.GetNodeGroup(groupKey, hash);
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash, RefCountingMemory? payload) =>
            Inner.SetNodeGroup(groupKey, hash, payload);

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
        using PbtWriteBatchBuilder<PbtStorageTreeKey> batch = new(0);
        foreach ((byte[] key, byte[]? value) in entries)
            batch.Set(new PbtStorageTreeKey(key), new ValueHash256(value!));
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
