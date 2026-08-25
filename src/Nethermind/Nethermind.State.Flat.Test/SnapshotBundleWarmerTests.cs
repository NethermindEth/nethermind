// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleWarmerTests
{
    private const int ChurnMilliseconds = 2_000;
    private static readonly TimeSpan BailOutTimeout = TimeSpan.FromSeconds(30);

    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp() => _pool = new ResourcePool(new FlatDbConfig());

    private sealed class NullTrieNodeCache : ITrieNodeCache
    {
        public int TryGetCount;

        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            TryGetCount++;
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource) { }
        public void Clear() { }
    }

    private sealed class BlockingTrieNodeCache(ManualResetEventSlim entered, ManualResetEventSlim release) : ITrieNodeCache
    {
        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            entered.Set();
            if (!release.Wait(BailOutTimeout)) throw new TimeoutException("warmer read was not released");
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource) { }
        public void Clear() { }
    }

    private SnapshotBundle NewBundle(Action<SnapshotContent>? persisted = null) =>
        new(FlatTestHelpers.MakeBundle(_pool, persisted), new NullTrieNodeCache(), _pool, ResourcePool.Usage.MainBlockProcessing);

    private static TrieNode Leaf(byte value) => new(NodeType.Leaf, new byte[] { 0xC1, value });

    // The trie warmer never reads the recyclable _snapshots; it reads persistence plus the _transientResource
    // node cache (which it also warms), pinned by a per-read lease. A node that lives only in the bundle's own
    // committed snapshot list is visible to a normal read but Unknown to the warmer; a node in persistence is
    // returned by both.
    [Test]
    public void Trie_warmer_reads_persistence_only_and_ignores_recyclable_snapshots()
    {
        Hash256 storageAddress = TestItem.KeccakC;
        TreePath persistedPath = TreePath.FromHexString("ab");
        TreePath persistedStoragePath = TreePath.FromHexString("cd");
        TrieNode persistedNode = Leaf(0x80);
        TrieNode persistedStorageNode = Leaf(0x81);

        using SnapshotBundle bundle = NewBundle(content =>
        {
            content.StateNodes[persistedPath] = persistedNode;
            content.StorageNodes[(storageAddress, persistedStoragePath)] = persistedStorageNode;
        });

        TreePath committedPath = TreePath.FromHexString("12");
        TreePath committedStoragePath = TreePath.FromHexString("34");
        TrieNode committedNode = Leaf(0x82);
        TrieNode committedStorageNode = Leaf(0x83);
        bundle.SetStateNode(committedPath, committedNode);
        bundle.SetStorageNode(storageAddress, committedStoragePath, committedStorageNode);

        // Commit folds the written nodes into the bundle's recyclable _snapshots and swaps the transient away
        // from them, so afterwards they are reachable only through the path the warmer must not take.
        bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), returnSnapshot: false);

        TrieNode warmedCommittedState = bundle.FindStateNodeOrUnknownForTrieWarmer(committedPath, TestItem.KeccakA);
        TrieNode warmedCommittedStorage = bundle.FindStorageNodeOrUnknownTrieWarmer(storageAddress, committedStoragePath, TestItem.KeccakA);
        TrieNode warmedPersistedState = bundle.FindStateNodeOrUnknownForTrieWarmer(persistedPath, TestItem.KeccakB);
        TrieNode warmedPersistedStorage = bundle.FindStorageNodeOrUnknownTrieWarmer(storageAddress, persistedStoragePath, TestItem.KeccakB);

        TrieNode normalStateRead = bundle.FindStateNodeOrUnknown(committedPath, TestItem.KeccakA);
        TrieNode normalStorageRead = bundle.FindStorageNodeOrUnknown(storageAddress, committedStoragePath, TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmedCommittedState.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(warmedCommittedStorage.NodeType, Is.EqualTo(NodeType.Unknown));

            Assert.That(warmedPersistedState, Is.SameAs(persistedNode));
            Assert.That(warmedPersistedStorage, Is.SameAs(persistedStorageNode));

            Assert.That(normalStateRead, Is.SameAs(committedNode));
            Assert.That(normalStorageRead, Is.SameAs(committedStorageNode));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Unresolved_warmer_miss_does_not_reach_trie_node_cache(bool storage)
    {
        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool), cache, _pool, ResourcePool.Usage.MainBlockProcessing);

        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = TestItem.KeccakC;
        Hash256 address = TestItem.AddressA.ToAccountPath.ToCommitment();
        Hash256? cacheAddress = storage ? address : null;

        TrieNode warmed = storage
            ? bundle.FindStorageNodeOrUnknownTrieWarmer(address, path, hash)
            : bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);
        TrieNode live = storage
            ? bundle.FindStorageNodeOrUnknown(address, path, hash)
            : bundle.FindStateNodeOrUnknown(path, hash);

        (_, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA));
        cache.Add(retired!);
        retired!.ReleaseLease();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(live, Is.Not.SameAs(warmed));
            Assert.That(live.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(cache.TryGet(cacheAddress, in path, hash, out _), Is.False);
        }
    }

    // A repeated warmer miss is served by the owned placeholder, not by another persistence lookup.
    [TestCase(false)]
    [TestCase(true)]
    public void Repeated_warmer_miss_is_served_from_the_negative_cache(bool storage)
    {
        NullTrieNodeCache cache = new();
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool), cache, _pool, ResourcePool.Usage.MainBlockProcessing);

        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = TestItem.KeccakA;
        Hash256 address = TestItem.KeccakC;

        _ = storage
            ? bundle.FindStorageNodeOrUnknownTrieWarmer(address, path, hash)
            : bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);
        _ = storage
            ? bundle.FindStorageNodeOrUnknownTrieWarmer(address, path, hash)
            : bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);

        Assert.That(cache.TryGetCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Returned_transient_resource_clears_warmer_nodes(bool storage)
    {
        ResourcePool.Usage usage = ResourcePool.Usage.MainBlockProcessing;
        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = TestItem.KeccakC;
        Hash256 address = TestItem.AddressA.ToAccountPath.ToCommitment();

        TransientResource rented = _pool.GetCachedResource(usage);
        try
        {
            if (storage)
            {
                rented.GetOrAddStorageNode((Hash256AsKey)address, in path, new TrieNode(NodeType.Unknown, hash));
            }
            else
            {
                rented.GetOrAddStateNode(in path, new TrieNode(NodeType.Unknown, hash));
            }
        }
        finally
        {
            rented.ReleaseLease();
        }

        TransientResource recycled = _pool.GetCachedResource(usage);
        try
        {
            bool found = storage
                ? recycled.TryGetStorageNode((Hash256AsKey)address, in path, hash, out _)
                : recycled.TryGetStateNode(in path, hash, out _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(recycled, Is.SameAs(rented));
                Assert.That(found, Is.False);
            }
        }
        finally
        {
            recycled.ReleaseLease();
        }
    }

    // Live reads see the warmer's instance only once resolved; retirement promotes a detached copy of it.
    [TestCase(false)]
    [TestCase(true)]
    public void Resolved_warmer_node_is_reused_by_live_reads_and_promoted_detached(bool storage)
    {
        Hash256 address = TestItem.KeccakC;
        TreePath path = TreePath.FromHexString("12");
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        Hash256? cacheAddress = storage ? address : null;

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(rlp);
        reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(rlp);

        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);
        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool, reader), cache, _pool, ResourcePool.Usage.MainBlockProcessing);

        TrieNode warmed = storage
            ? bundle.FindStorageNodeOrUnknownTrieWarmer(address, path, hash)
            : bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);

        // Resolving through the warmer adapter is what issues the persistence read.
        TreePath resolvePath = path;
        Assert.That(warmed.TryResolveNode(WarmerResolver(bundle, storage ? address : null), ref resolvePath), Is.True);

        TrieNode live = storage
            ? bundle.FindStorageNodeOrUnknown(address, path, hash)
            : bundle.FindStateNodeOrUnknown(path, hash);

        (_, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA));
        cache.Add(retired!);
        retired!.ReleaseLease();

        bool found = cache.TryGet(cacheAddress, in path, hash, out TrieNode? cached);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(live.NodeType, Is.EqualTo(NodeType.Leaf));
            Assert.That(live, Is.SameAs(warmed));
            Assert.That(live.FullRlp.UnderlyingArray, Is.SameAs(warmed.FullRlp.UnderlyingArray));
            Assert.That(found, Is.True);
            Assert.That(cached, Is.Not.SameAs(live));
            Assert.That(cached!.FullRlp.UnderlyingArray, Is.SameAs(live.FullRlp.UnderlyingArray));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Concurrent_owned_warmer_resolution_loads_once(bool storage)
    {
        Hash256 address = TestItem.KeccakC;
        TreePath path = TreePath.FromHexString("12");
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        int loads = 0;

        using ManualResetEventSlim loadStarted = new(false);
        using ManualResetEventSlim allowLoad = new(false);
        byte[] Load()
        {
            Interlocked.Increment(ref loads);
            loadStarted.Set();
            if (!allowLoad.Wait(BailOutTimeout)) throw new TimeoutException("owned resolver was not released");
            return rlp;
        }

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        if (storage)
        {
            reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => Load());
        }
        else
        {
            reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => Load());
        }

        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool, reader), new NullTrieNodeCache(), _pool, ResourcePool.Usage.MainBlockProcessing);
        TrieNode warmed = storage
            ? bundle.FindStorageNodeOrUnknownTrieWarmer(address, path, hash)
            : bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);

        using ManualResetEventSlim start = new(false);
        const int resolverCount = 4;
        Task[] resolvers = new Task[resolverCount];
        for (int i = 0; i < resolvers.Length; i++)
        {
            resolvers[i] = Task.Run(() =>
            {
                start.Wait();
                TreePath resolvePath = path;
                Assert.That(warmed.TryResolveNode(WarmerResolver(bundle, storage ? address : null), ref resolvePath), Is.True);
            });
        }

        start.Set();
        bool firstLoadStarted = loadStarted.Wait(BailOutTimeout);

        // The shared instance is mid-resolution here and must stay invisible to live reads.
        TrieNode midResolution = storage
            ? bundle.FindStorageNodeOrUnknown(address, path, hash)
            : bundle.FindStateNodeOrUnknown(path, hash);

        allowLoad.Set();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstLoadStarted, Is.True, "no resolver reached persistence");
            Assert.That(midResolution, Is.Not.SameAs(warmed));
            Assert.That(midResolution.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(Task.WaitAll(resolvers, BailOutTimeout), Is.True, "owned resolvers did not complete");
            Assert.That(Volatile.Read(ref loads), Is.EqualTo(1));
        }

        if (storage) reader.Received(1).TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
        else reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void Hash_only_unknown_node_is_not_promoted()
    {
        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = TestItem.KeccakC;
        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);

        TransientResource rented = _pool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        rented.UpdateStateNode(in path, new TrieNode(NodeType.Unknown, hash));
        cache.Add(rented);
        rented.ReleaseLease();

        Assert.That(cache.TryGet(null, in path, hash, out _), Is.False);
    }

    // Retirement must not scan the transient while a warmer read that passed the lease check is still writing to it.
    [Test]
    public void Retirement_waits_for_an_in_flight_warmer_read()
    {
        TreePath path = TreePath.FromHexString("12");
        using ManualResetEventSlim readEntered = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool),
            new BlockingTrieNodeCache(readEntered, releaseRead), _pool, ResourcePool.Usage.MainBlockProcessing);

        Task<TrieNode> warmerRead = Task.Run(() => bundle.FindStateNodeOrUnknownForTrieWarmer(path, TestItem.KeccakA));
        Assert.That(readEntered.Wait(BailOutTimeout), Is.True, "the warmer read did not reach the cache");

        (_, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA));
        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);
        Task add = Task.Run(() => cache.Add(retired!));
        bool addCompletedWhileRead = add.Wait(TimeSpan.FromMilliseconds(200));
        releaseRead.Set();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(addCompletedWhileRead, Is.False, "retirement scanned the transient under an in-flight warmer read");
            Assert.That(add.Wait(BailOutTimeout), Is.True, "retirement did not resume after the warmer read released its lease");
            Assert.That(warmerRead.Wait(BailOutTimeout), Is.True);
            Assert.That(warmerRead.Result.NodeType, Is.EqualTo(NodeType.Unknown));
        }

        retired!.ReleaseLease();
    }

    [Test]
    public void Retirement_promotes_a_detached_owned_node_without_pruning_its_source()
    {
        TrieNode child = TrieNodeFactory.CreateLeaf([0x3, 0x4], new byte[32]);
        TreePath rootPath = TreePath.Empty;
        child.ResolveKey(NullTrieNodeResolver.Instance, ref rootPath);

        TrieNode branch = new(NodeType.Branch);
        branch.SetChild(0, child);
        branch.ResolveKey(NullTrieNodeResolver.Instance, ref rootPath);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(branch.FullRlp.ToArray());

        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);
        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool, reader), cache, _pool, ResourcePool.Usage.MainBlockProcessing);
        TreePath path = TreePath.FromHexString("1234");
        TrieNode source = bundle.FindStateNodeOrUnknownForTrieWarmer(path, branch.Keccak!);
        TreePath resolvePath = path;
        Assert.That(source.TryResolveNode(WarmerResolver(bundle, null), ref resolvePath), Is.True);

        TreePath childPath = path;
        source.AppendChildPath(ref childPath, 0);
        TrieNode sourceChild = source.GetChildWithChildPath(NullTrieNodeResolver.Instance, ref childPath, 0, keepChildRef: true)!;

        (_, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA));
        cache.Add(retired!);
        retired!.ReleaseLease();

        childPath = path;
        source.AppendChildPath(ref childPath, 0);
        TrieNode childAfterPromotion = source.GetChildWithChildPath(NullTrieNodeResolver.Instance, ref childPath, 0, keepChildRef: true)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(null, in path, branch.Keccak!, out TrieNode? cached), Is.True);
            Assert.That(cached, Is.Not.SameAs(source));
            Assert.That(cached!.FullRlp.UnderlyingArray, Is.SameAs(source.FullRlp.UnderlyingArray));
            Assert.That(childAfterPromotion, Is.SameAs(sourceChild));
        }
    }

    // The warmer's persistence read is path-keyed, so it can return another node; reader guards compare the node's
    // own claimed Keccak, so publishing those bytes under the requested hash would poison live reads and the cache.
    [TestCase(false)]
    [TestCase(true)]
    public void Warmer_hash_mismatch_does_not_become_resolved_or_poison_live_reads(bool storage)
    {
        Hash256 address = TestItem.KeccakC;
        TreePath path = TreePath.FromHexString("12");
        (byte[] otherRlp, Hash256 otherHash) = EncodedLeaf();
        Hash256 requestedHash = TestItem.KeccakB;
        Hash256? cacheAddress = storage ? address : null;
        Assert.That(requestedHash, Is.Not.EqualTo(otherHash));

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(otherRlp);
        reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(otherRlp);

        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);
        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool, reader), cache, _pool, ResourcePool.Usage.MainBlockProcessing);

        TrieNode warmed = storage
            ? bundle.FindStorageNodeOrUnknownTrieWarmer(address, path, requestedHash)
            : bundle.FindStateNodeOrUnknownForTrieWarmer(path, requestedHash);

        TreePath resolvePath = path;
        Assert.That(warmed.TryResolveNode(WarmerResolver(bundle, storage ? address : null), ref resolvePath), Is.False);

        if (storage) reader.Received(1).TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
        else reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());

        TrieNode live = storage
            ? bundle.FindStorageNodeOrUnknown(address, path, requestedHash)
            : bundle.FindStateNodeOrUnknown(path, requestedHash);

        (_, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA));
        cache.Add(retired!);
        retired!.ReleaseLease();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(live, Is.Not.SameAs(warmed));
            Assert.That(live.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(cache.TryGet(cacheAddress, in path, requestedHash, out _), Is.False,
                "the mismatched persistence node was promoted under the requested hash");
        }
    }

    [TestCase(false, new byte[] { 0xc2, 0x80, 0x01 })]
    [TestCase(true, new byte[] { 0xc2, 0x80, 0x01 })]
    [TestCase(false, new byte[] { 0xf8 })]
    [TestCase(true, new byte[] { 0xf8 })]
    public void Invalid_warmer_rlp_is_not_resolved_or_promoted(bool storage, byte[] invalidRlp)
    {
        Hash256 address = TestItem.KeccakC;
        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = new(ValueKeccak.Compute(invalidRlp));
        Hash256? cacheAddress = storage ? address : null;

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(invalidRlp);
        reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(invalidRlp);

        TrieNodeCache cache = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);
        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool, reader), cache, _pool, ResourcePool.Usage.MainBlockProcessing);

        TrieNode warmed = storage
            ? bundle.FindStorageNodeOrUnknownTrieWarmer(address, path, hash)
            : bundle.FindStateNodeOrUnknownForTrieWarmer(path, hash);

        TreePath resolvePath = path;
        Assert.That(warmed.TryResolveNode(WarmerResolver(bundle, storage ? address : null), ref resolvePath), Is.False);

        TrieNode live = storage
            ? bundle.FindStorageNodeOrUnknown(address, path, hash)
            : bundle.FindStateNodeOrUnknown(path, hash);

        (_, TransientResource? retired) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA));
        Assert.DoesNotThrow(() => cache.Add(retired!));
        retired!.ReleaseLease();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(live, Is.Not.SameAs(warmed));
            Assert.That(live.NodeType, Is.EqualTo(NodeType.Unknown));
            Assert.That(cache.TryGet(cacheAddress, in path, hash, out _), Is.False);
        }
    }

    private static ITrieNodeResolver WarmerResolver(SnapshotBundle bundle, Hash256? address)
    {
        StateTrieStoreWarmerAdapter state = new(bundle);
        return address is null ? state : state.GetStorageTrieNodeResolver(address);
    }

    /// <summary>Builds a leaf whose RLP is long enough to be hash-referenced rather than inlined.</summary>
    private static (byte[] Rlp, Hash256 Hash) EncodedLeaf()
    {
        TrieNode leaf = TrieNodeFactory.CreateLeaf([0x3, 0x4], new byte[32]);
        TreePath empty = TreePath.Empty;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref empty);
        return (leaf.FullRlp.ToArray()!, leaf.Keccak!);
    }

    // Dispose releases the transient back to the pool while warmer jobs may still be in flight. A read that
    // arrives after that must fall back to the leased persistence reader rather than touch the recycled
    // resource - and must not spin waiting for a transient that will never be replaced, which is what the
    // bounded wait here asserts.
    [Test]
    public void Trie_warmer_read_on_disposed_bundle_falls_back_to_persistence()
    {
        TreePath persistedPath = TreePath.FromHexString("ab");
        TrieNode persistedNode = Leaf(0x80);

        SnapshotBundle bundle = NewBundle(content => content.StateNodes[persistedPath] = persistedNode);

        Assert.That(bundle.TryLeaseReadOnlyBundle(), Is.True);
        try
        {
            bundle.Dispose();

            Task<(TrieNode Node, bool ShouldPrewarm)> read = Task.Run(() =>
                (bundle.FindStateNodeOrUnknownForTrieWarmer(persistedPath, TestItem.KeccakA),
                    bundle.ShouldQueuePrewarm(TestItem.AddressA)));

            Assert.That(read.Wait(BailOutTimeout), Is.True, "the warmer read did not return on a disposed bundle");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(read.Result.Node, Is.SameAs(persistedNode));
                Assert.That(read.Result.ShouldPrewarm, Is.False);
            }
        }
        finally
        {
            bundle.ReleaseReadOnlyBundleLease();
        }
    }

    private sealed record ChurnEpoch(SnapshotBundle Bundle, TrieNode Node);

    // Regression for the warmer recycle-under-reader race. Warmer-shaped readers hold only a
    // ReadOnlySnapshotBundle lease (as the real warmer job does) while the owner drives both recycle paths:
    // CollectAndApplySnapshot, which swaps the transient in place and retires the old one to the pool while
    // the bundle stays published, and Dispose, which releases the transient outright. Each epoch owns a
    // distinct persisted node instance, so a read served from another epoch's recycled transient is caught by
    // identity rather than by value. The prewarm dedupe bloom lives on the same resource, so it is exercised
    // here too: unpinned, its BloomFilter can be freed by a pool overflow mid-read.
    [Test]
    public void Trie_warmer_reads_survive_owner_churn_without_foreign_values()
    {
        TreePath persistedPath = TreePath.FromHexString("ab");

        ChurnEpoch NewEpoch()
        {
            TrieNode node = Leaf(0x80);
            return new ChurnEpoch(NewBundle(content => content.StateNodes[persistedPath] = node), node);
        }

        long leasedReads = 0;
        long queuedPrewarms = 0;
        long foreignReads = 0;
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
                            TrieNode node = epoch.Bundle.FindStateNodeOrUnknownForTrieWarmer(persistedPath, TestItem.KeccakA);
                            if (!ReferenceEquals(node, epoch.Node)) Interlocked.Increment(ref foreignReads);
                            Interlocked.Increment(ref leasedReads);

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
        int commits = 0;
        int disposals = 0;
        ulong blockNumber = 0;
        ChurnEpoch current = published;
        while (sw.ElapsedMilliseconds < ChurnMilliseconds && !Volatile.Read(ref stop))
        {
            StateId from = new(blockNumber, TestItem.KeccakA);
            StateId to = new(++blockNumber, TestItem.KeccakA);
            current.Bundle.CollectAndApplySnapshot(from, to, returnSnapshot: false);
            commits++;

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
            $"commits={commits} disposals={disposals} leasedReads={Volatile.Read(ref leasedReads)} " +
            $"queuedPrewarms={Volatile.Read(ref queuedPrewarms)} foreignReads={Volatile.Read(ref foreignReads)}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readersFinished, Is.True, "reader tasks did not finish");
            Assert.That(readerException, Is.Null);
            Assert.That(Volatile.Read(ref foreignReads), Is.Zero);
            // Prove both churn paths and the leased reads actually ran, so the assertions above are not vacuous.
            Assert.That(commits, Is.GreaterThan(0));
            Assert.That(disposals, Is.GreaterThan(0));
            Assert.That(Volatile.Read(ref leasedReads), Is.GreaterThan(0));
            Assert.That(Volatile.Read(ref queuedPrewarms), Is.GreaterThan(0));
        }
    }
}
