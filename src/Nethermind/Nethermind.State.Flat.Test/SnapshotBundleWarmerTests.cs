// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleWarmerTests
{
    private static readonly TimeSpan BailOutTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public void Shared_session_promotes_the_same_nodes([Values] bool storage)
    {
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        using SessionContext context = new(storage, hash, rlp);
        context.Hint();
        Assert.That(context.CompleteJob(), Is.True);
        TrieNode live = context.FindLiveNode(hash);
        TransientResource retired = Retire(context.Bundle);
        try
        {
            context.Cache.Add(retired);
            bool cached = context.Cache.TryGet(context.CacheAddress, TreePath.Empty, hash, out TrieNode? cachedNode);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(live.NodeType, Is.EqualTo(NodeType.Leaf));
                Assert.That(cached, Is.True);
                Assert.That(cachedNode, Is.SameAs(live));
            }
        }
        finally
        {
            retired.ReleaseLease();
        }
    }

    [Test]
    public void Initial_overlay_supplies_session_root_and_storage_account([Values] bool storage)
    {
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        TrieNode committed = new(NodeType.Unknown, hash, rlp);
        TreePath path = TreePath.Empty;
        committed.ResolveNode(NullTrieNodeResolver.Instance, path);
        using SessionContext context = new(storage, hash, null, committed: committed);

        context.Hint();
        Assert.That(context.CompleteJob(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.FindLiveNode(hash), Is.SameAs(committed));
            Assert.That(context.TrieLoads, Is.Zero);
        }
    }

    [Test]
    public void Newest_snapshot_node_takes_priority_over_a_cached_unknown([Values] bool storage)
    {
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        TrieNode committed = new(NodeType.Unknown, hash, rlp);
        committed.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        using SessionContext context = new(storage, hash, rlp);
        TransientResource cacheEntries = context.Pool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        try
        {
            TrieNode unknown = new(NodeType.Unknown, hash);
            if (storage) cacheEntries.UpdateStorageNode(context.AddressHash, TreePath.Empty, unknown);
            else cacheEntries.UpdateStateNode(TreePath.Empty, unknown);
            context.Cache.Add(cacheEntries);
        }
        finally
        {
            cacheEntries.ReleaseLease();
        }

        Snapshot snapshot = FlatTestHelpers.MakeSnapshot(context.Pool, content =>
        {
            if (storage) content.StorageNodes[(context.AddressHash, TreePath.Empty)] = committed;
            else content.StateNodes[TreePath.Empty] = committed;
        });
        context.Bundle._snapshots.Add(snapshot);

        Assert.That(context.FindLiveNode(hash), Is.SameAs(committed));
    }

    [Test]
    public async Task Concurrent_session_traversals_read_the_same_initial_state([Values] bool storage)
    {
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        using SessionContext context = new(storage, hash, rlp);
        context.Hint();
        Task<bool>[] traversals = new Task<bool>[4];
        for (int i = 0; i < traversals.Length; i++) traversals[i] = Task.Run(context.CompleteJob);
        bool[] results = await Task.WhenAll(traversals).WaitAsync(BailOutTimeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.All.True);
            Assert.That(context.FindLiveNode(hash).FullRlp.ToArray(), Is.EqualTo(rlp));
        }
    }

    [Test]
    public async Task Retirement_drains_active_session_without_waiting_for_borrowers([Values] bool storage)
    {
        using ManualResetEventSlim readEntered = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        using ManualResetEventSlim retirementStarted = new(false);
        using SessionContext context = new(storage, TestItem.KeccakA, null, () =>
        {
            readEntered.Set();
            if (!releaseRead.Wait(BailOutTimeout)) throw new TimeoutException("session read was not released");
        });
        context.Hint();
        Task<bool> warmup = Task.Run(context.CompleteJob);
        Task<TransientResource>? retirement = null;
        try
        {
            Assert.That(readEntered.Wait(BailOutTimeout), Is.True);
            retirement = Task.Run(() =>
            {
                retirementStarted.Set();
                return Retire(context.Bundle);
            });
            Assert.That(retirementStarted.Wait(BailOutTimeout), Is.True);
            Assert.That(retirement.Wait(TimeSpan.FromMilliseconds(200)), Is.False);
        }
        finally
        {
            releaseRead.Set();
            await warmup.WaitAsync(BailOutTimeout);
            if (retirement is not null)
            {
                TransientResource retired = await retirement.WaitAsync(BailOutTimeout);
                try
                {
                    context.Cache.Add(retired);
                }
                finally
                {
                    retired.ReleaseLease();
                }
            }
        }

        Assert.That(context.CompleteJob(), Is.False, "retired sessions reject queued jobs while borrowers remain alive");
    }

    [Test]
    public void Session_and_live_reader_share_a_parent_from_the_initial_overlay(
        [Values] bool storage, [Values] bool iterator)
    {
        Hash256 address = TestItem.AddressA.ToAccountPath.ToHash256();
        ValueHash256 key = TestItem.AddressA.ToAccountPath;
        if (storage) StorageTree.ComputeKeyWithLookup(UInt256.Zero, ref key);
        int childIndex = key.BytesAsSpan[0] >> 4;
        TreePath rootPath = TreePath.Empty;
        TreePath childPath = rootPath.Append(childIndex);
        TrieNode child = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("0304"), new byte[33]);
        child.ResolveKey(NullTrieNodeResolver.Instance, ref childPath);
        child.Seal();
        TrieNode branch = new(NodeType.Branch);
        branch.SetChild(childIndex, child);
        branch.ResolveKey(NullTrieNodeResolver.Instance, ref rootPath);
        TrieNode sharedParent = new(NodeType.Unknown, branch.Keccak!, branch.FullRlp);
        sharedParent.ResolveNode(NullTrieNodeResolver.Instance, rootPath);

        using SessionContext context = new(storage, branch.Keccak!, null, committed: child, committedPath: childPath,
            populateOverlay: content =>
            {
                if (storage) content.StorageNodes[(address, rootPath)] = sharedParent;
                else content.StateNodes[rootPath] = sharedParent;
            });
        context.Hint();
        Assert.That(context.CompleteJob(), Is.True);
        Assert.That(context.TrieLoads, Is.Zero);

        StateTrieStoreAdapter state = new(context.Bundle, new ConcurrencyController(1));
        ITrieNodeResolver live = storage ? state.GetStorageTrieNodeResolver(address) : state;
        TrieNode liveParent = live.FindCachedOrUnknown(rootPath, branch.Keccak!);
        Assert.That(liveParent, Is.SameAs(sharedParent));
        TrieNode? liveChild = iterator
            ? liveParent.CreateChildIterator().GetChildWithChildPath(live, ref childPath, childIndex)
            : liveParent.GetChildWithChildPath(live, ref childPath, childIndex);
        Assert.That(liveChild, Is.SameAs(child));
    }

    private static TransientResource Retire(SnapshotBundle bundle)
    {
        (Snapshot? snapshot, TransientResource? retired) = bundle.CollectAndApplySnapshot(
            StateId.PreGenesis, new StateId(1, TestItem.KeccakA));
        snapshot?.Dispose();
        return retired!;
    }

    private static (byte[] Rlp, Hash256 Hash) EncodedLeaf()
    {
        TrieNode leaf = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("0304"), new byte[32]);
        TreePath empty = TreePath.Empty;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref empty);
        return (leaf.FullRlp.ToArray()!, leaf.Keccak!);
    }

    private sealed class SessionContext : IDisposable
    {
        private readonly bool _storage;
        private readonly IWorldStateScopeProvider.ITrieWarmupSession _session;
        private readonly ITrieWarmer _warmer = Substitute.For<ITrieWarmer>();
        private ITrieWarmer.IStorageWarmer? _storageWarmer;
        private int _sequenceId;

        public Hash256 AddressHash { get; } = TestItem.AddressA.ToAccountPath.ToHash256();
        public Hash256? CacheAddress => _storage ? AddressHash : null;
        public TrieNodeCache Cache { get; } = new(new FlatDbConfig { TrieCacheMemoryBudget = MemorySizes.MiB }, LimboLogs.Instance);
        public SnapshotBundle Bundle { get; }
        public ResourcePool Pool { get; } = new(new FlatDbConfig());
        public int TrieLoads;

        public SessionContext(bool storage, Hash256 hash, byte[]? rlp, Action? onRead = null, TrieNode? committed = null,
            TreePath committedPath = default, Action<SnapshotContent>? populateOverlay = null)
        {
            _storage = storage;
            IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
            reader.GetAccount(Arg.Any<Address>()).Returns(new Account(0, UInt256.Zero, committed is null ? hash : Keccak.EmptyTreeHash, Keccak.OfAnEmptyString));
            byte[]? Load()
            {
                Interlocked.Increment(ref TrieLoads);
                onRead?.Invoke();
                return rlp;
            }
            reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => Load());
            reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => Load());
            SnapshotPooledList? snapshots = committed is null ? null : FlatTestHelpers.SnapshotList(FlatTestHelpers.MakeSnapshot(Pool, content =>
            {
                content.Accounts[TestItem.AddressA] = new Account(0, UInt256.Zero, hash, Keccak.OfAnEmptyString);
                if (storage) content.StorageNodes[(AddressHash, committedPath)] = committed;
                else content.StateNodes[committedPath] = committed;
                populateOverlay?.Invoke(content);
            }));
            Bundle = new SnapshotBundle(FlatTestHelpers.MakeBundle(Pool, reader), Cache, Pool, ResourcePool.Usage.MainBlockProcessing, snapshots);
            _warmer.PushAddressJob(Arg.Any<ITrieWarmer.IAddressWarmer>(), Arg.Any<Address>(), Arg.Do<int>(id => _sequenceId = id)).Returns(true);
            _warmer.PushSlotJobMpmc(Arg.Do<ITrieWarmer.IStorageWarmer>(warmer => _storageWarmer = warmer),
                Arg.Any<UInt256>(), Arg.Do<int>(id => _sequenceId = id)).Returns(true);
            _session = Bundle.CreateTrieWarmupSession(new StateId(0, hash), _warmer, LimboLogs.Instance);
        }

        public void Hint()
        {
            if (_storage) _session.HintWarmSlot(new ValueAddress(TestItem.AddressA.Bytes), UInt256.Zero);
            else _session.HintWarmAccount(new ValueAddress(TestItem.AddressA.Bytes));
        }

        public TrieNode FindLiveNode(Hash256 hash) => _storage
            ? Bundle.FindStorageNodeOrUnknown(AddressHash, TreePath.Empty, hash)
            : Bundle.FindStateNodeOrUnknown(TreePath.Empty, hash);

        public bool CompleteJob() => _storage
            ? _storageWarmer!.WarmUpStorageTrie(UInt256.Zero, _sequenceId)
            : ((ITrieWarmer.IAddressWarmer)_session).WarmUpStateTrie(TestItem.AddressA, _sequenceId);

        public void Dispose()
        {
            Bundle.Dispose();
            _session.Dispose();
        }
    }
}
