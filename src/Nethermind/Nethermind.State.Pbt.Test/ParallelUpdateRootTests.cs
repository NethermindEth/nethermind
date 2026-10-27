// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Snapshot;
using NUnit.Framework;
using static Nethermind.State.Pbt.Test.PbtStoreTestExtensions;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class ParallelUpdateRootTests
{
    [Test]
    public async Task Concurrent_independent_folds_equal_serial_fold_across_mutations()
    {
        (byte[] Key, byte[]? Value)[] entries = Entries(seed: 8297, count: 2000);
        (byte[] Key, byte[]? Value)[] changes = Changes(entries);
        using PbtTreeHarness serial = new();
        ValueHash256 expectedInitialRoot = serial.ApplyBatch(entries);
        ValueHash256 expectedRoot = serial.ApplyBatch(changes);
        string[] expectedRecords = serial.CanonicalRecords();

        Task<(ValueHash256 InitialRoot, ValueHash256 Root, string[] Records)>[] tasks = new Task<(ValueHash256, ValueHash256, string[])>[8];
        for (int worker = 0; worker < tasks.Length; worker++)
        {
            tasks[worker] = Task.Run(() =>
            {
                using PbtTreeHarness parallel = new();
                return (parallel.ApplyBatch(entries), parallel.ApplyBatch(changes), parallel.CanonicalRecords());
            });
        }

        (ValueHash256 InitialRoot, ValueHash256 Root, string[] Records)[] results = await Task.WhenAll(tasks);
        foreach ((ValueHash256 initialRoot, ValueHash256 root, string[] records) in results)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(initialRoot, Is.EqualTo(expectedInitialRoot));
                Assert.That(root, Is.EqualTo(expectedRoot));
                Assert.That(records, Is.EqualTo(expectedRecords));
            }
        }
    }

    [Test]
    public void Partition_folds_preserve_canonical_and_physical_records_across_reopen(
        [Values(1, 3)] int populatedZones,
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
                if (key[compressed ? 12 : 1] == 0xF0 && key[^1] == 2) singleLeaves.Add((key, value));
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
            root = store.Fold(root, writes);
            sequential.ApplyBatch(mutations);
            oracle.Apply(mutations);
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(sequential.RootHash));
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                Assert.That(store.PhysicalRecords(), Is.EqualTo(sequential.PhysicalPayloads.PhysicalRecords()));
                Assert.That(reopened.Fold(root, writes), Is.EqualTo(root));
                Assert.That(reopened.PhysicalRecords(), Is.EqualTo(store.PhysicalRecords()));
            }
        }
    }

    [Test]
    public void Sibling_prefix_jumps_restore_paths_across_mutations_and_reopen([Values] bool splitEveryFrame)
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
            root = target.Fold(root, writes, SplitFanOut(splitEveryFrame));
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
            string[] canonicalRecords = target.CanonicalRecords();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                Assert.That(root, Is.EqualTo(rebuilt.RootHash));
                Assert.That(canonicalRecords, Is.EqualTo(rebuilt.CanonicalRecords()));
            }
        }
    }

    [TestCase(new[] { 2000 }, new long[] { 0 }, 1024, new[] { 1 })]
    [TestCase(new[] { 100, 100, 100 }, new long[] { 0, 0, 0 }, 1024, new[] { 3 })]
    [TestCase(new[] { 500, 600, 700 }, new long[] { 0, 0, 0 }, 1024, new[] { 3 })]
    [TestCase(new[] { 1024, 1024, 1024 }, new long[] { 0, 0, 0 }, 1024, new[] { 1, 2, 3 })]
    [TestCase(new[] { 500, 600, 1024, 10 }, new long[] { 0, 0, 0, 0 }, 1024, new[] { 2, 4 })]
    [TestCase(new[] { 10, 5000, 10, 5000 }, new long[] { 0, 0, 0, 0 }, 1024, new[] { 2, 4 })]
    [TestCase(new[] { 1, 1, 1 }, new long[] { 0, 0, 0 }, 0, new[] { 1, 2, 3 })]
    // Zero-descendant cases cover the merging alone; the others keep the default large-subtree size of 32 KiB, which lowers the minimum to 16.
    [TestCase(new[] { 20, 20, 20, 140 }, new long[] { 0, 0, 0, 0 }, FoldFanOut.DefaultMinOperationsPerWorker, new[] { 4 }, TestName = "Small buckets need the full minimum")]
    [TestCase(new[] { 20, 20, 20, 140 }, new long[] { 40000, 0, 0, 0 }, FoldFanOut.DefaultMinOperationsPerWorker, new[] { 1, 4 }, TestName = "A large bucket cuts its own run early and leaves the next one whole")]
    [TestCase(new[] { 20, 20, 140 }, new long[] { 20000, 20000, 0 }, FoldFanOut.DefaultMinOperationsPerWorker, new[] { 2, 3 }, TestName = "Descendants accumulate across the buckets of a run")]
    [TestCase(new[] { 20, 20, 20, 140 }, new long[] { 20000, 20000, 20000, 0 }, FoldFanOut.DefaultMinOperationsPerWorker, new[] { 2, 4 }, TestName = "A cut forgets the descendants it already charged")]
    public void Bucket_runs_merge_consecutive_buckets_up_to_their_own_minimum(int[] counts, long[] descendantBytes, int minOperations, int[] expectedRunEnds)
    {
        int[] runEnds = new int[PbtTwoLevelGroupGeometry.BoundarySlots];
        int runCount = (FoldFanOut.Default with { MinOperationsPerWorker = minOperations }).PlanBucketRuns(counts, descendantBytes, runEnds);
        Assert.That(runEnds.AsSpan(0, runCount).ToArray(), Is.EqualTo(expectedRunEnds));
    }

    [TestCase(0, FoldFanOut.DefaultMinOperationsPerWorker)]
    [TestCase(FoldFanOut.DefaultLargeSubtreeBytes - 1, FoldFanOut.DefaultMinOperationsPerWorker)]
    [TestCase(FoldFanOut.DefaultLargeSubtreeBytes, FoldFanOut.DefaultLargeSubtreeMinOperationsPerWorker)]
    public void Worker_minimum_drops_from_the_large_subtree_size(long descendantBytes, int expectedMinimum)
    {
        int[] runEnds = new int[2];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PbtTreeHarness.DefaultFanOut.PlanBucketRuns([expectedMinimum - 1, FoldFanOut.DefaultMinOperationsPerWorker], [descendantBytes, 0], runEnds), Is.EqualTo(1), "one operation short of the minimum");
            Assert.That(PbtTreeHarness.DefaultFanOut.PlanBucketRuns([expectedMinimum, FoldFanOut.DefaultMinOperationsPerWorker], [descendantBytes, 0], runEnds), Is.EqualTo(2));
        }
    }

    // One populated zone keeps the zone fan-out out of the picture, so any second thread is a bucket worker. While the
    // buckets hold less than the large-subtree size below them a 40-operation frame folds on the calling thread alone;
    // once they hold more the same 40 operations form two runs of the large-subtree minimum, and the store's barrier at
    // the bucket groups proves both workers fold at once. A quota without a spare worker keeps even those runs on the
    // calling thread, and either way the quota must be whole again afterwards.
    [TestCase(64, 2, false)]
    [TestCase(20000, 2, true)]
    [TestCase(20000, 1, false)]
    public void Bucket_fan_out_follows_the_stored_descendants(int keys, int foldConcurrency, bool expectParallel)
    {
        (byte[] Key, byte[]? Value)[] initial = RandomZoneEntries(new Random(keys), keys).Where(entry => entry.Key[0] == 0x01).ToArray();
        (byte[] Key, byte[]? Value)[] changes = initial.Take(40).Select((entry, index) => (entry.Key, (byte[]?)Value((byte)(index + 1)))).ToArray();
        using BucketWorkerStore store = new();
        using PbtTreeHarness sequential = new();
        ConcurrencyController foldQuota = new(foldConcurrency);
        ValueHash256 root = store.Fold(default, initial, foldQuota, PbtTreeHarness.DefaultFanOut);
        sequential.ApplyBatch(initial);
        store.Observe(coordinate: expectParallel);
        root = store.Fold(root, changes, foldQuota, PbtTreeHarness.DefaultFanOut);
        sequential.ApplyBatch(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.ReadThreads, expectParallel ? Is.GreaterThan(1) : Is.EqualTo(1));
            Assert.That(root, Is.EqualTo(sequential.RootHash));
            Assert.That(store.Inner.PhysicalRecords(), Is.EqualTo(sequential.PhysicalPayloads.PhysicalRecords()));
            Assert.That(AvailableWorkers(foldQuota), Is.EqualTo(foldConcurrency - 1));
        }
    }

    // Zones with at least two worker minimums of operations fold their buckets on worker threads, each worker taking a
    // run of consecutive buckets; a fold at a quota of one is the serial oracle for both the root and the
    // byte-identical group payloads. 4096 changes are ~85 per bucket of each zone's four depth-10 groups, so a minimum of 64 gives every bucket
    // its own worker and 200 merges three buckets per worker.
    [TestCase(0, 64)]
    [TestCase(0, 200)]
    [TestCase(1, 200)]
    public void Random_partition_mutations_match_reference_after_each_fold(int foldConcurrency, int minOperationsPerWorker)
    {
        const int keysPerZone = 4096;
        const int rounds = 4;
        const int changesPerRound = 4096;
        Random random = new(8297);
        (byte[] Key, byte[]? Value)[] entries = RandomZoneEntries(random, keysPerZone);
        ConcurrencyController foldQuota = new(foldConcurrency > 0 ? foldConcurrency : Environment.ProcessorCount);
        using DifferentialTree tree = new(PbtTreeHarness.FanOut(minOperationsPerWorker), foldQuota);
        for (int round = 0; round < rounds; round++)
        {
            (byte[] Key, byte[]? Value)[] changes = new (byte[], byte[]?)[changesPerRound];
            for (int index = 0; index < changes.Length; index++)
                changes[index] = (entries[random.Next(entries.Length)].Key, random.Next(3) == 0 ? null : Value((byte)random.Next(1, 256)));
            tree.Apply(changes);
        }
    }

    // Keys differing only at bit 270 or 271 meet in the group at depth 270, whose slots end with a 272-bit key.
    // A zero worker minimum fans out every frame, so that group's slots and covers are read up to the key end.
    [Test]
    public void Keys_differing_in_the_last_group_match_reference_when_every_frame_fans_out([Values(270, 271)] int differingBit)
    {
        byte[] left = ZoneKey("00A5");
        byte[] right = (byte[])left.Clone();
        right[differingBit >> 3] |= (byte)(0x80 >> (differingBit & 7));
        using DifferentialTree tree = new(PbtTreeHarness.FanOut(0), PbtTreeHarness.FoldQuota());
        tree.Apply([(left, Value(1)), (right, Value(2)), (ZoneKey("005A"), Value(3))]);
        tree.Apply([(left, Value(4)), (right, Value(5))]);
        tree.Apply([(left, Value(6)), (right, null)]);
        tree.Apply([(left, Value(7)), (right, Value(8))]);
    }

    // 20000 keys per zone fan out at depth 8 and, with an 8-operation worker minimum, again below it, so the join
    // and failure paths cover nested workers. A quota of three has the spare worker the root slot fan-out is gated on.
    // The injected failure sits at depth 12 and deeper, which the 64-key tree reaches.
    [Test]
    public void Root_slot_workers_write_disjoint_groups_and_join_before_returning([Values] bool failWorker, [Values(64, 20000)] int keysPerZone)
    {
        FoldFanOut fanOut = PbtTreeHarness.FanOut(8);
        const int foldConcurrency = 3;
        (byte[] Key, byte[]? Value)[] initial = RandomZoneEntries(new Random(keysPerZone), keysPerZone);
        using CoordinatedStore store = new();
        using PbtTreeHarness sequential = new();
        ConcurrencyController foldQuota = new(foldConcurrency);
        ValueHash256 root = store.Fold(default, initial, foldQuota, fanOut);
        sequential.ApplyBatch(initial);
        string[] initialRecords = store.Inner.PhysicalRecords();
        (byte[] Key, byte[]? Value)[] changes = Changes(initial);
        using PbtPartitionBatches prepared = PreparePartitions(changes);
        store.Coordinate = true;
        store.FailWorker = failWorker;
        store.Writes = 0;
        if (failWorker)
        {
            Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, (root, 0), prepared, foldQuota, fanOut, null));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(store.Writes, Is.GreaterThan(0), "partial writes belong to the caller on failure");
                Assert.That(store.Inner.PhysicalRecords(), Is.Not.EqualTo(initialRecords));
                Assert.That(store.ArrivedWorkers, Is.EqualTo(2));
                Assert.That(store.ActiveReads, Is.Zero, "all workers joined before failure returns");
                Assert.That(AvailableWorkers(foldQuota), Is.EqualTo(foldConcurrency - 1), "failed folds return their quota");
            }
            Assert.That(store.DuplicateWrites, Is.False, "each group has one owner");
            return;
        }
        ValueHash256 result = TrieUpdater.UpdateRoot(store, (root, 0), prepared, foldQuota, fanOut, null).Hash;
        sequential.ApplyBatch(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.ArrivedWorkers, Is.EqualTo(2), "both root slot folds reached the barrier concurrently");
            Assert.That(result, Is.EqualTo(sequential.RootHash));
            Assert.That(store.Inner.PhysicalRecords(), Is.EqualTo(sequential.PhysicalPayloads.PhysicalRecords()));
            Assert.That(store.ActiveReads, Is.Zero);
            Assert.That(store.DuplicateWrites, Is.False, "each group has one owner");
            Assert.That(AvailableWorkers(foldQuota), Is.EqualTo(foldConcurrency - 1), "completed folds return their quota");
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
    public void Path_only_groups_preserve_roots_across_mutations(
        [Values] bool splitEveryFrame, [Values] bool compressed)
    {
        using GroupRecordingStore store = new();
        EipReferenceTree oracle = new();
        ValueHash256 root = default;
        (byte[] Key, byte[]? Value)[] entries = ZoneEntries(3, compressed);
        int nonRootReads = 0;
        int nonRootWrites = 0;
        ApplyAndCheck(entries);
        Assert.That(store.Reads, Is.Empty, "building from an empty tree reads no group");
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
        }

        void ApplyAndCheck((byte[] Key, byte[]? Value)[] changes)
        {
            store.Reads.Clear();
            store.Writes.Clear();
            root = store.Fold(root, changes, SplitFanOut(splitEveryFrame));

            foreach (PbtStorageNodePath path in store.Reads)
                if (path.BitDepth != 0) nonRootReads++;
            oracle.Apply(changes);
            IReadOnlyList<PbtStorageNodePath> storedGroups = store.StoredGroups;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                foreach (PbtStorageNodePath path in store.Writes)
                {
                    Assert.That(storedGroups.Contains(path), Is.True, $"physical group at {path}");
                    if (path.BitDepth != 0) nonRootWrites++;
                }
            }
        }
    }

    [Test]
    public void Log_reference_folds_bypass_snapshot_probes_across_mutations(
        [Values] bool splitEveryFrame, [Values] bool compressed)
    {
        using LoggedGroupStore store = new();
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] entries = ZoneEntries(3, compressed);
        Apply(entries);
        Apply(Changes(entries));
        Apply(entries);
        Apply([(entries[0].Key, Value(0xA5))]);
        Apply(entries.Where((_, index) => index % 64 != 1).Select(static entry => (entry.Key, (byte[]?)null)).ToArray());
        Apply(entries);
        Apply(entries.Select(static entry => (entry.Key, (byte[]?)null)).ToArray());
        Assert.That(store.RootPointer == 0, Is.True);
        Apply([entries[0]]);
        Apply([(entries[0].Key, null)]);

        void Apply((byte[] Key, byte[]? Value)[] writes) => AssertLoggedFold(store, expected, writes, SplitFanOut(splitEveryFrame));
    }

    [TestCase("000100", "000200")]
    [TestCase("01", "0100")]
    [TestCase("0020", "0040")]
    public void Log_reference_folds_forward_descendants_through_created_and_removed_groups(string divergingKey, string absentKey)
    {
        using LoggedGroupStore store = new();
        using PbtTreeHarness expected = new();
        Apply((ZoneKey("000000"), Value(1)), (ZoneKey("000001"), Value(2)), (ZoneKey("000002"), Value(4)));
        string[] initial = store.Payloads().PhysicalRecords();
        Apply((ZoneKey(absentKey), null));
        Apply((ZoneKey(divergingKey), Value(3)));
        Apply((ZoneKey("000010"), Value(5)));
        Apply((ZoneKey(divergingKey), null));
        Apply((ZoneKey(divergingKey), Value(3)));
        Apply((ZoneKey(divergingKey), null), (ZoneKey("000010"), null));
        Assert.That(store.Payloads().PhysicalRecords(), Is.EqualTo(initial));

        void Apply(params (byte[] Key, byte[]? Value)[] writes) => AssertLoggedFold(store, expected, writes, SplitFanOut(true));
    }

    private static void AssertLoggedFold(LoggedGroupStore store, PbtTreeHarness expected, (byte[] Key, byte[]? Value)[] writes, FoldFanOut fanOut)
    {
        PbtStorageNodePath[] previous = store.TreePayloads().Select(static group => group.Key).ToArray();
        using PbtGroupRecordBufferPin pin = store.Buffer.PinHead();
        ValueHash256 actual = store.Fold(writes, fanOut);
        expected.ApplyBatch(writes);
        HashSet<PbtStorageNodePath> current = expected.PhysicalPayloads.Select(static group => group.Key).ToHashSet();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected.RootHash));
            Assert.That(store.Payloads().PhysicalRecords(), Is.EqualTo(expected.PhysicalPayloads.PhysicalRecords()));
            Assert.That(store.LastFallbackReads, Is.Zero);
            Assert.That(store.StoreReads, Is.EqualTo(store.LastFallbackReads));
            Assert.That(store.TreePayloads().PhysicalRecords(), Is.EqualTo(expected.PhysicalPayloads.PhysicalRecords()), "the walk from the root reaches exactly the tree's groups");
            Assert.That(store.TreePayloads(pin.WriteBoundary).Select(static group => group.Key), Is.EquivalentTo(store.LastWrittenGroups), "a bounded walk yields only the fold's own groups");
            foreach (PbtPhysicalPayload group in expected.PhysicalPayloads)
                Assert.That(store.FindGroup(group.Key), Is.EqualTo(group.Payload.ToArray()));
            foreach (PbtStorageNodePath removed in previous.Where(key => !current.Contains(key)))
                Assert.That(store.FindGroup(removed), Is.Null, "a removed group misses");
        }
    }

    [Test]
    public void Empty_root_after_memory_deletions_does_not_fetch_the_old_persisted_tree()
    {
        using LoggedGroupStore store = new();
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] entries = ZoneEntries(3, compressed: true);
        expected.ApplyBatch(entries);
        store.Seed(expected.RootHash, expected.PhysicalPayloads);
        (byte[] Key, byte[]? Value)[] deletes = entries.Select(static entry => (entry.Key, (byte[]?)null)).ToArray();
        Assert.That(store.Fold(deletes, SplitFanOut(true)), Is.EqualTo(default(ValueHash256)));
        expected.ApplyBatch(deletes);
        AssertLoggedFold(store, expected, [(entries[0].Key, Value(0xA5))], SplitFanOut(true));
    }

    [Test]
    public void Log_reference_folds_use_exact_store_fallback_for_unknown_or_retired_roots([Values] bool retired)
    {
        using LoggedGroupStore store = new();
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] entries = ZoneEntries(3, compressed: true);
        expected.ApplyBatch(entries);
        ulong unavailable = store.Buffer.Append(default, expected.PhysicalPayloads.Single(static group => group.Key.BitDepth == 0).Payload.Span, default);
        store.Seed(expected.RootHash, expected.PhysicalPayloads);
        (byte[] Key, byte[]? Value)[] changes = Changes(entries);
        ValueHash256 actual = store.Fold(changes, SplitFanOut(true), retired ? unavailable : 0);
        expected.ApplyBatch(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected.RootHash));
            Assert.That(store.LastFallbackReads, Is.GreaterThan(0));
            Assert.That(store.StoreReads, Is.EqualTo(store.LastFallbackReads));
            Assert.That(store.Payloads().PhysicalRecords(), Is.EqualTo(expected.PhysicalPayloads.PhysicalRecords()));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void Log_reference_fold_failure_does_not_publish_partial_records(bool splitEveryFrame, bool failAfterWrite)
    {
        using PbtGroupRecordBuffer buffer = new(0, 256, 1);
        using LoggedGroupStore store = new(buffer);
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] entries = ZoneEntries(3, compressed: false);
        expected.ApplyBatch(entries);
        store.Seed(expected.RootHash, expected.PhysicalPayloads);
        string[] initial = store.Payloads().PhysicalRecords();
        ulong initialFrontier = buffer.WriteFrontier();
        int initialPins = buffer.PinCount();
        if (failAfterWrite)
        {
            store.FoldConcurrency = 1;
            store.FailOnWrite = 2;
        }
        else store.FailDescendantReads = true;
        Assert.That(() => store.Fold(Changes(entries), SplitFanOut(splitEveryFrame)),
            Throws.TypeOf(failAfterWrite ? typeof(InvalidOperationException) : typeof(InvalidDataException)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.LastComplete, Is.False);
            Assert.That(store.Payloads().PhysicalRecords(), Is.EqualTo(initial));
            Assert.That(store.RootPointer, Is.Zero);
            if (failAfterWrite) Assert.That(store.LastWriteFrontier, Is.GreaterThan(initialFrontier), "the failure followed actual private group writes");
            store.DiscardUncommittedState();
            Assert.That(buffer.PinCount(), Is.EqualTo(initialPins), "aborting the scope releases its generation pin");
        }
    }

    [Test]
    public async Task Log_reference_forks_resolve_unknown_or_retired_children_against_their_own_view([Values] bool unknownChildren)
    {
        using PbtGroupRecordBuffer buffer = new(0, 256, 1);
        using LoggedGroupStore first = new(buffer);
        using LoggedGroupStore second = new(buffer);
        using PbtTreeHarness firstExpected = new();
        using PbtTreeHarness secondExpected = new();
        (byte[] Key, byte[]? Value)[] firstEntries = ZoneEntries(3, compressed: true);
        (byte[] Key, byte[]? Value)[] secondEntries = firstEntries.Select((entry, index) => (entry.Key, (byte[]?)Value((byte)(0x40 + index % 32)))).ToArray();
        first.Fold(firstEntries, SplitFanOut(true));
        second.Fold(secondEntries, SplitFanOut(true));
        firstExpected.ApplyBatch(firstEntries);
        secondExpected.ApplyBatch(secondEntries);
        Assert.That(buffer.TryLocate(first.RootPointer, out PbtGroupRecord firstParent), Is.True);
        Assert.That(buffer.TryLocate(second.RootPointer, out PbtGroupRecord secondParent), Is.True);
        ulong firstChild = firstParent.GetLink(0);
        ulong secondChild = secondParent.GetLink(0);
        Assert.That(firstChild != 0 && secondChild != 0, Is.True);
        first.RetireDescendants(unknownChildren);
        second.RetireDescendants(unknownChildren);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TryLocate(first.RootPointer, out _), Is.True);
            Assert.That(buffer.TryLocate(second.RootPointer, out _), Is.True);
            Assert.That(buffer.TryLocate(firstChild, out _), Is.False, "the live parent must not require its old child generation to stay resident");
            Assert.That(buffer.TryLocate(secondChild, out _), Is.False);
        }
        (byte[] Key, byte[]? Value)[] firstChanges = Changes(firstEntries);
        (byte[] Key, byte[]? Value)[] secondChanges = [(secondEntries[0].Key, Value(0xE1)), (secondEntries[^1].Key, null)];
        ValueHash256[] roots = await Task.WhenAll(
            Task.Run(() => first.Fold(firstChanges, SplitFanOut(true))),
            Task.Run(() => second.Fold(secondChanges, SplitFanOut(true))));
        firstExpected.ApplyBatch(firstChanges);
        secondExpected.ApplyBatch(secondChanges);
        AssertFork(first, firstExpected, firstEntries, firstChanges, roots[0]);
        AssertFork(second, secondExpected, secondEntries, secondChanges, roots[1]);
        first.Dispose();
        second.Dispose();
        Assert.That(buffer.PinCount(), Is.Zero);

        static void AssertFork(LoggedGroupStore store, PbtTreeHarness expected, (byte[] Key, byte[]? Value)[] entries,
            (byte[] Key, byte[]? Value)[] changes, ValueHash256 root)
        {
            EipReferenceTree pointer = new();
            pointer.Apply(entries);
            pointer.Apply(changes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(pointer.Merkelize()));
                Assert.That(root, Is.EqualTo(expected.RootHash));
                Assert.That(store.Payloads().PhysicalRecords(), Is.EqualTo(expected.PhysicalPayloads.PhysicalRecords()));
                Assert.That(store.LastDirectReads, Is.GreaterThan(0), "the parent itself stayed in the shared buffer");
                Assert.That(store.LastFallbackReads, Is.GreaterThan(0));
                Assert.That(store.StoreReads, Is.EqualTo(store.LastFallbackReads));
            }
        }
    }

    private sealed class LoggedGroupStore : IPbtStore, IDisposable
    {
        private PbtSnapshotContent _content;
        private readonly bool _ownsBuffer;
        private PbtNodeGroupStore? _persisted;
        private ValueHash256 _root;
        private long _storeReads;
        private long _directReads;
        private long _fallbackReads;
        private ulong _inputRoot;
        private readonly List<ulong> _writes = [];
        // Groups the folds published, with None for removed ones, over the persisted groups.
        private readonly Dictionary<PbtStorageNodePath, ulong> _groups = [];

        public LoggedGroupStore(PbtGroupRecordBuffer? buffer = null)
        {
            _ownsBuffer = buffer is null;
            Buffer = buffer ?? new PbtGroupRecordBuffer(0, 4096, 1);
            _content = NewContent();
        }

        public PbtGroupRecordBuffer Buffer { get; }

        private PbtSnapshotContent NewContent()
        {
            PbtSnapshotContent content = new();
            content.BeginNodeGroupWrites(Buffer);
            return content;
        }
        public ulong RootPointer { get; private set; }
        public long LastDirectReads { get; private set; }
        public long LastFallbackReads { get; private set; }
        public long StoreReads => Interlocked.Read(ref _storeReads);
        public bool FailDescendantReads { get; set; }
        /// <summary>The ordinal of the fold's write batch that throws, if any.</summary>
        public int? FailOnWrite { get; set; }
        private int _writeCount;
        public bool LastComplete { get; private set; }
        public ulong LastWriteFrontier { get; private set; }
        public List<PbtStorageNodePath> LastWrittenGroups { get; } = [];
        public int FoldConcurrency { get; set; } = Environment.ProcessorCount;

        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength)
        {
            if (Interlocked.Increment(ref _writeCount) == FailOnWrite) throw new InvalidOperationException("Configured write failure.");
            IPbtGroupRecordWriteBatch batch = Buffer.StartWriteBatch(alignedTotalLength);
            lock (_writes) _writes.Add(batch.BasePointer);
            return batch;
        }

        public bool TryLocate(ulong pointer, out PbtGroupRecord record)
        {
            if (Buffer.TryLocate(pointer, out record))
            {
                Interlocked.Increment(ref _directReads);
                return true;
            }
            Interlocked.Increment(ref _fallbackReads);
            return false;
        }

        public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            Interlocked.Increment(ref _storeReads);
            if (FailDescendantReads && path.BitDepth != 0) throw new InvalidDataException("Configured descendant read failure.");
            return PbtNodeGroupStore.TryLoadPayload(_persisted?.GetNodeGroup(path), action, arg);
        }

        public ValueHash256 Fold((byte[] Key, byte[]? Value)[] writes, FoldFanOut fanOut, ulong? rootOverride = null)
        {
            _inputRoot = rootOverride ?? RootPointer;
            _writes.Clear();
            _writeCount = 0;
            _directReads = _fallbackReads = 0;
            LastComplete = false;
            using PbtPartitionBatches batches = PreparePartitions(writes);
            _storeReads = 0;
            ValueHash256 root;
            try
            {
                root = TrieUpdater.UpdateRoot(this, (_root, _inputRoot), batches, new ConcurrencyController(FoldConcurrency), fanOut, null).Hash;
                LastComplete = true;
            }
            finally
            {
                LastWriteFrontier = Buffer.WriteFrontier();
                LastDirectReads = _directReads;
                LastFallbackReads = _fallbackReads;
                LastWrittenGroups.Clear();
                if (LastComplete)
                {
                    RootPointer = 0;
                    foreach (ulong pointer in _writes)
                    {
                        Buffer.TryLocate(pointer, out PbtGroupRecord record);
                        PbtStorageNodePath path = record.Path;
                        _groups[path] = pointer;
                        if (path.BitDepth == 0) RootPointer = pointer;
                        LastWrittenGroups.Add(path);
                    }
                }
                _writes.Clear();
            }
            Assert.That(LastComplete, Is.True);
            return _root = root;
        }

        public void DiscardUncommittedState()
        {
            _content.Dispose();
            _content = NewContent();
            _groups.Clear();
        }

        public void Seed(ValueHash256 root, IReadOnlyList<PbtPhysicalPayload> groups)
        {
            DiscardUncommittedState();
            _persisted?.Dispose();
            _persisted = PbtNodeGroupStore.FromPhysicalPayloads(groups);
            _root = root;
            RootPointer = 0;
        }

        public void RetireDescendants(bool unknownLinks)
        {
            PbtNodeGroupStore persisted = PbtNodeGroupStore.FromPhysicalPayloads(Payloads());
            _persisted?.Dispose();
            _persisted = persisted;
            Assert.That(Buffer.TryLocate(RootPointer, out PbtGroupRecord record), Is.True);
            using PbtGroupRecordBufferPin parent = Buffer.PinHead();
            RootPointer = Buffer.Append(default, record.Payload.Span, unknownLinks ? default : record.Links);
            DiscardUncommittedState();
            _groups[default] = RootPointer;
        }

        /// <summary>The groups reached by walking the buffer from <see cref="RootPointer"/>, pruned below <paramref name="lowerBound"/>.</summary>
        public IReadOnlyList<PbtPhysicalPayload> TreePayloads(ulong lowerBound = default)
        {
            List<PbtPhysicalPayload> payloads = [];
            foreach (PbtGroupRecord record in PbtNodeGroupKey.InKeyOrder(Buffer, RootPointer, lowerBound)) payloads.Add(new PbtPhysicalPayload(record.Path, record.Payload.Span));
            return payloads;
        }

        /// <summary>The payload of the group at <paramref name="path"/> in the tree under <see cref="RootPointer"/>, or null when the walk misses.</summary>
        public byte[]? FindGroup(PbtStorageNodePath path) =>
            Buffer.TryFindGroup(RootPointer, path, out PbtGroupRecord record) ? record.Payload.ToArray() : null;

        public IReadOnlyList<PbtPhysicalPayload> Payloads()
        {
            Dictionary<PbtStorageNodePath, PbtPhysicalPayload> groups = [];
            if (_persisted is not null)
                foreach (PbtPhysicalPayload group in _persisted.ExportPhysicalPayloads()) groups[group.Key] = group;
            foreach ((PbtStorageNodePath key, ulong pointer) in _groups)
            {
                if (pointer == 0) { groups.Remove(key); continue; }
                Assert.That(Buffer.TryLocate(pointer, out PbtGroupRecord record), Is.True);
                groups[key] = new PbtPhysicalPayload(key, record.Payload.Span);
            }
            using PbtNodeGroupStore tree = PbtNodeGroupStore.FromPhysicalPayloads([.. groups.Values]);
            tree.RetainTreeGroups(_root == default);
            return tree.ExportPhysicalPayloads();
        }

        public void Dispose()
        {
            _content.Dispose();
            _persisted?.Dispose();
            if (_ownsBuffer) Buffer.Dispose();
        }
    }

    private sealed class GroupRecordingStore : IPbtStore, IDisposable
    {
        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new PbtRecordCallbackWriteBatch(SetNodeGroup, alignedTotalLength);
        public bool TryLocate(ulong pointer, out PbtGroupRecord record)
        {
            record = default;
            return false;
        }
        public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            PbtNodeGroupStore.TryLoadPayload(GetNodeGroup(path), action, arg);

        private readonly PbtNodeGroupStore _store = new();
        internal List<PbtStorageNodePath> Reads { get; } = [];
        internal List<PbtStorageNodePath> Writes { get; } = [];
        internal IReadOnlyList<PbtStorageNodePath> StoredGroups => _store.EnumerateNodeGroupKeys();

        public byte[]? GetNodeGroup(scoped in PbtTraversalPath groupKey)
        {
            lock (Reads) Reads.Add(groupKey.ToPath<PbtStorageNodePath>());
            return _store.GetNodeGroup(groupKey);
        }

        private void SetNodeGroup(PbtGroupRecord record)
        {
            lock (Writes) Writes.Add(record.Path);
            _store.SetNodeGroup(record);
        }

        public void Dispose() => _store.Dispose();
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

    /// <summary>Keys spread uniformly below each zone byte, so every group level fans out into all four buckets.</summary>
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

    private sealed class CoordinatedStore : IPbtStore, IDisposable
    {
        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new PbtRecordCallbackWriteBatch(SetNodeGroup, alignedTotalLength);
        public bool TryLocate(ulong pointer, out PbtGroupRecord record)
        {
            record = default;
            return false;
        }
        public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            PbtNodeGroupStore.TryLoadPayload(GetNodeGroup(path), action, arg);

        private readonly Barrier _barrier = new(2);
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

        public byte[]? GetNodeGroup(scoped in PbtTraversalPath groupKey)
        {
            Interlocked.Increment(ref _activeReads);
            try
            {
                // The account and storage zone groups lie under different root slots; the code zone's shares the account's.
                if (Coordinate && groupKey.BitDepth == 8 && groupKey.ToPath<PbtStorageNodePath>().GetByte(0) != Eip8297KeyDerivation.CodeZone)
                {
                    Interlocked.Increment(ref _arrivedWorkers);
                    if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("The independent root slot folds did not overlap.");
                }
                if (FailWorker && groupKey.BitDepth >= 12 && groupKey.ToPath<PbtStorageNodePath>().GetByte(0) == 0x01 && groupKey.ToPath<PbtStorageNodePath>().GetByte(1) >= 0x10)
                    throw new InvalidDataException("Injected worker failure after folding the first slot.");
                return Inner.GetNodeGroup(groupKey);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        private void SetNodeGroup(PbtGroupRecord record)
        {
            lock (_writtenGroups)
            {
                if (Writes == 0) _writtenGroups.Clear();
                DuplicateWrites |= !_writtenGroups.Add(record.Path);
                Writes++;
                Inner.SetNodeGroup(record);
            }
        }

        public void Dispose()
        {
            _barrier.Dispose();
            Inner.Dispose();
        }
    }

    /// <summary>Counts the threads reading once observed; coordinating, the first two hold their first bucket-group read until both arrive.</summary>
    /// <remarks>Only reads below the zone frame take part, so the calling thread's reads before the fan-out cannot block on a worker that never starts.</remarks>
    private sealed class BucketWorkerStore : IPbtStore, IDisposable
    {
        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => Inner.StartWriteBatch(alignedTotalLength);
        public bool TryLocate(ulong pointer, out PbtGroupRecord record)
        {
            record = default;
            return false;
        }
        public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            PbtNodeGroupStore.TryLoadPayload(GetNodeGroup(path), action, arg);

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

        public byte[]? GetNodeGroup(scoped in PbtTraversalPath groupKey)
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
            return Inner.GetNodeGroup(groupKey);
        }

        public void Dispose()
        {
            _barrier.Dispose();
            Inner.Dispose();
        }
    }

    /// <summary>A single-operation minimum splits every frame with two touched slots, so even small batches fold in parallel.</summary>
    private static FoldFanOut SplitFanOut(bool splitEveryFrame) => splitEveryFrame ? PbtTreeHarness.FanOut(1) : PbtTreeHarness.DefaultFanOut;

    /// <summary>Keys cycling through the account, code and storage zones, each led by its index byte.</summary>
    private static (byte[] Key, byte[]? Value)[] Entries(int seed, int count)
    {
        Random random = new(seed);
        byte[] zones = [0x00, 0x01, 0xFF];
        (byte[] Key, byte[]? Value)[] entries = new (byte[], byte[]?)[count];
        for (int index = 0; index < entries.Length; index++)
        {
            byte zone = zones[index % zones.Length];
            byte[] key = new byte[zone == 0xFF ? PbtStoragePath.KeyLength : PbtPath.KeyLength];
            random.NextBytes(key.AsSpan(2));
            key[0] = zone;
            key[1] = (byte)index;
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
}
