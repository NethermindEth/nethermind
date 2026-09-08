// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
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
    public void Shared_session_only_publishes_verified_nodes(
        [Values] bool storage, [Values("valid", "mismatched", "invalid", "missing")] string response)
    {
        (byte[] encodedRlp, Hash256 hash) = EncodedLeaf();
        byte[]? rlp = encodedRlp;
        if (response == "mismatched") hash = TestItem.KeccakA;
        if (response == "invalid")
        {
            rlp = Bytes.FromHexString("f8");
            hash = new Hash256(ValueKeccak.Compute(rlp));
        }
        if (response == "missing") rlp = null;

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
                Assert.That(live.NodeType, Is.EqualTo(response == "valid" ? NodeType.Leaf : NodeType.Unknown));
                Assert.That(cached, Is.EqualTo(response == "valid"));
                if (cached)
                {
                    Assert.That(cachedNode, Is.Not.SameAs(live));
                    Assert.That(cachedNode!.FullRlp.UnderlyingArray, Is.SameAs(live.FullRlp.UnderlyingArray));
                }
            }
        }
        finally
        {
            retired.ReleaseLease();
        }
    }

    [Test]
    public void Warmer_miss_does_not_hide_valid_live_snapshot([Values] bool storage)
    {
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        TrieNode committed = new(NodeType.Unknown, hash, rlp);
        TreePath path = TreePath.Empty;
        committed.ResolveNode(NullTrieNodeResolver.Instance, path);
        using SessionContext context = new(storage, hash, null, committed: committed);

        context.Hint();
        Assert.That(context.CompleteJob(), Is.True);
        Assert.That(context.FindLiveNode(hash), Is.SameAs(committed));
    }

    [Test]
    public async Task Concurrent_session_traversals_resolve_once_without_publishing_partial_nodes([Values] bool storage)
    {
        (byte[] rlp, Hash256 hash) = EncodedLeaf();
        using ManualResetEventSlim readEntered = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        int loads = 0;
        using SessionContext context = new(storage, hash, rlp, () =>
        {
            Interlocked.Increment(ref loads);
            readEntered.Set();
            if (!releaseRead.Wait(BailOutTimeout)) throw new TimeoutException("session read was not released");
        });
        context.Hint();
        Task<bool>[] traversals = new Task<bool>[4];
        for (int i = 0; i < traversals.Length; i++) traversals[i] = Task.Run(context.CompleteJob);
        try
        {
            Assert.That(readEntered.Wait(BailOutTimeout), Is.True);
            Assert.That(context.FindLiveNode(hash).NodeType, Is.EqualTo(NodeType.Unknown));
        }
        finally
        {
            releaseRead.Set();
            await Task.WhenAll(traversals).WaitAsync(BailOutTimeout);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loads, Is.EqualTo(1));
            Assert.That(context.FindLiveNode(hash).NodeType, Is.EqualTo(NodeType.Leaf));
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

        public SessionContext(bool storage, Hash256 hash, byte[]? rlp, Action? onRead = null, TrieNode? committed = null)
        {
            _storage = storage;
            ResourcePool pool = new(new FlatDbConfig());
            IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
            reader.GetAccount(Arg.Any<Address>()).Returns(new Account(0, UInt256.Zero, hash, Keccak.OfAnEmptyString));
            byte[]? Load()
            {
                onRead?.Invoke();
                return rlp;
            }
            reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => Load());
            reader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => Load());
            SnapshotPooledList? snapshots = committed is null ? null : FlatTestHelpers.SnapshotList(FlatTestHelpers.MakeSnapshot(pool, content =>
            {
                if (storage) content.StorageNodes[(AddressHash, TreePath.Empty)] = committed;
                else content.StateNodes[TreePath.Empty] = committed;
            }));
            Bundle = new SnapshotBundle(FlatTestHelpers.MakeBundle(pool, reader), Cache, pool, ResourcePool.Usage.MainBlockProcessing, snapshots);
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
