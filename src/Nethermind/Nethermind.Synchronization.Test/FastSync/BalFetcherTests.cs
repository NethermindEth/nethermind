// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Stats.SyncLimits;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class BalFetcherTests
{
    public enum FetchOutcome
    {
        Success,
        Failure,
        Cancellation,
        InvalidBal,
    }

    private const int BlockCount = 300;
    private const int AllocateTimeoutMs = 5;

    private readonly Dictionary<ValueHash256, byte[]> _balByHash = [];
    private int _largestRequest;
    private int _responseLimit;
    private int _disposedAllocations;
    private IBlockTree _blockTree = null!;
    private MemDb _balDb = null!;
    private BlockAccessListStore _balStore = null!;
    private ISyncPeerPool _pool = null!;
    private BalFetcher _fetcher = null!;

    [SetUp]
    public void SetUp()
    {
        _balByHash.Clear();
        _largestRequest = 0;
        _responseLimit = int.MaxValue;
        _disposedAllocations = 0;
        _blockTree = Substitute.For<IBlockTree>();
        _balDb = new MemDb();
        _balStore = new BlockAccessListStore(_balDb);
        _pool = Substitute.For<ISyncPeerPool>();
        _fetcher = new BalFetcher(_pool, _blockTree, _balStore, new TestSyncConfig { SyncDispatcherAllocateTimeoutMs = AllocateTimeoutMs }, LimboLogs.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _balDb.Dispose();
        await _pool.DisposeAsync();
    }

    [Test]
    public async Task Fetches_missing_bals_via_snap2()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        BlockHeader to = Block(12, [0x03, 0x04]);
        AllocatePeer(Snap2Peer());

        bool result = await _fetcher.EnsureRange(from, to, default);

        Assert.That(result, Is.True);
        Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
        Assert.That(_balStore.Exists(12, to.Hash!), Is.True);
    }

    [Test]
    public async Task Falls_back_to_eth71_when_peer_has_no_snap2()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        AllocatePeer(Eth71Peer());

        bool result = await _fetcher.EnsureRange(from, b11, default);

        Assert.That(result, Is.True);
        Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
    }

    [Test]
    public async Task Returns_false_when_no_capable_peer()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        _pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SyncPeerAllocation(AllocationContexts.State));

        bool result = await _fetcher.EnsureRange(from, b11, default);

        Assert.That(result, Is.False);
        Assert.That(_balStore.Exists(11, b11.Hash!), Is.False);

        await _pool.Received().Allocate(Arg.Any<IPeerAllocationStrategy>(), AllocationContexts.State, AllocateTimeoutMs, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Disposes_allocation_after_fetch([Values(FetchOutcome.Success, FetchOutcome.Failure, FetchOutcome.Cancellation)] FetchOutcome outcome)
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        PeerInfo peer = Snap2Peer(outcome);
        AllocatePeer(peer);

        if (outcome == FetchOutcome.Cancellation)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await _fetcher.EnsureRange(from, b11, default));
        }
        else
        {
            bool result = await _fetcher.EnsureRange(from, b11, default);
            Assert.That(result, Is.EqualTo(outcome == FetchOutcome.Success));
        }

        using (Assert.EnterMultipleScope())
        {
            int expectedDisposals = outcome == FetchOutcome.Failure ? 50 : 1;
            Assert.That(_disposedAllocations, Is.EqualTo(expectedDisposals));
            Assert.That(peer.IsAllocated(AllocationContexts.State), Is.False);
        }
    }

    [Test]
    public async Task Reports_peer_by_fault([Values(FetchOutcome.InvalidBal, FetchOutcome.Failure)] FetchOutcome outcome)
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        PeerInfo peer = Snap2Peer(outcome);
        AllocatePeer(peer);

        bool result = await _fetcher.EnsureRange(from, b11, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.False);
        }

        // A BAL not matching the header it was requested by is provable misbehaviour; a failed request is not.
        if (outcome == FetchOutcome.InvalidBal)
        {
            _pool.Received().ReportBreachOfProtocol(peer, DisconnectReason.InvalidTxOrUncle, Arg.Any<string>());
            _pool.DidNotReceive().ReportWeakPeer(Arg.Any<PeerInfo>(), Arg.Any<AllocationContexts>());
        }
        else
        {
            _pool.Received().ReportWeakPeer(peer, AllocationContexts.State);
            _pool.DidNotReceive().ReportBreachOfProtocol(Arg.Any<PeerInfo>(), Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test]
    public async Task Does_not_report_a_peer_that_simply_has_no_bals()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        // The peer answers correctly, with an empty entry for a BAL it does not have.
        _balByHash.Remove(b11.Hash!.ValueHash256);
        AllocatePeer(Snap2Peer());

        bool result = await _fetcher.EnsureRange(from, b11, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.False);
        }

        _pool.DidNotReceive().ReportWeakPeer(Arg.Any<PeerInfo>(), Arg.Any<AllocationContexts>());
        _pool.DidNotReceive().ReportBreachOfProtocol(Arg.Any<PeerInfo>(), Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task Moves_past_peers_that_answer_empty()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        Dictionary<PeerInfo, int> asked = [];
        PeerInfo[] empty = [CountingPeer(asked, serves: false), CountingPeer(asked, serves: false), CountingPeer(asked, serves: false)];
        PeerInfo serving = CountingPeer(asked, serves: true);
        AllocateFrom([.. empty, serving]);

        bool result = await _fetcher.EnsureRange(from, b11, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
            foreach (PeerInfo peer in empty) Assert.That(asked.GetValueOrDefault(peer), Is.LessThanOrEqualTo(1));
        }

        _pool.DidNotReceive().ReportWeakPeer(Arg.Any<PeerInfo>(), Arg.Any<AllocationContexts>());
    }

    [Test]
    public async Task Asks_peers_that_answered_empty_again_once_all_have()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        Dictionary<PeerInfo, int> asked = [];
        PeerInfo[] empty = [CountingPeer(asked, serves: false), CountingPeer(asked, serves: false)];
        AllocateFrom(empty);

        bool result = await _fetcher.EnsureRange(from, b11, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            foreach (PeerInfo peer in empty) Assert.That(asked.GetValueOrDefault(peer), Is.GreaterThan(1));
        }
    }

    [Test]
    public async Task Retried_range_still_skips_peers_that_answered_empty_at_a_later_window()
    {
        // Window 1 (blocks 11..1034) is already stored; window 2 (block 1035) stalls.
        BlockHeader from = Block(10, bal: null);
        for (ulong number = 11; number <= 1034; number++)
        {
            byte[] bal = [(byte)number, (byte)(number >> 8)];
            BlockHeader header = Block(number, bal);
            _balStore.Insert(number, header.Hash!, bal);
        }
        BlockHeader to = Block(1035, [0x01, 0x02]);

        Dictionary<PeerInfo, int> asked = [];
        using CancellationTokenSource cts = new();
        PeerInfo[] empty = [CountingPeer(asked, serves: false, onAsked: Cancel), CountingPeer(asked, serves: false, onAsked: Cancel)];
        AllocateFrom(empty);
        await Assert.ThatAsync(() => _fetcher.EnsureRange(from, to, cts.Token), Throws.InstanceOf<OperationCanceledException>());

        PeerInfo serving = CountingPeer(asked, serves: true);
        AllocateFrom([.. empty, serving]);
        bool result = await _fetcher.EnsureRange(from, to, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
            Assert.That(asked.GetValueOrDefault(serving), Is.EqualTo(1));
            foreach (PeerInfo peer in empty) Assert.That(asked.GetValueOrDefault(peer), Is.EqualTo(1));
        }

        // Stop the first call once every peer has answered empty, before the set resets.
        void Cancel()
        {
            if (asked.Count == 2) cts.Cancel();
        }
    }

    [Test]
    public async Task Stores_what_a_short_response_carries()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        BlockHeader b12 = Block(12, [0x03, 0x04]);
        // A peer may answer with fewer entries than it was asked for.
        _responseLimit = 1;
        AllocatePeer(Snap2Peer());

        bool result = await _fetcher.EnsureRange(from, b12, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
            Assert.That(_balStore.Exists(12, b12.Hash!), Is.True);
        }

        _pool.DidNotReceive().ReportWeakPeer(Arg.Any<PeerInfo>(), Arg.Any<AllocationContexts>());
    }

    [Test]
    public async Task Stores_the_present_entries_of_a_partly_empty_response()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        BlockHeader b12 = Block(12, [0x03, 0x04]);
        // The peer holds the first BAL and answers empty for the second.
        _balByHash.Remove(b12.Hash!.ValueHash256);
        AllocatePeer(Snap2Peer());

        bool result = await _fetcher.EnsureRange(from, b12, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
            Assert.That(_balStore.Exists(12, b12.Hash!), Is.False);
        }

        _pool.DidNotReceive().ReportWeakPeer(Arg.Any<PeerInfo>(), Arg.Any<AllocationContexts>());
    }

    [Test]
    public async Task Does_not_fetch_when_all_bals_already_present()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        _balStore.Insert(11, b11.Hash!, _balByHash[b11.Hash!.ValueHash256]);

        bool result = await _fetcher.EnsureRange(from, b11, default);

        Assert.That(result, Is.True);
        await _pool.DidNotReceive().Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [TestCase(true, BlockCount, TestName = "snap/2 requests the whole window at once")]
    [TestCase(false, GethSyncLimits.MaxBodyFetch, TestName = "eth/71 stays within its request cap")]
    public async Task Sizes_requests_to_the_protocol_limit(bool snap2, int expectedLargestRequest)
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader to = null!;
        for (ulong number = 11; number <= 10 + BlockCount; number++)
            to = Block(number, [(byte)number, (byte)(number >> 8)]);
        AllocatePeer(snap2 ? Snap2Peer() : Eth71Peer());

        bool result = await _fetcher.EnsureRange(from, to, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(_largestRequest, Is.EqualTo(expectedLargestRequest));
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
        }
    }

    // Registers a header at `number` and (when it has a BAL) records the RLP a peer should return for it.
    private BlockHeader Block(ulong number, byte[]? bal)
    {
        BlockHeaderBuilder builder = Build.A.BlockHeader.WithNumber(number);
        if (bal is not null) builder = builder.WithBlockAccessListHash(Keccak.Compute(bal));
        BlockHeader header = builder.TestObject;

        _blockTree.FindHeader(number).Returns(header);
        if (bal is not null) _balByHash[header.Hash!.ValueHash256] = bal;
        return header;
    }

    private void AllocatePeer(PeerInfo peer) =>
        _pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                AllocationContexts contexts = ci.Arg<AllocationContexts>();
                SyncPeerAllocation allocation = new(contexts, null, () => _disposedAllocations++);
                allocation.AllocatePeer(peer);
                return allocation;
            });

    // Lets the strategy choose among `peers`, as the pool would.
    private void AllocateFrom(PeerInfo[] peers) =>
        _pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                SyncPeerAllocation allocation = new(ci.Arg<AllocationContexts>(), null, () => _disposedAllocations++);
                PeerInfo? chosen = ci.Arg<IPeerAllocationStrategy>().Allocate(null, peers, Substitute.For<INodeStatsManager>(), _blockTree);
                if (chosen is not null) allocation.AllocatePeer(chosen);
                return allocation;
            });

    // A snap/2 peer that serves every known BAL, or answers empty for all of them, counting the requests it gets.
    private PeerInfo CountingPeer(Dictionary<PeerInfo, int> asked, bool serves, Action? onAsked = null)
    {
        PeerInfo peer = null!;
        ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
        snap.SnapProtocolVersion.Returns(SnapVersions.Snap2);
        snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                asked[peer] = asked.GetValueOrDefault(peer) + 1;
                IReadOnlyList<ValueHash256> requested = ci.Arg<IReadOnlyList<ValueHash256>>();
                ArrayPoolList<byte[]> response = new(requested.Count);
                foreach (ValueHash256 hash in requested) response.Add(serves ? _balByHash.GetValueOrDefault(hash, []) : []);
                onAsked?.Invoke();
                return Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(response));
            });

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>())
            .Returns(ci => { ci[1] = snap; return true; });
        syncPeer.IsInitialized.Returns(true);
        peer = new PeerInfo(syncPeer);
        return peer;
    }

    private PeerInfo Snap2Peer(FetchOutcome outcome = FetchOutcome.Success)
    {
        ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
        snap.SnapProtocolVersion.Returns(SnapVersions.Snap2);
        snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (outcome == FetchOutcome.Failure) throw new InvalidOperationException("fetch failed");
                if (outcome == FetchOutcome.Cancellation) throw new OperationCanceledException();

                IReadOnlyList<ValueHash256> requested = ci.Arg<IReadOnlyList<ValueHash256>>();
                _largestRequest = Math.Max(_largestRequest, requested.Count);
                int served = Math.Min(requested.Count, _responseLimit);
                ArrayPoolList<byte[]> response = new(served);
                // An invalid BAL is garbage that won't hash to the header's BlockAccessListHash.
                for (int i = 0; i < served; i++) response.Add(outcome == FetchOutcome.InvalidBal ? [0xDE, 0xAD] : _balByHash.GetValueOrDefault(requested[i], []));
                return Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(response));
            });

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>())
            .Returns(ci => { ci[1] = snap; return true; });
        return new PeerInfo(syncPeer);
    }

    private PeerInfo Eth71Peer()
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(EthVersions.Eth71);
        syncPeer.GetBlockAccessLists(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<Hash256> requested = ci.Arg<IReadOnlyList<Hash256>>();
                _largestRequest = Math.Max(_largestRequest, requested.Count);
                ArrayPoolList<byte[]?> response = new(requested.Count);
                foreach (Hash256 hash in requested)
                    response.Add(_balByHash.TryGetValue(hash.ValueHash256, out byte[]? rlp) ? rlp : null);
                return Task.FromResult<IOwnedReadOnlyList<byte[]?>>(response);
            });
        return new PeerInfo(syncPeer);
    }
}
