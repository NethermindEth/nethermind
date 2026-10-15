// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleWarmerTests
{
    private const int ChurnMilliseconds = 2_000;
    private static readonly TimeSpan BailOutTimeout = TimeSpan.FromSeconds(30);

    private ResourcePool _pool = null!;
    private TrieNodeLog _log = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new ResourcePool(new FlatDbConfig());
        _log = FlatTestHelpers.NewTestLog();
    }

    private sealed class NullTrieNodeCache : ITrieNodeCache
    {
        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource) { }
        public void Clear() { }
    }

    private SnapshotBundle NewBundle(NodeReader reader) =>
        new(FlatTestHelpers.MakeBundle(_pool, reader), new NullTrieNodeCache(), _log, _pool, ResourcePool.Usage.MainBlockProcessing);

    /// <summary>A trie whose root is the leaf <paramref name="rootRlp"/>, as its root and the persisted nodes serving it.</summary>
    private static (TrieRoot Root, Dictionary<TreePath, byte[]> Nodes) LeafTrie(byte[] rootRlp) =>
        (new TrieRoot(ValueKeccak.Compute(rootRlp), 0), new Dictionary<TreePath, byte[]> { [TreePath.Empty] = rootRlp });

    // A warm-up reads persistence, never the bundle's recyclable snapshots: a node only the bundle's own committed snapshot
    // holds stays unwarmed, though a bulk set still finds it there, while a persisted node is warmed.
    [Test]
    public void Warm_up_reads_persistence_only_and_ignores_the_bundle_snapshots([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] committedRlp = FlatTestHelpers.LeafRlp(0x82);
        (TrieRoot persistedRoot, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        TrieRoot committedRoot = new(ValueKeccak.Compute(committedRlp), 0);
        using SnapshotBundle bundle = NewBundle(new NodeReader(persisted));

        TrieNodeLogRetain retain = _log.RetainActive();
        PublishNode(bundle, address, TreePath.Empty, committedRlp);
        bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), retain, returnSnapshot: false);

        bundle.WarmUp(address, committedRoot, ValueKeccak.Zero);
        bundle.WarmUp(address, persistedRoot, ValueKeccak.Zero);
        bool committedLoadable = bundle.TryLoadTrieNodeRecord(address, TreePath.Empty, committedRoot.Hash, out _);

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(new StateId(1, TestItem.KeccakA), new StateId(2, TestItem.KeccakA), null);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retired!.WarmedNodes.TryGet(address, TreePath.Empty, persistedRoot.Hash, out _), Is.True, "persisted node warmed");
                Assert.That(retired.WarmedNodes.TryGet(address, TreePath.Empty, committedRoot.Hash, out _), Is.False, "committed node warmed");
                Assert.That(committedLoadable, Is.True, "committed node loadable by a bulk set");
            }
        }
        finally
        {
            snapshot?.Dispose();
            retired?.ReleaseLease();
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
            Assert.That(rented.WarmedNodes.TryInsert(address, TreePath.Empty, hash, rlp, out _), Is.True);
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
                Assert.That(recycled.WarmedNodes.TryGet(address, TreePath.Empty, hash, out _), Is.False);
            }
        }
        finally
        {
            recycled.ReleaseLease();
        }
    }

    // Retirement must not scan the transient while a warm-up that passed the lease check is still writing to it.
    [Test]
    public void Retirement_waits_for_an_in_flight_warm_up()
    {
        (TrieRoot root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        using ManualResetEventSlim readEntered = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        using SnapshotBundle bundle = NewBundle(new NodeReader(persisted, readEntered, releaseRead));

        Task warmUp = Task.Run(() => bundle.WarmUp(null, root, ValueKeccak.Zero));
        Assert.That(readEntered.Wait(BailOutTimeout), Is.True, "the warm-up did not reach persistence");

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), null);
        try
        {
            Task retirement = Task.Run(retired!.WaitForExclusiveLease);
            bool retiredDuringWarmUp = retirement.Wait(TimeSpan.FromMilliseconds(200));
            releaseRead.Set();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(retiredDuringWarmUp, Is.False, "retirement scanned the transient under an in-flight warm-up");
                Assert.That(retirement.Wait(BailOutTimeout), Is.True, "retirement did not resume after the warm-up released its lease");
                Assert.That(warmUp.Wait(BailOutTimeout), Is.True);
                Assert.That(retired.WarmedNodes.TryGet(null, TreePath.Empty, root.Hash, out _), Is.True, "the warm-up warmed the transient it leased");
            }
        }
        finally
        {
            releaseRead.Set();
            snapshot?.Dispose();
            retired?.ReleaseLease();
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
                Assert.That(retired!.WarmedNodes.Count, Is.Zero);
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
        List<NodeRecordRef> records = [];
        ulong blockNumber = 0;
        ChurnEpoch current = published;
        while (sw.ElapsedMilliseconds < ChurnMilliseconds && !Volatile.Read(ref stop))
        {
            StateId from = new(blockNumber, TestItem.KeccakA);
            StateId to = new(++blockNumber, TestItem.KeccakA);
            (Snapshot? snapshot, TransientResource? retired) = current.Bundle.CollectAndApplySnapshot(from, to, null);
            retired!.WaitForExclusiveLease();
            records.Clear();
            retired.WarmedNodes.CopyAllTo(records);
            retiredRecords += records.Count;
            foreignRecords += records.Count(record => record.Hash != current.Root.Hash);
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
    public void Warm_up_fetches_each_persisted_node_once_and_bulk_sets_read_it_without_persistence()
    {
        Hash256 address = TestItem.KeccakC;
        ValueHash256[] keys = new ValueHash256[64];
        for (int i = 0; i < keys.Length; i++) keys[i] = ValueKeccak.Compute([(byte)i]);
        byte[] newValue = Bytes.FromHexString("0xff");

        TrieNodeLog persistedLog = FlatTestHelpers.NewTestLog();
        using TrieNodeLogRetain persistedRetain = persistedLog.RetainActive();
        using WrittenNodes written = new();
        TrieRoot root;
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(keys.Length))
        {
            for (int i = 0; i < keys.Length; i++) entries.Add(new PatriciaTree.BulkSetEntry(keys[i], [(byte)(i + 1)]));
            root = LogBulkSet.BulkSet(persistedLog, new DictionaryNodeSource(new Dictionary<TreePath, byte[]>(), new WarmedNodes(1), address),
                address, TrieRoot.Empty, entries, written, canParallelize: false);
        }

        Dictionary<TreePath, byte[]> persisted = written.AsSpan().ToArray()
            .ToDictionary(node => node.Path, node => persistedLog.GetRecord(node.Pointer).Rlp.ToArray());
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        using TrieNodeLogRetain retain = _log.RetainActive();
        TrieRoot preBlockRoot = LogBulkSet.ResolveRoot(_log, address, root.Hash);

        int firstWarmUp = WarmUpAll();
        int secondWarmUp = WarmUpAll();
        int readsBeforeBulkSet = reader.Reads;
        using LoggedTrie trie = new(bundle, _log, address, preBlockRoot);
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
            Assert.That(preBlockRoot.Pointer, Is.Zero, "root held by the log");
            Assert.That(firstWarmUp, Is.EqualTo(persisted.Count));
            Assert.That(secondWarmUp, Is.Zero);
            Assert.That(reader.Reads, Is.EqualTo(readsBeforeBulkSet));
            Assert.That(trie.RootHash, Is.EqualTo(expected.RootHash));
        }

        int WarmUpAll()
        {
            int before = reader.Reads;
            foreach (ValueHash256 key in keys) bundle.WarmUp(address, preBlockRoot, key);
            return reader.Reads - before;
        }
    }

    /// <summary>Publishes the node <paramref name="rlp"/> into <paramref name="bundle"/> as a bulk set writing it would.</summary>
    private void PublishNode(SnapshotBundle bundle, Hash256? address, in TreePath path, byte[] rlp)
    {
        using WrittenNodes written = new();
        written.Add(path, _log.AppendRecordFromRlp(address, path, ValueKeccak.Compute(rlp), rlp));
        if (address is null) bundle.PublishLoggedStateNodes(written);
        else bundle.PublishLoggedStorageNodes(bundle.GetStorageNodeDestination(address), written);
    }

    /// <summary>
    /// Serves state and storage trie node RLP by path through the span reads only, counting them; with
    /// <paramref name="release"/> each read signals <paramref name="entered"/> and waits for it.
    /// </summary>
    private sealed class NodeReader(
        IReadOnlyDictionary<TreePath, byte[]> nodes,
        ManualResetEventSlim? entered = null,
        ManualResetEventSlim? release = null) : NoopPersistenceReader, IPersistence.IPersistenceReader
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        bool IPersistence.IPersistenceReader.TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) =>
            Load(path, action, arg);

        bool IPersistence.IPersistenceReader.TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) =>
            Load(path, action, arg);

        private bool Load<TArg>(in TreePath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            Interlocked.Increment(ref _reads);
            if (release is not null)
            {
                entered!.Set();
                if (!release.Wait(BailOutTimeout)) throw new TimeoutException("persistence read was not released");
            }

            if (!nodes.TryGetValue(path, out byte[]? rlp)) return false;
            action(rlp, arg);
            return true;
        }
    }
}
