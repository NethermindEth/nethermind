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
using Nethermind.Trie.Native;
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
        _buffer = FlatTestHelpers.NewTestBuffer();
    }

    [TearDown]
    public void TearDown() => _buffer.Dispose();

    private SnapshotBundle NewBundle(NodeReader reader) =>
        new(FlatTestHelpers.MakeBundle(_pool, reader), _buffer, _pool, ResourcePool.Usage.MainBlockProcessing);

    private static (Hash256 Root, Dictionary<TreePath, byte[]> Nodes) LeafTrie(byte[] rootRlp) =>
        (new Hash256(ValueKeccak.Compute(rootRlp)), new Dictionary<TreePath, byte[]> { [TreePath.Empty] = rootRlp });

    private void WarmUp(SnapshotBundle bundle, Hash256? address, Hash256 root, in ValueHash256 key)
    {
        using NativeTrieNodeStore source = new(bundle, address, _buffer);
        bundle.WarmUpNative(source, _buffer, address, root, key);
    }

    [Test]
    public void Warm_up_reads_persistence_only_and_reuses_committed_nodes_from_the_buffer([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] committedRlp = FlatTestHelpers.LeafRlp(0x82);
        (Hash256 persistedRoot, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        using INodeRecordBufferPin pin = _buffer.PinHead();
        ulong committedPointer = PublishNode(bundle, address, TreePath.Empty, committedRlp);
        Hash256 committedRoot = new(ValueKeccak.Compute(committedRlp));
        bundle.CollectAndApplySnapshot(
            StateId.PreGenesis, new StateId(1, TestItem.KeccakA), returnSnapshot: false, bufferPin: pin.Copy());

        WarmUp(bundle, address, committedRoot, ValueKeccak.Zero);
        Assert.That(reader.Reads, Is.Zero, "committed nodes are read through the native buffer, not the persisted source");
        WarmUp(bundle, address, persistedRoot, ValueKeccak.Zero);
        WarmUp(bundle, address, persistedRoot, ValueKeccak.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Reads, Is.EqualTo(1), "persisted nodes are reused within the transient resource");
            Assert.That(_buffer.Native.GetNodeRlp(committedPointer, out _).ToArray(), Is.EqualTo(committedRlp));
        }
    }

    [Test]
    public void Returned_transient_resource_clears_warmed_nodes([Values] bool storage)
    {
        ResourcePool.Usage usage = ResourcePool.Usage.MainBlockProcessing;
        Hash256? address = storage ? TestItem.KeccakC : null;
        (Hash256 root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        using NativeTrieNodeStore source = new(bundle, address, _buffer);
        TransientResource rented = _pool.GetCachedResource(usage);
        try
        {
            NativePatriciaBulkSet.WarmUp(source, _buffer.Native, rented.NativeWarmedNodes, address, root.ValueHash256, ValueKeccak.Zero);
            NativePatriciaBulkSet.WarmUp(source, _buffer.Native, rented.NativeWarmedNodes, address, root.ValueHash256, ValueKeccak.Zero);
            Assert.That(reader.Reads, Is.EqualTo(1));
        }
        finally
        {
            rented.ReleaseLease();
        }

        TransientResource recycled = _pool.GetCachedResource(usage);
        try
        {
            NativePatriciaBulkSet.WarmUp(source, _buffer.Native, recycled.NativeWarmedNodes, address, root.ValueHash256, ValueKeccak.Zero);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recycled, Is.SameAs(rented));
                Assert.That(reader.Reads, Is.EqualTo(2), "recycling clears the previous warmed records");
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
        (Hash256 root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        WarmUp(bundle, address, root, ValueKeccak.Zero);
        Assert.That(reader.Reads, Is.EqualTo(1));

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), null);
        snapshot?.Dispose();
        retired!.ReleaseLease();
        WarmUp(bundle, address, root, ValueKeccak.Zero);
        WarmUp(bundle, address, root, ValueKeccak.Zero);

        Assert.That(reader.Reads, Is.EqualTo(2), "a replacement transient reloads once without a shared cache");
    }

    [Test]
    public void Retirement_releases_the_owner_but_recycling_waits_for_an_in_flight_warm_up()
    {
        (Hash256 root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        using ManualResetEventSlim readEntered = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        using SnapshotBundle bundle = NewBundle(new NodeReader(persisted, readEntered, releaseRead));

        Task warmUp = Task.Run(() => WarmUp(bundle, null, root, ValueKeccak.Zero));
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
            Assert.That(recycled, Is.SameAs(retired), "the final warmer release returns the resource exactly once");

            NodeReader reader = new(persisted);
            using SnapshotBundle verifier = NewBundle(reader);
            using NativeTrieNodeStore source = new(verifier, null, _buffer);
            NativePatriciaBulkSet.WarmUp(source, _buffer.Native, recycled.NativeWarmedNodes, null, root.ValueHash256, ValueKeccak.Zero);
            Assert.That(reader.Reads, Is.EqualTo(1), "reset happens after the warmer finishes writing");
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

    [Test]
    public void Warm_up_does_not_warm_a_mismatching_or_invalid_node(
        [Values] bool storage,
        [ValueSource(nameof(MismatchingOrInvalidNodes))] (string PersistedRlpHex, bool RequestPersistedHash) persisted)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] persistedRlp = Bytes.FromHexString(persisted.PersistedRlpHex);
        Hash256 root = persisted.RequestPersistedHash ? new Hash256(ValueKeccak.Compute(persistedRlp)) : TestItem.KeccakB;
        NodeReader reader = new(new Dictionary<TreePath, byte[]> { [TreePath.Empty] = persistedRlp });
        using SnapshotBundle bundle = NewBundle(reader);

        WarmUp(bundle, address, root, ValueKeccak.Zero);
        WarmUp(bundle, address, root, ValueKeccak.Zero);

        Assert.That(reader.Reads, Is.EqualTo(2), "invalid or mismatching records must not satisfy the next warm-up");
    }

    [Test]
    public void Warm_up_on_a_disposed_bundle_is_a_no_op()
    {
        (Hash256 root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(0x80));
        NodeReader reader = new(persisted);
        SnapshotBundle bundle = NewBundle(reader);

        Assert.That(bundle.TryLeaseReadOnlyBundle(), Is.True);
        try
        {
            bundle.Dispose();
            Task<bool> warmUp = Task.Run(() =>
            {
                WarmUp(bundle, null, root, ValueKeccak.Zero);
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

    [Test]
    public void In_flight_warm_up_reads_the_next_node_after_owner_disposal([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakC : null;
        byte[] leaf = FlatTestHelpers.LeafRlp(0x80);
        byte[] rootRlp = FlatTestHelpers.ExtensionRlp([0], ValueKeccak.Compute(leaf));
        Hash256 root = new(ValueKeccak.Compute(rootRlp));
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        NodeReader reader = new(new Dictionary<TreePath, byte[]>
        {
            [TreePath.Empty] = rootRlp,
            [TreePath.Empty.Append(0)] = leaf
        }, entered, release);
        using SnapshotBundle bundle = NewBundle(reader);
        Task warmUp = Task.Run(() => WarmUp(bundle, address, root, ValueKeccak.Zero));
        try
        {
            Assert.That(entered.Wait(BailOutTimeout), Is.True, "the root read must be in flight before disposal");
            bundle.Dispose();
            release.Set();
            Assert.That(warmUp.Wait(BailOutTimeout), Is.True);
            Assert.That(reader.Reads, Is.EqualTo(2), "the leased traversal must still fetch the extension's child");
            Assert.That(() => bundle.TryLoadStateRlp(TreePath.Empty, root, ReadFlags.None), Throws.TypeOf<ObjectDisposedException>());
        }
        finally
        {
            release.Set();
            if (!warmUp.IsCompleted) warmUp.Wait(BailOutTimeout);
        }
    }

    private sealed record ChurnEpoch(SnapshotBundle Bundle, Hash256 Root);

    [Test]
    public void Warm_ups_survive_owner_churn()
    {
        int epochs = 0;
        ChurnEpoch NewEpoch()
        {
            (Hash256 root, Dictionary<TreePath, byte[]> persisted) = LeafTrie(FlatTestHelpers.LeafRlp(BitConverter.GetBytes(++epochs)));
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
                            WarmUp(epoch.Bundle, null, epoch.Root, ValueKeccak.Zero);
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
        ulong blockNumber = 0;
        ChurnEpoch current = published;
        while (sw.ElapsedMilliseconds < ChurnMilliseconds && !Volatile.Read(ref stop))
        {
            StateId from = new(blockNumber, TestItem.KeccakA);
            StateId to = new(++blockNumber, TestItem.KeccakA);
            (Snapshot? snapshot, TransientResource? retired) = current.Bundle.CollectAndApplySnapshot(from, to, null);
            snapshot!.Dispose();
            retired!.ReleaseLease();
            retirements++;

            ChurnEpoch next = NewEpoch();
            Volatile.Write(ref published, next);
            current.Bundle.Dispose();
            current = next;
            disposals++;
        }

        Volatile.Write(ref stop, true);
        bool readersFinished = Task.WaitAll(readers, BailOutTimeout);
        if (readersFinished) current.Bundle.Dispose();

        TestContext.Out.WriteLine(
            $"retirements={retirements} disposals={disposals} warmUps={Volatile.Read(ref warmUps)} queuedPrewarms={Volatile.Read(ref queuedPrewarms)}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(readersFinished, Is.True, "reader tasks did not finish");
            Assert.That(readerException, Is.Null);
            Assert.That(retirements, Is.GreaterThan(0));
            Assert.That(disposals, Is.GreaterThan(0));
            Assert.That(Volatile.Read(ref warmUps), Is.GreaterThan(0));
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
        using INodeRecordBufferPin persistedPin = persistedBuffer.PinHead();
        PatriciaTree.BulkSetEntry[] entries = keys.Select((key, i) => new PatriciaTree.BulkSetEntry(key, [(byte)(i + 1)])).ToArray();
        Hash256 root = FlatTestHelpers.WriteNative(persistedBuffer, address, Keccak.EmptyTreeHash, entries);
        ulong rootPointer = persistedBuffer.FindRoot(address, root.ValueHash256);
        Dictionary<TreePath, byte[]> persisted = persistedBuffer.EnumerateNodes(rootPointer, persistedPin.WriteBoundary)
            .ToDictionary(node => node.Path, node => persistedBuffer.Native.GetNodeRlp(node.Pointer, out _).ToArray());
        NodeReader reader = new(persisted);
        using SnapshotBundle bundle = NewBundle(reader);
        using INodeRecordBufferPin pin = _buffer.PinHead();
        using NativeTrieNodeStore source = new(bundle, address, _buffer);

        int firstWarmUp = WarmUpAll();
        int secondWarmUp = WarmUpAll();
        int readsBeforeBulkSet = reader.Reads;
        Hash256 updatedRoot = source.BulkSet(root, [new PatriciaTree.BulkSetEntry(keys[0], newValue)]);
        source.Publish(updatedRoot);

        PatriciaTree expected = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        for (int i = 0; i < keys.Length; i++) expected.Set(keys[i].Bytes, [(byte)(i + 1)]);
        expected.Set(keys[0].Bytes, newValue);
        expected.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstWarmUp, concurrent ? Is.GreaterThanOrEqualTo(persisted.Count) : Is.EqualTo(persisted.Count));
            Assert.That(secondWarmUp, Is.Zero, "linked warmed paths are reused without persistence");
            Assert.That(reader.Reads, Is.EqualTo(readsBeforeBulkSet));
            Assert.That(updatedRoot, Is.EqualTo(expected.RootHash));
        }

        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(
            StateId.PreGenesis, new StateId(1, updatedRoot), bufferPin: pin.Copy());
        try
        {
            ulong[] published = storage ? snapshot!.StorageNodes.Select(node => node.Pointer).ToArray() : snapshot!.StateNodes.Select(node => node.Pointer).ToArray();
            Assert.That(published, Does.Contain(_buffer.FindRoot(address, updatedRoot.ValueHash256)));
            retired!.ReleaseLease();
            retired = null;
            foreach (ulong pointer in published)
                Assert.That(_buffer.Native.GetNodeRlp(pointer, out _).ToArray(), Is.Not.Empty, "persistent records survive transient reset");

            FlatTestHelpers.StartNewGeneration(_buffer);
            using NativeTrieNodeStore next = new(bundle, address, _buffer);
            Hash256 nextRoot = next.BulkSet(updatedRoot, [new PatriciaTree.BulkSetEntry(keys[0], [(byte)0xfe])]);
            expected.Set(keys[0].Bytes, [(byte)0xfe]);
            expected.UpdateRootHash();
            Assert.That(nextRoot, Is.EqualTo(expected.RootHash), "published child references survive warmed-resource reset");
        }
        finally
        {
            snapshot?.Dispose();
            retired?.ReleaseLease();
        }

        int WarmUpAll()
        {
            int before = reader.Reads;
            if (concurrent) Parallel.ForEach(keys, key => WarmUp(bundle, address, root, key));
            else foreach (ValueHash256 key in keys) WarmUp(bundle, address, root, key);
            return reader.Reads - before;
        }
    }

    private ulong PublishNode(SnapshotBundle bundle, Hash256? address, in TreePath path, byte[] rlp)
    {
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        ulong pointer = _buffer.AppendRecordFromRlp(address, path, hash, rlp);
        if (address is null) bundle.PublishNativeStateRoot(pointer);
        else bundle.PublishNativeStorageRoot(address, pointer, hash);
        return pointer;
    }

    private sealed class NodeReader(
        IReadOnlyDictionary<TreePath, byte[]> nodes,
        ManualResetEventSlim? entered = null,
        ManualResetEventSlim? release = null) : NoopPersistenceReader, IPersistence.IPersistenceReader
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);

        byte[]? IPersistence.IPersistenceReader.TryLoadStateRlp(in TreePath path, ReadFlags flags) => Load(path);
        byte[]? IPersistence.IPersistenceReader.TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => Load(path);
        bool IPersistence.IPersistenceReader.TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) => Load(path, action, arg);
        bool IPersistence.IPersistenceReader.TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) => Load(path, action, arg);

        private bool Load<TArg>(in TreePath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            byte[]? rlp = Load(path);
            if (rlp is null) return false;
            action(rlp, arg);
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
