// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleWarmerTests
{
    private const int ChurnMilliseconds = 2_000;
    private static readonly TimeSpan BailOutTimeout = TimeSpan.FromSeconds(30);

    private ResourcePool _pool = null!;
    private INodeRecordBuffer _buffer = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new ResourcePool(new FlatDbConfig());
        _buffer = new ForwardingNodeRecordBuffer(FlatTestHelpers.NewTestBuffer());
    }

    [TearDown]
    public void TearDown() => _buffer.Dispose();

    [Test]
    public void Buffer_contract_preserves_batch_offsets_hashes_and_root_lookup([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        using ForwardingNodeRecordBuffer buffer = new(FlatTestHelpers.NewTestBuffer());
        using INodeRecordBufferPin retain = buffer.Pin();
        byte[] prefix = FlatTestHelpers.LeafRlp(0x80);
        buffer.AppendRecordFromRlp(address, TreePath.Empty, ValueKeccak.Compute(prefix), prefix);
        int completedBefore = buffer.CompletedRecords;
        int batchesBefore = buffer.CompletedWrites;
        ValueHash256[] keys = new ValueHash256[64];
        byte[] value = new byte[40];
        value.AsSpan().Fill(0x42);
        using WarmedNodeLog warmed = new(1);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), warmed, address);
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = ValueKeccak.Compute([(byte)i]);
            entries.Add(new PatriciaTree.BulkSetEntry(keys[i], value));
        }

        TrieRoot root = BufferBulkSet.BulkSet(buffer, source, address, TrieRoot.Empty, entries, canParallelize: false);
        (TreePath Path, ulong Pointer, NodeRecord Record)[] nodes = buffer.EnumerateNodes(root.Pointer).ToArray();
        BufferWarmUp.WarmUp(new WarmedNodeRecordBuffer(buffer, warmed), warmed,
            (in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, BufferWarmUp.WarmUpRead> action, BufferWarmUp.WarmUpRead read) =>
                throw new InvalidOperationException("Unexpected persistence read"), address, root, keys[0]);
        PatriciaTree expected = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach (ValueHash256 key in keys) expected.Set(key.Bytes, value);
        buffer.SetRoot(address, root.Hash, root.Pointer);
        expected.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.ResolveRoot(address, root.Hash), Is.EqualTo(root));
            Assert.That(buffer.CompletedRecords - completedBefore, Is.EqualTo(nodes.Length));
            Assert.That(buffer.CompletedWrites - batchesBefore, Is.LessThan(nodes.Length), "a batch contains multiple relative record offsets");
            Assert.That(nodes.All(node => node.Record.Hash == ValueKeccak.Compute(node.Record.Rlp)), Is.True);
            Assert.That(root.Hash, Is.EqualTo(expected.RootHash.ValueHash256));
            Assert.That(retain.Buffer, Is.SameAs(buffer));
        }
    }

    private SnapshotBundle NewBundle(NodeReader reader) =>
        new(FlatTestHelpers.MakeBundle(_pool, reader), _buffer, _pool, ResourcePool.Usage.MainBlockProcessing);

    /// <summary>A trie whose root is the leaf <paramref name="rootRlp"/>, as its root and the persisted nodes serving it.</summary>
    private static (TrieRoot Root, Dictionary<TreePath, byte[]> Nodes) LeafTrie(byte[] rootRlp) =>
        (new TrieRoot(ValueKeccak.Compute(rootRlp), 0), new Dictionary<TreePath, byte[]> { [TreePath.Empty] = rootRlp });

    // Committed snapshot nodes stay in the retained buffer; only persisted nodes enter the warmed source.
    [Test]
    public void Warm_up_reads_persistence_only_and_ignores_the_bundle_snapshots([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] committedRlp = FlatTestHelpers.LeafRlp(0x82);
        (TrieRoot persistedRoot, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        TrieRoot committedRoot = new(ValueKeccak.Compute(committedRlp), 0);
        using SnapshotBundle bundle = NewBundle(new NodeReader(persisted));

        INodeRecordBufferPin retain = _buffer.Pin();
        committedRoot = new TrieRoot(committedRoot.Hash, PublishNode(bundle, address, TreePath.Empty, committedRlp));
        bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), retain, returnSnapshot: false);

        bundle.WarmUp(address, committedRoot, ValueKeccak.Zero);
        bundle.WarmUp(address, persistedRoot, ValueKeccak.Zero);
        bool committedLoadable = bundle.TryLoadTrieNodeRecord(address, TreePath.Empty, committedRoot.Hash, out _);

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(new StateId(1, TestItem.KeccakA), new StateId(2, TestItem.KeccakA), null);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retired!.WarmedNodeLog.TryGet(address, TreePath.Empty, persistedRoot.Hash, out _, out _), Is.True, "persisted node warmed");
                Assert.That(retired.WarmedNodeLog.TryGet(address, TreePath.Empty, committedRoot.Hash, out _, out _), Is.False, "committed node warmed");
                Assert.That(committedLoadable, Is.False, "the source must skip committed snapshot nodes");
                Assert.That(_buffer.GetRecord(committedRoot.Pointer).Rlp.ToArray(), Is.EqualTo(committedRlp), "committed node remains in the retained buffer");
            }
        }
        finally
        {
            snapshot?.Dispose();
            retired?.ReleaseLease();
        }
    }

    [Test, NonParallelizable]
    public void Normal_warmed_cache_metrics_count_each_lookup_once([Values] bool storage, [Values] bool warmUp)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        (TrieRoot root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        (long Hits, long Misses, long IdleHits, long IdleMisses) before = WarmedCacheCounts();

        if (warmUp)
        {
            bundle.WarmUp(address, root, ValueKeccak.Zero);
            bundle.WarmUp(address, root, ValueKeccak.Zero);
        }
        else
        {
            Assert.That(bundle.TryLoadTrieNodeRecord(address, TreePath.Empty, root.Hash, out _), Is.True);
            Assert.That(bundle.TryLoadTrieNodeRecord(address, TreePath.Empty, root.Hash, out _), Is.True);
        }

        Assert.That(reader.Reads, Is.EqualTo(1));
        AssertWarmedCacheCounts(before, hits: 1, misses: 1);
    }

    [Test, NonParallelizable]
    public void Idle_warmed_cache_metrics_count_each_lookup_once()
    {
        Hash256 address = TestItem.KeccakC;
        byte[] rlp = FlatTestHelpers.LeafRlp(0x80);
        (TrieRoot root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(rlp);
        NodeReader reader = new(persisted);
        using ReadOnlySnapshotBundle readOnly = FlatTestHelpers.MakeBundle(_pool, reader);
        using SnapshotPooledList snapshots = new(1);
        using SnapshotBundle.BackgroundTrieSource source = new(readOnly, _buffer, snapshots, address);
        (long Hits, long Misses, long IdleHits, long IdleMisses) before = WarmedCacheCounts();

        Assert.That(source.TryLoad(TreePath.Empty, root.Hash, out _), Is.True);
        Assert.That(source.TryLoad(TreePath.Empty, root.Hash, out NodeRecord record), Is.True);
        Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp));

        Assert.That(reader.Reads, Is.EqualTo(1));
        AssertWarmedCacheCounts(before, idleHits: 1, idleMisses: 1);
    }

    [Test, NonParallelizable]
    public void Warm_up_metrics_count_transient_pointer_hits_but_not_main_buffer_hits([Values] bool inBuffer)
    {
        byte[] rlp = FlatTestHelpers.LeafRlp(0x80);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        using WarmedNodeLog warmed = new(1);
        ulong pointer;
        if (inBuffer) pointer = _buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp);
        else Assert.That(warmed.TryAppend(null, TreePath.Empty, hash, rlp, out _, out pointer), Is.True);
        (long Hits, long Misses, long IdleHits, long IdleMisses) before = WarmedCacheCounts();

        BufferWarmUp.WarmUp(new WarmedNodeRecordBuffer(_buffer, warmed), warmed, (in TreePath path, in ValueHash256 nodeHash, ReadOnlySpanAction<byte, BufferWarmUp.WarmUpRead> action, BufferWarmUp.WarmUpRead read) => throw new InvalidOperationException("Unexpected persistence read"),
            null, new TrieRoot(hash, pointer), ValueKeccak.Zero);

        AssertWarmedCacheCounts(before, hits: inBuffer ? 0 : 1);
    }

    private static (long Hits, long Misses, long IdleHits, long IdleMisses) WarmedCacheCounts() =>
        (Metrics.WarmedNodeCacheHits, Metrics.WarmedNodeCacheMisses,
            Metrics.IdleWarmedNodeCacheHits, Metrics.IdleWarmedNodeCacheMisses);

    private static void AssertWarmedCacheCounts((long Hits, long Misses, long IdleHits, long IdleMisses) before,
        long hits = 0, long misses = 0, long idleHits = 0, long idleMisses = 0)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.WarmedNodeCacheHits - before.Hits, Is.EqualTo(hits));
            Assert.That(Metrics.WarmedNodeCacheMisses - before.Misses, Is.EqualTo(misses));
            Assert.That(Metrics.IdleWarmedNodeCacheHits - before.IdleHits, Is.EqualTo(idleHits));
            Assert.That(Metrics.IdleWarmedNodeCacheMisses - before.IdleMisses, Is.EqualTo(idleMisses));
        }
    }

    [Test]
    public void Returned_transient_resource_clears_warmed_nodes([Values] bool storage)
    {
        ResourcePool.Usage usage = ResourcePool.Usage.MainBlockProcessing;
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] rlp = FlatTestHelpers.LeafRlp(0x80);
        ValueHash256 hash = ValueKeccak.Compute(rlp);

        TransientResource rented = _pool.GetCachedResource(usage);
        try
        {
            Assert.That(rented.WarmedNodeLog.TryAppend(address, TreePath.Empty, hash, rlp, out _, out _), Is.True);
        }
        finally
        {
            rented.ReleaseLease();
        }

        TransientResource recycled = _pool.GetCachedResource(usage);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recycled, Is.SameAs(rented));
                Assert.That(recycled.WarmedNodeLog.TryGet(address, TreePath.Empty, hash, out _, out _), Is.False);
            }
        }
        finally
        {
            recycled.ReleaseLease();
        }
    }

    [Test]
    public void Warmed_only_nodes_are_read_again_after_retirement([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        (TrieRoot root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        bundle.WarmUp(address, root, ValueKeccak.Zero);
        Assert.That(reader.Reads, Is.EqualTo(1));

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), null);
        snapshot?.Dispose();
        retired!.ReleaseLease();
        bundle.WarmUp(address, root, ValueKeccak.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Reads, Is.EqualTo(2), "retirement no longer promotes warmed nodes into a shared cache");
            Assert.That(bundle.TryLoadTrieNodeRecord(address, TreePath.Empty, root.Hash, out _), Is.True);
            Assert.That(reader.Reads, Is.EqualTo(2), "the replacement transient still reuses its own warmed records");
        }
    }

    [Test]
    public void Retirement_releases_the_owner_but_recycling_waits_for_an_in_flight_warm_up()
    {
        (TrieRoot root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        using ManualResetEventSlim readEntered = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        using SnapshotBundle bundle = NewBundle(new NodeReader(persisted, readEntered, releaseRead));

        Task warmUp = Task.Run(() => bundle.WarmUp(null, root, ValueKeccak.Zero));
        Assert.That(readEntered.Wait(BailOutTimeout), Is.True, "the warm-up did not reach persistence");

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), null);
        TransientResource? probe = null;
        TransientResource? recycled = null;
        bool ownerReleased = false;
        try
        {
            retired!.ReleaseLease();
            ownerReleased = true;
            probe = _pool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
            Assert.That(probe, Is.Not.SameAs(retired), "the active warmer still owns the retired resource");
            releaseRead.Set();
            Assert.That(warmUp.Wait(BailOutTimeout), Is.True);
            recycled = _pool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recycled, Is.SameAs(retired), "the final warmer release returns the resource exactly once");
                Assert.That(recycled.WarmedNodeLog.Count, Is.Zero, "reset happens after the warmer finishes writing");
            }
        }
        finally
        {
            releaseRead.Set();
            snapshot?.Dispose();
            if (!ownerReleased) retired?.ReleaseLease();
            probe?.ReleaseLease();
            recycled?.ReleaseLease();
        }
    }

    private static readonly (string PersistedRlpHex, bool RequestPersistedHash)[] MismatchingOrInvalidNodes =
    [
        (FlatTestHelpers.LeafRlpA, false),
        ("c28001", true),
        ("f8", true),
    ];

    // The persistence read is path-keyed, so it can return another node or invalid bytes; neither may be warmed.
    [Test]
    public void Warm_up_does_not_warm_a_mismatching_or_invalid_node(
        [Values] bool storage,
        [ValueSource(nameof(MismatchingOrInvalidNodes))] (string PersistedRlpHex, bool RequestPersistedHash) persisted)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] persistedRlp = Bytes.FromHexString(persisted.PersistedRlpHex);
        TrieRoot root = new(persisted.RequestPersistedHash ? ValueKeccak.Compute(persistedRlp) : TestItem.KeccakB.ValueHash256, 0);
        NodeReader reader = new(new Dictionary<TreePath, byte[]> { [TreePath.Empty] = persistedRlp });
        using SnapshotBundle bundle = NewBundle(reader);

        bundle.WarmUp(address, root, ValueKeccak.Zero);

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), null);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.Reads, Is.EqualTo(1));
                Assert.That(retired!.WarmedNodeLog.Count, Is.Zero);
            }
        }
        finally
        {
            snapshot?.Dispose();
            retired?.ReleaseLease();
        }
    }

    // Dispose releases the transient back to the pool while warm-ups may still be queued. One that arrives after that must
    // return without reading anything rather than touch the recycled resource, or spin waiting for a transient that will never
    // be replaced, which is what the bounded wait here asserts.
    [Test]
    public void Warm_up_on_a_disposed_bundle_is_a_no_op()
    {
        (TrieRoot root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        SnapshotBundle bundle = NewBundle(reader);

        Assert.That(bundle.TryLeaseReadOnlyBundle(), Is.True);
        try
        {
            bundle.Dispose();

            Task<bool> warmUp = Task.Run(() =>
            {
                bundle.WarmUp(null, root, ValueKeccak.Zero);
                return bundle.ShouldQueuePrewarm(TestItem.AddressA);
            });

            Assert.That(warmUp.Wait(BailOutTimeout), Is.True, "the warm-up did not return on a disposed bundle");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.Reads, Is.Zero);
                Assert.That(warmUp.Result, Is.False);
            }
        }
        finally
        {
            bundle.ReleaseReadOnlyBundleLease();
        }
    }

    private sealed record ChurnEpoch(SnapshotBundle Bundle, TrieRoot Root);

    // Regression for the warmer recycle-under-reader race. Warm-ups hold only a ReadOnlySnapshotBundle lease (as the real
    // warmer job does) while the owner drives both recycle paths: CollectAndApplySnapshot, which swaps the transient in place
    // and retires the old one while the bundle stays published, and Dispose, which releases the transient outright. Each epoch
    // persists a distinct root, so a warm-up writing into another epoch's recycled transient shows up as a foreign record when
    // the owner retires its transient. The prewarm dedupe bloom lives on the same resource, so it is exercised here too:
    // unpinned, its BloomFilter can be freed by a pool overflow mid-read.
    [Test]
    public void Warm_ups_survive_owner_churn_without_foreign_records()
    {
        int epochs = 0;
        ChurnEpoch NewEpoch()
        {
            (TrieRoot root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(BitConverter.GetBytes(++epochs)));
            return new ChurnEpoch(NewBundle(new NodeReader(persisted)), root);
        }

        long warmUps = 0;
        long queuedPrewarms = 0;
        long slotCounter = 0;
        Exception? readerException = null;
        bool stop = false;
        ChurnEpoch published = NewEpoch();

        Task[] readers = new Task[Math.Max(2, Environment.ProcessorCount - 2)];
        for (int t = 0; t < readers.Length; t++)
        {
            readers[t] = Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        ChurnEpoch epoch = Volatile.Read(ref published);
                        if (!epoch.Bundle.TryLeaseReadOnlyBundle()) continue;
                        try
                        {
                            epoch.Bundle.WarmUp(null, epoch.Root, ValueKeccak.Zero);
                            Interlocked.Increment(ref warmUps);

                            UInt256 slot = (UInt256)Interlocked.Increment(ref slotCounter);
                            if (epoch.Bundle.ShouldQueuePrewarm(TestItem.AddressA, slot)) Interlocked.Increment(ref queuedPrewarms);
                        }
                        finally
                        {
                            epoch.Bundle.ReleaseReadOnlyBundleLease();
                        }
                    }
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref readerException, e, null);
                    Volatile.Write(ref stop, true);
                }
            });
        }

        Stopwatch sw = Stopwatch.StartNew();
        int retirements = 0;
        int disposals = 0;
        int retiredRecords = 0;
        int foreignRecords = 0;
        ulong blockNumber = 0;
        ChurnEpoch current = published;
        while (sw.ElapsedMilliseconds < ChurnMilliseconds && !Volatile.Read(ref stop))
        {
            StateId from = new(blockNumber, TestItem.KeccakA);
            StateId to = new(++blockNumber, TestItem.KeccakA);
            (Snapshot? snapshot, TransientResource? retired) = current.Bundle.CollectAndApplySnapshot(from, to, null);
            int recordCount = retired!.WarmedNodeLog.Count;
            retiredRecords += recordCount;
            foreignRecords += Math.Max(0, recordCount - 1);
            if (recordCount != 0 && !retired.WarmedNodeLog.TryGet(null, TreePath.Empty, current.Root.Hash, out _, out _)) foreignRecords++;
            snapshot!.Dispose();
            retired.ReleaseLease();
            retirements++;

            ChurnEpoch next = NewEpoch();
            Volatile.Write(ref published, next);
            current.Bundle.Dispose();
            current = next;
            disposals++;
        }

        Volatile.Write(ref stop, true);
        bool readersFinished = Task.WaitAll(readers, BailOutTimeout);
        // Leave the last bundle alive if a reader is stuck; disposing under it would only mask the hang.
        if (readersFinished) current.Bundle.Dispose();

        TestContext.Out.WriteLine(
            $"retirements={retirements} disposals={disposals} warmUps={Volatile.Read(ref warmUps)} " +
            $"queuedPrewarms={Volatile.Read(ref queuedPrewarms)} retiredRecords={retiredRecords} foreignRecords={foreignRecords}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readersFinished, Is.True, "reader tasks did not finish");
            Assert.That(readerException, Is.Null);
            Assert.That(foreignRecords, Is.Zero);
            // Prove both churn paths and the leased warm-ups actually ran, so the assertions above are not vacuous.
            Assert.That(retirements, Is.GreaterThan(0));
            Assert.That(disposals, Is.GreaterThan(0));
            Assert.That(Volatile.Read(ref warmUps), Is.GreaterThan(0));
            Assert.That(retiredRecords, Is.GreaterThan(0));
            Assert.That(Volatile.Read(ref queuedPrewarms), Is.GreaterThan(0));
        }
    }

    [Test]
    public void Warm_ups_publish_verified_records_and_bulk_sets_reuse_them([Values] bool concurrent, [Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        ValueHash256[] keys = new ValueHash256[64];
        for (int i = 0; i < keys.Length; i++) keys[i] = ValueKeccak.Compute([(byte)i]);
        byte[] newValue = Bytes.FromHexString("0xff");

        using INodeRecordBuffer persistedBuffer = FlatTestHelpers.NewTestBuffer();
        using INodeRecordBufferPin persistedRetain = persistedBuffer.Pin();
        using WarmedNodeLog unusedWarmed = new(1);
        TrieRoot root;
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(keys.Length))
        {
            for (int i = 0; i < keys.Length; i++) entries.Add(new PatriciaTree.BulkSetEntry(keys[i], [(byte)(i + 1)]));
            root = BufferBulkSet.BulkSet(persistedBuffer, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), unusedWarmed, address),
                address, TrieRoot.Empty, entries, canParallelize: false);
        }

        Dictionary<TreePath, byte[]> persisted = persistedBuffer.EnumerateNodes(root.Pointer)
            .ToDictionary(node => node.Path, node => node.Record.Rlp.ToArray());
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        using INodeRecordBufferPin retain = _buffer.Pin();
        TrieRoot preBlockRoot = _buffer.ResolveRoot(address, root.Hash);

        int firstWarmUp = WarmUpAll();
        int secondWarmUp = WarmUpAll();
        int readsBeforeBulkSet = reader.Reads;
        BufferedTrie trie = new(bundle, address, preBlockRoot);
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> change = new(1))
        {
            change.Add(new PatriciaTree.BulkSetEntry(keys[0], newValue));
            trie.BulkSet(change, canParallelize: false);
        }

        PatriciaTree expected = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        for (int i = 0; i < keys.Length; i++) expected.Set(keys[i].Bytes, [(byte)(i + 1)]);
        expected.Set(keys[0].Bytes, newValue);
        expected.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preBlockRoot.Pointer, Is.Zero, "root held by the buffer");
            Assert.That(firstWarmUp, concurrent ? Is.GreaterThanOrEqualTo(persisted.Count) : Is.EqualTo(persisted.Count));
            Assert.That(secondWarmUp, Is.Zero, "the position index reuses every warmed record");
            Assert.That(reader.Reads, Is.EqualTo(readsBeforeBulkSet));
            Assert.That(reader.OwnedReads, Is.Zero, "warming and demand loading must use the borrowed-span API");
            Assert.That(trie.RootHash, Is.EqualTo(expected.RootHash));
        }

        trie.Publish();
        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), retain.Copy());
        try
        {
            ulong[] published = storage ? snapshot!.StorageNodes.Select(node => node.Value).ToArray() : snapshot!.StateNodes.Select(node => node.Value).ToArray();
            Assert.That(published, Does.Contain(trie.Root.Pointer), "the root is published into the snapshot");
            foreach (ulong pointer in published)
            {
                Assert.That(NodeRecordCodec.IsTransient(pointer), Is.False, "snapshot pointers are real-buffer records");
                NodeRecordRef view = _buffer.GetRecord(pointer).View;
                for (int slot = 0; slot < 16; slot++)
                {
                    if ((view.HashMask & (1 << slot)) != 0) Assert.That(NodeRecordCodec.IsTransient(view.Child(slot).Pointer), Is.False, "published records cannot retain temporary children");
                }
            }

            int links = 0;
            foreach ((TreePath path, byte[] rlp) in persisted)
            {
                if (!retired!.WarmedNodeLog.TryGet(address, path, ValueKeccak.Compute(rlp), out NodeRecord record, out _)) continue;
                Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp), "published bytes remain complete under overlapping warm-ups");
                for (int slot = 0; slot < 16; slot++)
                {
                    if ((record.View.HashMask & (1 << slot)) == 0) continue;
                    ChildRef child = record.View.Child(slot);
                    if (child.Pointer == 0) continue;
                    links++;
                    Assert.That(retired.WarmedNodeLog.TryLocate(child.Pointer, out NodeRecord linked) && linked.Hash == child.Data, Is.True, "every published link resolves in its resource");
                }
            }
            Assert.That(links, Is.GreaterThan(0), "warm-up must populate child pointers, not just path entries");
        }
        finally
        {
            snapshot?.Dispose();
            retired?.ReleaseLease();
        }

        int WarmUpAll()
        {
            int before = reader.Reads;
            if (concurrent) Parallel.ForEach(keys, key => bundle.WarmUp(address, preBlockRoot, key));
            else foreach (ValueHash256 key in keys) bundle.WarmUp(address, preBlockRoot, key);
            return reader.Reads - before;
        }
    }

    /// <summary>Publishes the node <paramref name="rlp"/> into <paramref name="bundle"/> as a bulk set writing it would.</summary>
    private ulong PublishNode(SnapshotBundle bundle, Hash256? address, in TreePath path, byte[] rlp)
    {
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        ulong pointer = _buffer.AppendRecordFromRlp(address, path, hash, rlp);
        TrieRoot root = new(hash, pointer);
        if (address is null) bundle.PublishBufferedStateRoot(root);
        else bundle.PublishBufferedStorageRoot(address, root);
        return pointer;
    }

    /// <summary>
    /// Serves state and storage trie node RLP by path, counting reads; with
    /// <paramref name="release"/> each read signals <paramref name="entered"/> and waits for it.
    /// </summary>
    private sealed class NodeReader(
        IReadOnlyDictionary<TreePath, byte[]> nodes,
        ManualResetEventSlim? entered = null,
        ManualResetEventSlim? release = null) : NoopPersistenceReader, IPersistence.IPersistenceReader
    {
        private int _reads;
        private int _ownedReads;

        public int Reads => Volatile.Read(ref _reads);
        public int OwnedReads => Volatile.Read(ref _ownedReads);

        byte[]? IPersistence.IPersistenceReader.TryLoadStateRlp(in TreePath path, ReadFlags flags) => LoadOwned(path);

        byte[]? IPersistence.IPersistenceReader.TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => LoadOwned(path);

        private byte[]? LoadOwned(in TreePath path)
        {
            Interlocked.Increment(ref _ownedReads);
            return Load(path);
        }

        bool IPersistence.IPersistenceReader.TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) =>
            Load(path, action, arg);

        bool IPersistence.IPersistenceReader.TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) =>
            Load(path, action, arg);

        private bool Load<TArg>(in TreePath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            byte[]? rlp = Load(path);
            if (rlp is null) return false;
            Span<byte> buffer = stackalloc byte[rlp.Length];
            rlp.CopyTo(buffer);
            action(buffer, arg);
            buffer.Fill(0xFF);
            return true;
        }

        private byte[]? Load(in TreePath path)
        {
            Interlocked.Increment(ref _reads);
            if (release is not null)
            {
                entered!.Set();
                if (!release.Wait(BailOutTimeout)) throw new TimeoutException("persistence read was not released");
            }

            return nodes.GetValueOrDefault(path);
        }
    }
}
