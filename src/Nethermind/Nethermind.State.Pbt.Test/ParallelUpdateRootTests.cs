// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Core.Threading;
using Nethermind.Pbt;
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
            root = target.Fold(root, writes, SplitFanOut(splitEveryFrame), null);
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
        int[] runEnds = new int[PbtFourLevelGroupGeometry.BoundarySlots];
        int runCount = (FoldFanOut.Default with { MinOperationsPerWorker = minOperations }).PlanBucketRuns(counts, descendantBytes, runEnds);
        Assert.That(runEnds.AsSpan(0, runCount).ToArray(), Is.EqualTo(expectedRunEnds));
    }

    [TestCase(0, FoldFanOut.DefaultMinOperationsPerWorker)]
    [TestCase(FoldFanOut.DefaultLargeSubtreeBytes - 1, FoldFanOut.DefaultMinOperationsPerWorker)]
    [TestCase(FoldFanOut.DefaultLargeSubtreeBytes, FoldFanOut.DefaultLargeSubtreeMinOperationsPerWorker)]
    public void Worker_minimum_drops_from_the_large_subtree_size(long descendantBytes, int expectedMinimum) =>
        Assert.That(PbtTreeHarness.DefaultFanOut.MinOperationsFor(descendantBytes), Is.EqualTo(expectedMinimum));

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
        ValueHash256 root = store.Fold(default, initial, foldQuota, PbtTreeHarness.DefaultFanOut, null);
        sequential.ApplyBatch(initial);
        store.Observe(coordinate: expectParallel);
        root = store.Fold(root, changes, foldQuota, PbtTreeHarness.DefaultFanOut, null);
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
    // byte-identical group payloads. 4096 changes are ~85 per depth-8 bucket, so a minimum of 64 gives every bucket
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

    // 20000 keys per zone fan out at depth 8 and, with an 8-operation worker minimum, again at depth 12, so the join
    // and failure paths cover nested workers. A quota of three has the spare worker the root slot fan-out is gated on.
    // The injected failure sits at depth 12, the deepest the 64-key tree reaches.
    [Test]
    public void Root_slot_workers_write_disjoint_groups_and_join_before_returning([Values] bool failWorker, [Values(64, 20000)] int keysPerZone)
    {
        FoldFanOut fanOut = PbtTreeHarness.FanOut(8);
        const int foldConcurrency = 3;
        (byte[] Key, byte[]? Value)[] initial = RandomZoneEntries(new Random(keysPerZone), keysPerZone);
        using CoordinatedStore store = new();
        using PbtTreeHarness sequential = new();
        ConcurrencyController foldQuota = new(foldConcurrency);
        ValueHash256 root = store.Fold(default, initial, foldQuota, fanOut, null);
        sequential.ApplyBatch(initial);
        string[] initialRecords = store.Inner.PhysicalRecords();
        (byte[] Key, byte[]? Value)[] changes = Changes(initial);
        using PbtPartitionBatches prepared = PreparePartitions(changes);
        store.Coordinate = true;
        store.FailWorker = failWorker;
        store.Writes = 0;
        if (failWorker)
        {
            Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, prepared, foldQuota, fanOut, null));
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
        ValueHash256 result = TrieUpdater.UpdateRoot(store, root, prepared, foldQuota, fanOut, null);
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
    public void Group_hashes_match_independent_boundary_subtrees_across_mutations(
        [Values] bool splitEveryFrame, [Values] bool compressed)
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
        Assert.That(store.Reads, Has.Count.EqualTo(1), "building from empty reads only the root group");
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
            root = store.Fold(root, changes, SplitFanOut(splitEveryFrame), null);

            foreach ((PbtStorageNodePath path, ValueHash256 hash) in store.Reads)
            {
                Assert.That(hash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize(path)), $"old subtree read at {path}");
                if (path.BitDepth != 0) nonRootReads++;
            }
            oracle.Apply(changes);
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

    private sealed class HashRecordingStore : IPbtStore, IPbtNodeGroupSink, IDisposable
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

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

    private sealed class CoordinatedStore : IPbtStore, IPbtNodeGroupSink, IDisposable
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

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

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash)
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

    /// <summary>Counts the threads reading once observed; coordinating, the first two hold their first bucket-group read until both arrive.</summary>
    /// <remarks>Only reads below the zone frame take part, so the calling thread's reads before the fan-out cannot block on a worker that never starts.</remarks>
    private sealed class BucketWorkerStore : IPbtStore, IPbtNodeGroupSink, IDisposable
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

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
