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
        using INodeRecordBufferPin retain = buffer.PinHead();
        byte[] prefix = FlatTestHelpers.LeafRlp(0x80);
        buffer.AppendRecordFromRlp(address, TreePath.Empty, ValueKeccak.Compute(prefix), prefix, []);
        int completedBefore = buffer.CompletedRecords;
        int batchesBefore = buffer.CompletedWrites;
        ValueHash256[] keys = new ValueHash256[64];
        byte[] value = new byte[40];
        value.AsSpan().Fill(0x42);
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = ValueKeccak.Compute([(byte)i]);
            entries.Add(new PatriciaTree.BulkSetEntry(keys[i], value));
        }

        (ValueHash256 Hash, ulong Pointer) root = FlatTestHelpers.BulkSet(_pool, buffer, address, (ValueKeccak.EmptyTreeHash, 0), entries, canParallelize: false);
        (TreePath Path, ulong Pointer, NodeRecord Record)[] nodes = buffer.EnumerateNodes(root.Pointer).ToArray();
        PatriciaTree expected = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach (ValueHash256 key in keys) expected.Set(key.Bytes, value);
        buffer.SetRoot(address, root.Hash, root.Pointer);
        expected.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.FindRoot(address, root.Hash, ulong.MaxValue), Is.EqualTo(root.Pointer));
            Assert.That(buffer.CompletedRecords - completedBefore, Is.EqualTo(nodes.Length));
            Assert.That(buffer.CompletedWrites - batchesBefore, Is.LessThan(nodes.Length), "a batch contains multiple relative record offsets");
            Assert.That(nodes.All(node => node.Record.Hash == ValueKeccak.Compute(node.Record.Rlp)), Is.True);
            Assert.That(root.Hash, Is.EqualTo(expected.RootHash.ValueHash256));
            Assert.That(retain.Buffer, Is.SameAs(buffer));
        }
    }

    [Test]
    public void Publishing_a_root_the_buffer_does_not_hold_copies_its_record_in()
    {
        byte[] rlp = FlatTestHelpers.LeafRlp(new byte[40]);
        ((ValueHash256 Hash, ulong Pointer) root, Dictionary<TreePath, byte[]> nodes) = LeafTrie(rlp);
        using SnapshotBundle bundle = NewBundle(new NodeReader(nodes));
        using INodeRecordBufferPin retain = _buffer.PinHead();
        new NodeRecordTrie(bundle, null, root).Publish();

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, root.Hash), retain.Copy());
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot!.StateRootPointer, Is.Not.Zero);
                Assert.That(snapshot.TryGetStateNode(TreePath.Empty, out TrieNode? node), Is.True);
                Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(rlp));
                Assert.That(_buffer.FindRoot(null, root.Hash, ulong.MaxValue), Is.EqualTo(snapshot.StateRootPointer), "the copy is indexed");
            }
        }
        finally
        {
            snapshot?.Dispose();
            retired?.ReleaseLease();
        }
    }

    private SnapshotBundle NewBundle(NodeReader reader) =>
        new(FlatTestHelpers.MakeBundle(_pool, reader), _buffer, _pool, ResourcePool.Usage.MainBlockProcessing);

    /// <summary>A trie whose root is the leaf <paramref name="rootRlp"/>, as its root and the persisted nodes serving it.</summary>
    private static ((ValueHash256 Hash, ulong Pointer) Root, Dictionary<TreePath, byte[]> Nodes) LeafTrie(byte[] rootRlp) =>
        ((ValueKeccak.Compute(rootRlp), 0), new Dictionary<TreePath, byte[]> { [TreePath.Empty] = rootRlp });

    // Committed snapshot nodes stay in the retained buffer; only persisted nodes enter the warmed source.
    [Test]
    public void Warm_up_reads_persistence_only_and_ignores_the_bundle_snapshots([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] committedRlp = FlatTestHelpers.LeafRlp(0x82);
        ((ValueHash256 Hash, ulong Pointer) persistedRoot, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        (ValueHash256 Hash, ulong Pointer) committedRoot = (ValueKeccak.Compute(committedRlp), 0);
        using SnapshotBundle bundle = NewBundle(new NodeReader(persisted));

        INodeRecordBufferPin retain = _buffer.PinHead();
        committedRoot = (committedRoot.Hash, PublishNode(bundle, address, TreePath.Empty, committedRlp));
        bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), retain, returnSnapshot: false);

        bundle.WarmUp(address, committedRoot, ValueKeccak.Zero, forDelete: false);
        bundle.WarmUp(address, persistedRoot, ValueKeccak.Zero, forDelete: false);

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(new StateId(1, TestItem.KeccakA), new StateId(2, TestItem.KeccakA), null);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retired!.WarmedNodeBuffer.TryGet(address, TreePath.Empty, out NodeRecord warmedRoot, out _), Is.True);
                Assert.That(warmedRoot.Hash, Is.EqualTo(persistedRoot.Hash), "the persisted node is warmed, the committed one is not");
                Assert.That(_buffer.TryLocate(committedRoot.Pointer, out NodeRecord committedRecord) && committedRecord.Rlp.SequenceEqual(committedRlp), Is.True, "committed node remains in the retained buffer");
            }
        }
        finally
        {
            snapshot?.Dispose();
            retired?.ReleaseLease();
        }
    }

    [Test]
    public void Warmed_nodes_are_read_from_persistence_once([Values] bool storage, [Values] bool warmUp,
        [Values(ResourcePool.Usage.MainBlockProcessing, ResourcePool.Usage.ReadOnlyProcessingEnv)] ResourcePool.Usage usage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        ((ValueHash256 Hash, ulong Pointer) root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool, reader), _buffer, _pool, usage);

        if (warmUp)
        {
            bundle.WarmUp(address, root, ValueKeccak.Zero, forDelete: false);
            bundle.WarmUp(address, root, ValueKeccak.Zero, forDelete: false);
        }
        else
        {
            Assert.That(bundle.TryLoadTrieNodeRecord(address, TreePath.Empty, root.Hash, out _), Is.True);
            Assert.That(bundle.TryLoadTrieNodeRecord(address, TreePath.Empty, root.Hash, out _), Is.True);
        }

        Assert.That(reader.Reads, Is.EqualTo(1));
    }

    [Test]
    public void Idle_worker_reads_a_warmed_node_from_persistence_once()
    {
        Hash256 address = TestItem.KeccakC;
        byte[] rlp = FlatTestHelpers.LeafRlp(0x80);
        ((ValueHash256 Hash, ulong Pointer) root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(rlp);
        NodeReader reader = new(persisted);
        using ReadOnlySnapshotBundle readOnly = FlatTestHelpers.MakeBundle(_pool, reader);
        using SnapshotPooledList snapshots = new(1);
        using SnapshotBundle.BackgroundTrieSource source = new(readOnly, _buffer, snapshots, address);

        Assert.That(source.TryLoad(TreePath.Empty, root.Hash, out _), Is.True);
        Assert.That(source.TryLoad(TreePath.Empty, root.Hash, out NodeRecord record), Is.True);
        Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp));
        Assert.That(reader.Reads, Is.EqualTo(1));
    }

    [Test]
    public void Warm_up_reads_a_node_the_buffer_or_the_warmed_nodes_hold_in_place([Values] bool inBuffer)
    {
        byte[] rlp = FlatTestHelpers.LeafRlp(0x80);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        using WarmedNodeBuffer warmed = new(1);
        ulong pointer;
        if (inBuffer) pointer = _buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp, []);
        else warmed.Append(null, TreePath.Empty, hash, rlp, [], out _, out pointer);

        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), warmed, null);
        source.WarmUp(_buffer, (hash, pointer), ValueKeccak.Zero);

        Assert.That(source.FetchCount, Is.Zero, "the node is read from the buffer or the warmed nodes");
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
            rented.WarmedNodeBuffer.Append(address, TreePath.Empty, hash, rlp, [], out _, out _);
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
                Assert.That(recycled.WarmedNodeBuffer.TryGet(address, TreePath.Empty, out _, out _), Is.False);
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
        ((ValueHash256 Hash, ulong Pointer) root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        bundle.WarmUp(address, root, ValueKeccak.Zero, forDelete: false);
        Assert.That(reader.Reads, Is.EqualTo(1));

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), null);
        snapshot?.Dispose();
        retired!.ReleaseLease();
        bundle.WarmUp(address, root, ValueKeccak.Zero, forDelete: false);

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
        ((ValueHash256 Hash, ulong Pointer) root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        using ManualResetEventSlim readEntered = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        using SnapshotBundle bundle = NewBundle(new NodeReader(persisted, readEntered, releaseRead));

        Task warmUp = Task.Run(() => bundle.WarmUp(null, root, ValueKeccak.Zero, forDelete: false));
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
                Assert.That(recycled.WarmedNodeBuffer.Count, Is.Zero, "reset happens after the warmer finishes writing");
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

    // Persisted RLP is trusted to be the requested node, but bytes that are not a trie node are never warmed.
    [Test]
    public void Warm_up_does_not_warm_an_invalid_node([Values] bool storage, [Values("c28001", "f8")] string persistedRlpHex)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] persistedRlp = Bytes.FromHexString(persistedRlpHex);
        (ValueHash256 Hash, ulong Pointer) root = (ValueKeccak.Compute(persistedRlp), 0);
        NodeReader reader = new(new Dictionary<TreePath, byte[]> { [TreePath.Empty] = persistedRlp });
        using SnapshotBundle bundle = NewBundle(reader);

        bundle.WarmUp(address, root, ValueKeccak.Zero, forDelete: false);

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), null);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.Reads, Is.EqualTo(1));
                Assert.That(retired!.WarmedNodeBuffer.Count, Is.Zero);
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
        ((ValueHash256 Hash, ulong Pointer) root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        SnapshotBundle bundle = NewBundle(reader);

        Assert.That(bundle.TryLeaseReadOnlyBundle(), Is.True);
        try
        {
            bundle.Dispose();

            Task<bool> warmUp = Task.Run(() =>
            {
                bundle.WarmUp(null, root, ValueKeccak.Zero, forDelete: false);
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

    private sealed record ChurnEpoch(SnapshotBundle Bundle, (ValueHash256 Hash, ulong Pointer) Root);

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
            ((ValueHash256 Hash, ulong Pointer) root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(BitConverter.GetBytes(++epochs)));
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
                            epoch.Bundle.WarmUp(null, epoch.Root, ValueKeccak.Zero, forDelete: false);
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
            int recordCount = retired!.WarmedNodeBuffer.Count;
            retiredRecords += recordCount;
            foreignRecords += Math.Max(0, recordCount - 1);
            if (recordCount != 0 && !(retired.WarmedNodeBuffer.TryGet(null, TreePath.Empty, out NodeRecord warmedRoot, out _) && warmedRoot.Hash == current.Root.Hash)) foreignRecords++;
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
        using INodeRecordBufferPin persistedRetain = persistedBuffer.PinHead();
        (ValueHash256 Hash, ulong Pointer) root;
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(keys.Length))
        {
            for (int i = 0; i < keys.Length; i++) entries.Add(new PatriciaTree.BulkSetEntry(keys[i], [(byte)(i + 1)]));
            root = FlatTestHelpers.BulkSet(_pool, persistedBuffer, address, (ValueKeccak.EmptyTreeHash, 0), entries, canParallelize: false);
        }

        Dictionary<TreePath, byte[]> persisted = persistedBuffer.EnumerateNodes(root.Pointer)
            .ToDictionary(node => node.Path, node => node.Record.Rlp.ToArray());
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        using INodeRecordBufferPin retain = _buffer.PinHead();
        (ValueHash256 Hash, ulong Pointer) preBlockRoot = (root.Hash, _buffer.FindRoot(address, root.Hash, ulong.MaxValue));

        int firstWarmUp = WarmUpAll();
        int secondWarmUp = WarmUpAll();
        int readsBeforeBulkSet = reader.Reads;
        NodeRecordTrie trie = new(bundle, address, preBlockRoot);
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
            ulong[] published = storage ? snapshot!.StorageNodes.Select(node => node.Pointer).ToArray() : snapshot!.StateNodes.Select(node => node.Pointer).ToArray();
            Assert.That(published, Does.Contain(trie.Root.Pointer), "the root is published into the snapshot");
            Assert.That(published.All(pointer => _buffer.TryLocate(pointer, out _)), Is.True, "snapshot pointers are real-buffer records");
            // A published record linking a warmed child would end the walk through the real buffer early.
            WarmedNodeRecordBuffer warmedView = new(_buffer, retired!.WarmedNodeBuffer);
            Assert.That(_buffer.EnumerateNodes(trie.Root.Pointer).Count(), Is.EqualTo(warmedView.EnumerateNodes(trie.Root.Pointer).Count()), "published records cannot retain temporary children");

            Assert.That(retired.WarmedNodeBuffer.TryGet(address, TreePath.Empty, out _, out ulong warmedRoot), Is.True);
            (TreePath Path, ulong Pointer, NodeRecord Record)[] warmedNodes = warmedView.EnumerateNodes(warmedRoot).ToArray();
            Assert.That(warmedNodes, Has.Length.GreaterThan(1), "warm-up must populate child pointers, not just path entries");
            foreach ((TreePath path, _, NodeRecord record) in warmedNodes)
                Assert.That(record.Rlp.ToArray(), Is.EqualTo(persisted[path]), "published bytes remain complete under overlapping warm-ups");
        }
        finally
        {
            snapshot?.Dispose();
            retired?.ReleaseLease();
        }

        int WarmUpAll()
        {
            int before = reader.Reads;
            if (concurrent) Parallel.ForEach(keys, key => bundle.WarmUp(address, preBlockRoot, key, forDelete: false));
            else foreach (ValueHash256 key in keys) bundle.WarmUp(address, preBlockRoot, key, forDelete: false);
            return reader.Reads - before;
        }
    }

    /// <summary>Publishes the node <paramref name="rlp"/> into <paramref name="bundle"/> as a bulk set writing it would.</summary>
    private ulong PublishNode(SnapshotBundle bundle, Hash256? address, in TreePath path, byte[] rlp)
    {
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        ulong pointer = _buffer.AppendRecordFromRlp(address, path, hash, rlp, []);
        (ValueHash256 Hash, ulong Pointer) root = (hash, pointer);
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
