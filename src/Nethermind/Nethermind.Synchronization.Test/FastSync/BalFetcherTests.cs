// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.V2;
using Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;
using Nethermind.State.SnapServer;
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
        PeerCancellation,
        InvalidBal,
    }

    private const int BlockCount = 300;
    private const int AllocateTimeoutMs = 5;

    private readonly Dictionary<ValueHash256, byte[]> _balByHash = [];
    private int _largestRequest;
    private int _responseLimit;
    private int _disposedAllocations;
    private IContainer _container = null!;
    private CancellationTokenSource _cts = null!;
    private IBlockTree _blockTree = null!;
    private MemDb _balDb = null!;
    private BlockAccessListStore _balStore = null!;
    private ISyncPeerPool _pool = null!;
    private BalFetcher _fetcher = null!;
    private INodeStatsManager _stats = null!;

    [SetUp]
    public void SetUp()
    {
        _balByHash.Clear();
        _largestRequest = 0;
        _responseLimit = int.MaxValue;
        _disposedAllocations = 0;
        _cts = new CancellationTokenSource();
        _container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        _blockTree = _container.Resolve<IBlockTree>();
        _balDb = new MemDb();
        _balStore = new BlockAccessListStore(_balDb);
        _pool = Substitute.For<ISyncPeerPool>();
        _stats = Substitute.For<INodeStatsManager>();
        _fetcher = new BalFetcher(_pool, _blockTree, _balStore, new TestSyncConfig { SyncDispatcherAllocateTimeoutMs = AllocateTimeoutMs }, LimboLogs.Instance, _stats);
    }

    [TearDown]
    public async Task TearDown()
    {
        _balDb.Dispose();
        _cts.Dispose();
        await _pool.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Test]
    public async Task Repeated_empty_replies_rotate_to_an_honest_provider([Values] bool repeatedEmpty)
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = Block(11, [1, 2]);
        int emptyRequests = 0;
        PeerInfo emptyPeer = Snap2Peer(missing: () => ++emptyRequests == 1 || repeatedEmpty);
        PeerInfo honestPeer = Snap2Peer();
        await using SyncPeerPool penaltyPool = new(_blockTree, Substitute.For<INodeStatsManager>(),
            Substitute.For<IBetterPeerStrategy>(), LimboLogs.Instance);
        _pool.When(pool => pool.ReportNoSyncProgress(emptyPeer, AllocationContexts.BlockAccessLists)).Do(_ =>
        {
            penaltyPool.ReportNoSyncProgress(emptyPeer, AllocationContexts.BlockAccessLists);
        });
        AllocatePeer(() => emptyPeer.IsAsleep(AllocationContexts.BlockAccessLists) ? honestPeer : emptyPeer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _fetcher.EnsureRange(from, to, default), Is.True);

            Assert.That(emptyPeer.IsAsleep(AllocationContexts.BlockAccessLists), Is.EqualTo(repeatedEmpty));
            Assert.That(emptyRequests, Is.EqualTo(repeatedEmpty ? 3 : 2));
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
            Assert.That((repeatedEmpty ? honestPeer : emptyPeer).HasNoProgress(AllocationContexts.BlockAccessLists), Is.False);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Protocol_timeout_rotates_without_cancelling_the_range(
        [Values] bool cancelCaller, CancellationToken cancellationToken)
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = Block(11, [1, 2]);
        ISession session = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        session.Node.Returns(node);
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        stats.GetStatsOrNull(node).Returns(new NodeStatsLight(node));
        using Snap2ProtocolHandler protocol = new(session, stats, new MessageSerializationService(),
            RunImmediatelyScheduler.Instance, LimboLogs.Instance, new SyncConfig(), Substitute.For<ISnapServer>());
        PeerInfo silent = SnapPeer(protocol);
        PeerInfo honest = Snap2Peer();
        TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource honestRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IByteArrayList> honestReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.When(value => value.DeliverMessage(Arg.Any<GetBlockAccessListsMessage>())).Do(_ => requested.SetResult());
        honest.SyncPeer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer satellite);
        satellite.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            honestRequested.SetResult();
            return honestReply.Task;
        });
        AllocatePeer(() => silent.IsAsleep(AllocationContexts.BlockAccessLists) ? honest : silent);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task<bool> fetching = _fetcher.EnsureRange(from, to, caller.Token);
        Task first = await Task.WhenAny(requested.Task, fetching).WaitAsync(cancellationToken);
        if (first == fetching) await fetching;
        Assert.That(requested.Task.IsCompleted, Is.True, "the protocol must dispatch before the range finishes");
        await requested.Task.WaitAsync(cancellationToken);
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await fetching);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_disposedAllocations, Is.EqualTo(1));
                Assert.That(honestRequested.Task.IsCompleted, Is.False);
                Assert.That(silent.IsAsleep(AllocationContexts.BlockAccessLists), Is.False);
            }
            _pool.DidNotReceive().ReportWeakPeer(silent, AllocationContexts.BlockAccessLists);
        }
        else
        {
            await Task.WhenAny(honestRequested.Task, fetching).WaitAsync(cancellationToken);
            Assert.That(fetching.IsCompleted, Is.False);
            await honestRequested.Task.WaitAsync(cancellationToken);
            honestReply.SetResult(new ByteArrayListAdapter(new ArrayPoolList<byte[]>([[1, 2]])));
            Assert.That(await fetching, Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_disposedAllocations, Is.EqualTo(2));
                Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
                Assert.That(silent.IsAsleep(AllocationContexts.BlockAccessLists), Is.True);
            }
            _pool.Received(1).ReportWeakPeer(silent, AllocationContexts.BlockAccessLists);
            _pool.DidNotReceive().ReportNoSyncProgress(silent, AllocationContexts.BlockAccessLists);
        }
    }

    [Test]
    public async Task Timeout_after_two_empty_replies_does_not_complete_empty_evidence()
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = Block(11, [1, 2]);
        ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
        snap.SnapProtocolVersion.Returns(SnapVersions.Snap2);
        int requests = 0;
        snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++requests <= 2
                ? Task.FromResult<IByteArrayList>(EmptyByteArrayList.Instance)
                : Task.FromException<IByteArrayList>(new TaskCanceledException("Request timed out.")));
        PeerInfo provider = SnapPeer(snap);
        PeerInfo honest = Snap2Peer();
        AllocatePeer(() => provider.IsAsleep(AllocationContexts.BlockAccessLists) ? honest : provider);

        Assert.That(await _fetcher.EnsureRange(from, to, CancellationToken.None), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Is.EqualTo(3));
            Assert.That(_disposedAllocations, Is.EqualTo(4));
            Assert.That(provider.IsAsleep(AllocationContexts.BlockAccessLists), Is.True);
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
        }
        _pool.Received(1).ReportWeakPeer(provider, AllocationContexts.BlockAccessLists);
        _pool.DidNotReceive().ReportNoSyncProgress(provider, AllocationContexts.BlockAccessLists);
    }

    [Test]
    public async Task Cancellation_during_timeout_logging_preserves_the_exception_without_peer_evidence()
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = Block(11, [1, 2]);
        using CancellationTokenSource caller = new();
        OperationCanceledException timeout = new("Request timed out.");
        ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
        snap.SnapProtocolVersion.Returns(SnapVersions.Snap2);
        snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IByteArrayList>(timeout));
        PeerInfo provider = SnapPeer(snap);
        AllocatePeer(provider);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsDebug.Returns(true);
        logger.When(value => value.Debug(Arg.Any<string>())).Do(_ => caller.Cancel());
        BalFetcher fetcher = new(_pool, _blockTree, _balStore,
            new TestSyncConfig { SyncDispatcherAllocateTimeoutMs = AllocateTimeoutMs }, new OneLoggerLogManager(new ILogger(logger)));

        OperationCanceledException thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await fetcher.EnsureRange(from, to, caller.Token));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(timeout));
            Assert.That(_disposedAllocations, Is.EqualTo(1));
            Assert.That(provider.IsAsleep(AllocationContexts.BlockAccessLists), Is.False);
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.False);
        }
        _pool.DidNotReceive().ReportWeakPeer(provider, AllocationContexts.BlockAccessLists);
        _pool.DidNotReceive().ReportNoSyncProgress(provider, AllocationContexts.BlockAccessLists);
    }

    [Test]
    public async Task Transport_silence_exhausts_first_window_without_empty_evidence()
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = from;
        for (ulong number = 11; number <= 1035; number++)
            to = Block(number, [(byte)number, (byte)(number >> 8)]);
        List<PeerInfo> silentPeers = [];
        for (int i = 0; i < 50; i++) silentPeers.Add(Snap2Peer(FetchOutcome.Cancellation));
        int allocation = 0;
        AllocatePeer(() => silentPeers[allocation++]);

        Assert.That(await _fetcher.EnsureRange(from, to, default), Is.False);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_disposedAllocations, Is.EqualTo(50));
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.False);
        }
        _pool.Received(50).ReportWeakPeer(Arg.Any<PeerInfo>(), AllocationContexts.BlockAccessLists);
        _pool.DidNotReceive().ReportNoSyncProgress(Arg.Any<PeerInfo>(), AllocationContexts.BlockAccessLists);
    }

    [Test]
    public async Task Alternating_empty_providers_keep_independent_counts()
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = Block(11, [1, 2]);
        PeerInfo first = Snap2Peer(missing: () => true);
        PeerInfo second = Snap2Peer(missing: () => true);
        PeerInfo honest = Snap2Peer();
        int allocations = 0;
        AllocatePeer(() => ++allocations > 6 ? honest : allocations % 2 == 1 ? first : second);

        Assert.That(await _fetcher.EnsureRange(from, to, default), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocations, Is.EqualTo(7));
            Assert.That(first.IsAsleep(AllocationContexts.BlockAccessLists), Is.True);
            Assert.That(second.IsAsleep(AllocationContexts.BlockAccessLists), Is.True);
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
        }
        _pool.Received(1).ReportNoSyncProgress(first, AllocationContexts.BlockAccessLists);
        _pool.Received(1).ReportNoSyncProgress(second, AllocationContexts.BlockAccessLists);
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public async Task Useful_reply_resets_only_its_provider(bool sameProvider, bool otherProviderHasEmptyHistory)
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = Block(11, [1, 2]);
        int providerRequests = 0;
        PeerInfo provider = Snap2Peer(missing: () => ++providerRequests != 3 || !sameProvider);
        int honestRequests = 0;
        PeerInfo honest = Snap2Peer(missing: () => ++honestRequests == 1 && otherProviderHasEmptyHistory);
        PeerInfo[] sequence = sameProvider
            ? [provider, provider, provider, provider, provider, honest]
            : [provider, provider, honest, provider, honest];
        if (otherProviderHasEmptyHistory) sequence = [honest, .. sequence];
        int allocation = 0;
        AllocatePeer(() =>
        {
            PeerInfo next = sequence[allocation++];
            return next.IsAsleep(AllocationContexts.BlockAccessLists) ? honest : next;
        }, () =>
        {
            if (allocation == (otherProviderHasEmptyHistory ? 4 : 3)) _balStore.Delete(to.Number, to.Hash!);
        });

        Assert.That(await _fetcher.EnsureRange(from, to, default), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.IsAsleep(AllocationContexts.BlockAccessLists), Is.EqualTo(!sameProvider));
            Assert.That(honest.IsAsleep(AllocationContexts.BlockAccessLists), Is.False);
            Assert.That(providerRequests, Is.EqualTo(sameProvider ? 5 : 3));
            Assert.That(honestRequests, Is.EqualTo(sameProvider ? 1 : otherProviderHasEmptyHistory ? 3 : 2));
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
        }
        _pool.Received(sameProvider ? 0 : 1).ReportNoSyncProgress(provider, AllocationContexts.BlockAccessLists);
        _pool.DidNotReceive().ReportNoSyncProgress(honest, AllocationContexts.BlockAccessLists);
    }

    [Test]
    public async Task Useful_multiwindow_range_can_exceed_fifty_rounds()
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = from;
        for (ulong number = 11; number <= 1035; number++)
            to = Block(number, [(byte)number, (byte)(number >> 8)]);
        _responseLimit = 1;
        PeerInfo peer = Snap2Peer();
        AllocatePeer(peer);

        Assert.That(await _fetcher.EnsureRange(from, to, default), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_disposedAllocations, Is.EqualTo(1025));
            Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
        }
    }

    [Test]
    public async Task Empty_provider_history_starts_fresh_in_next_window()
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = from;
        for (ulong number = 11; number <= 1035; number++)
            to = Block(number, [(byte)number, (byte)(number >> 8)]);
        PeerInfo empty = Snap2Peer(missing: () => true);
        PeerInfo honest = Snap2Peer();
        int allocations = 0;
        AllocatePeer(() => ++allocations is 1 or 2 or 4 or 5 ? empty : honest);

        Assert.That(await _fetcher.EnsureRange(from, to, default), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocations, Is.EqualTo(6));
            Assert.That(empty.IsAsleep(AllocationContexts.BlockAccessLists), Is.False);
        }
        _pool.DidNotReceive().ReportNoSyncProgress(empty, AllocationContexts.BlockAccessLists);
    }

    [Test]
    public async Task Successive_capacity_evictions_forget_oldest_insertions()
    {
        BlockHeader from = Block(10, null);
        BlockHeader to = Block(11, [1, 2]);
        PeerInfo first = Snap2Peer(missing: () => true);
        PeerInfo second = Snap2Peer(missing: () => true);
        PeerInfo honest = Snap2Peer();
        List<PeerInfo> sequence = [first, second, first, second];
        for (int i = 0; i < 1024; i++)
        {
            sequence.Add(Snap2Peer(missing: () => true));
            if (i % 20 == 0) sequence.Add(honest);
        }
        sequence.AddRange([first, second, first, second, second, honest]);
        int allocation = 0;
        AllocatePeer(() => sequence[allocation++], () =>
        {
            if (allocation < sequence.Count) _balStore.Delete(to.Number, to.Hash!);
        });

        Assert.That(await _fetcher.EnsureRange(from, to, default), Is.True);

        _pool.DidNotReceive().ReportNoSyncProgress(first, AllocationContexts.BlockAccessLists);
        _pool.Received(1).ReportNoSyncProgress(second, AllocationContexts.BlockAccessLists);
        Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.True);
    }

    [Test]
    public async Task Fetches_missing_bals_via_snap2()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        BlockHeader to = Block(12, [0x03, 0x04]);
        AllocatePeer(Snap2Peer());

        bool result = await _fetcher.EnsureRange(from, to, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
            Assert.That(_balStore.Exists(12, to.Hash!), Is.True);
        }
    }

    [Test]
    public async Task Falls_back_to_eth71_when_peer_has_no_snap2()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        AllocatePeer(Eth71Peer());

        bool result = await _fetcher.EnsureRange(from, b11, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
        }
    }

    [Test]
    public async Task Returns_false_when_no_capable_peer()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        _pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SyncPeerAllocation(AllocationContexts.State));

        bool result = await _fetcher.EnsureRange(from, b11, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.False);
        }

        await _pool.Received().Allocate(Arg.Any<IPeerAllocationStrategy>(), AllocationContexts.State, AllocateTimeoutMs, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Disposes_allocation_after_fetch([Values(FetchOutcome.Success, FetchOutcome.Failure, FetchOutcome.Cancellation, FetchOutcome.PeerCancellation)] FetchOutcome outcome)
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        using CancellationTokenSource callerCancellation = new();
        PeerInfo peer = Snap2Peer(outcome, cancel: callerCancellation);
        AllocatePeer(peer);

        if (outcome == FetchOutcome.Cancellation)
        {
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await _fetcher.EnsureRange(from, b11, callerCancellation.Token));
        }
        else
        {
            bool result = await _fetcher.EnsureRange(from, b11, _cts.Token);
            Assert.That(result, Is.EqualTo(outcome == FetchOutcome.Success));
        }

        using (Assert.EnterMultipleScope())
        {
            int expectedDisposals = outcome is FetchOutcome.Failure or FetchOutcome.PeerCancellation ? 50 : 1;
            Assert.That(_disposedAllocations, Is.EqualTo(expectedDisposals));
            Assert.That(peer.IsAllocated(AllocationContexts.State), Is.False);
        }
    }

    [Test]
    public async Task Reports_peer_by_fault([Values(FetchOutcome.InvalidBal, FetchOutcome.Failure, FetchOutcome.PeerCancellation)] FetchOutcome outcome)
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
        _stats.DidNotReceiveWithAnyArgs().ReportTransferSpeedEvent(default!, default, default);
        if (outcome == FetchOutcome.InvalidBal)
            _pool.Received().ReportBreachOfProtocol(peer, DisconnectReason.InvalidTxOrUncle, Arg.Any<string>());
        else
            _pool.Received().ReportWeakPeer(peer, AllocationContexts.BlockAccessLists);
    }

    [Test]
    public async Task Repeated_unavailable_responses_report_no_progress_without_protocol_penalty()
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
        _pool.Received(48).ReportNoSyncProgress(Arg.Any<PeerInfo>(), AllocationContexts.BlockAccessLists);
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
        PeerInfo peer = Snap2Peer();
        AllocatePeer(peer);

        bool result = await _fetcher.EnsureRange(from, b12, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
            Assert.That(_balStore.Exists(12, b12.Hash!), Is.True);
        }

        _stats.Received(2).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.BlockAccessLists, Arg.Any<long>());
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

        _blockTree.Insert(header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        if (bal is not null) _balByHash[header.Hash!.ValueHash256] = bal;
        return header;
    }

    private void AllocatePeer(PeerInfo peer) => AllocatePeer(() => peer);

    private void AllocatePeer(Func<PeerInfo> peer, Action? onDispose = null) =>
        _pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                AllocationContexts contexts = ci.Arg<AllocationContexts>();
                SyncPeerAllocation allocation = new(contexts, null, () =>
                {
                    _disposedAllocations++;
                    onDispose?.Invoke();
                });
                allocation.AllocatePeer(peer());
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
                ArrayPoolList<byte[]> response = [with(requested.Count)];
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

    private PeerInfo Snap2Peer(FetchOutcome outcome = FetchOutcome.Success, Func<bool>? missing = null, CancellationTokenSource? cancel = null)
    {
        ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
        snap.SnapProtocolVersion.Returns(SnapVersions.Snap2);
        snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (outcome == FetchOutcome.Failure) throw new InvalidOperationException("fetch failed");
                if (outcome == FetchOutcome.Cancellation)
                {
                    (cancel ?? _cts).Cancel();
                    return Task.FromCanceled<IByteArrayList>((cancel ?? _cts).Token);
                }
                if (missing?.Invoke() == true) return Task.FromResult<IByteArrayList>(EmptyByteArrayList.Instance);
                if (outcome == FetchOutcome.PeerCancellation) throw new TaskCanceledException();

                IReadOnlyList<ValueHash256> requested = ci.Arg<IReadOnlyList<ValueHash256>>();
                _largestRequest = Math.Max(_largestRequest, requested.Count);
                int served = Math.Min(requested.Count, _responseLimit);
                ArrayPoolList<byte[]> response = [with(served)];
                // An invalid BAL is garbage that won't hash to the header's BlockAccessListHash.
                for (int i = 0; i < served; i++) response.Add(outcome == FetchOutcome.InvalidBal ? [0xDE, 0xAD] : _balByHash.GetValueOrDefault(requested[i], []));
                return Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(response));
            });

        return SnapPeer(snap);
    }

    private static PeerInfo SnapPeer(ISnapSyncPeer snap)
    {
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
                ArrayPoolList<byte[]?> response = [with(requested.Count)];
                foreach (Hash256 hash in requested)
                    response.Add(_balByHash.TryGetValue(hash.ValueHash256, out byte[]? rlp) ? rlp : null);
                return Task.FromResult<IOwnedReadOnlyList<byte[]?>>(response);
            });
        return new PeerInfo(syncPeer);
    }

    [Test, CancelAfter(30_000)]
    public async Task Empty_bal_reply_is_neutral_until_repeated_and_only_rotates_bal_work(
        [Values] bool repeatedEmpty, [Values] bool snap2, CancellationToken cancellationToken)
    {
        await using BalQualityContext context = new(snap2);
        ISyncPeer provider = await context.AddPeer(200, true);
        context.ConfigureProvider(provider);
        Assert.That(context.Pool.GetPeer(provider.Node)?.ServedHeadNumber, Is.EqualTo(200UL));
        PeerInfo info = context.Pool.GetPeer(provider.Node)!;
        Task<bool> fetching = context.Fetcher.EnsureRange(context.From, context.To, cancellationToken);
        bool completed = false;
        ISyncPeer lower;
        try
        {
            await context.SecondRequested.Task.WaitAsync(cancellationToken);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(info.HasNoProgress(AllocationContexts.All), Is.False);
                Assert.That(info.HasWeakness(AllocationContexts.State | AllocationContexts.BlockAccessLists), Is.False);
            }
            lower = await context.AddPeer(100, false);
            Assert.That(context.Pool.GetPeer(lower.Node)?.ServedHeadNumber, Is.EqualTo(100UL));
            context.Pool.DropUselessPeers(true);
            lower.Received(1).Disconnect(DisconnectReason.DropWorstPeer, Arg.Any<string>());
            provider.DidNotReceive().Disconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        finally
        {
            context.SecondReply.SetResult(repeatedEmpty ? IOwnedReadOnlyList<byte[]?>.Empty : context.ValidReply());
            completed = await fetching;
        }

        context.Pool.DropUselessPeers(true);
        lower.Received(2).Disconnect(DisconnectReason.DropWorstPeer, Arg.Any<string>());
        provider.DidNotReceive().Disconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.True);
            Assert.That(context.Store.Exists(context.To.Number, context.To.Hash!), Is.True);
            Assert.That(context.ProviderRequests, Is.EqualTo(repeatedEmpty ? 3 : 2));
            Assert.That(context.HonestRequests, Is.EqualTo(repeatedEmpty ? 1 : 0));
            Assert.That(info.HasNoProgress(AllocationContexts.State | AllocationContexts.Headers), Is.False);
            Assert.That(info.HasNoProgress(AllocationContexts.BlockAccessLists), Is.EqualTo(repeatedEmpty));
            Assert.That(info.IsAsleep(AllocationContexts.BlockAccessLists), Is.EqualTo(repeatedEmpty));
            Assert.That(info.IsAsleep(AllocationContexts.State), Is.False);
        }
    }

    private sealed class BalQualityContext : IAsyncDisposable
    {
        private readonly MemDb _balDb = new();
        private readonly IContainer _node;
        private readonly byte[] _validBal = [1, 2];
        private int _peerIndex;
        private readonly bool _snap2;
        public IBlockTree Tree { get; }
        public BlockHeader From { get; }
        public BlockHeader To { get; }
        public BlockAccessListStore Store { get; }
        public SyncPeerPool Pool { get; }
        public BalFetcher Fetcher { get; }
        public TaskCompletionSource SecondRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IOwnedReadOnlyList<byte[]?>> SecondReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProviderRequests;
        public int HonestRequests;

        public BalQualityContext(bool snap2)
        {
            _snap2 = snap2;
            _node = SyncPeerPoolTests.CreateNode();
            Tree = _node.Resolve<IBlockTree>();
            Block genesis = Build.A.Block.Genesis.TestObject;
            Tree.SuggestBlock(genesis);
            From = genesis.Header;
            To = Build.A.BlockHeader.WithNumber(1).WithParentHash(From.Hash!).WithBlockAccessListHash(Keccak.Compute(_validBal)).TestObject;
            Tree.Insert(To);
            Store = new(_balDb);
            Pool = _node.Resolve<SyncPeerPool>();
            Fetcher = new(Pool, Tree, Store, new TestSyncConfig { SyncDispatcherAllocateTimeoutMs = 1000 }, LimboLogs.Instance);
            Pool.Start();
        }

        public async Task<ISyncPeer> AddPeer(ulong headNumber, bool balCapable)
        {
            int index = _peerIndex++;
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.Node.Returns(new Node(TestItem.PublicKeys[index], "127.0.0.1", 30303 + index));
            peer.ProtocolVersion.Returns(balCapable && !_snap2 ? (byte)71 : (byte)69);
            if (balCapable && _snap2)
            {
                ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
                snap.SnapProtocolVersion.Returns(SnapVersions.Snap2);
                peer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>())
                    .Returns(call => { call[1] = snap; return true; });
            }
            BlockHeader header = Build.A.BlockHeader.WithNumber(headNumber).TestObject;
            Tree.Insert(header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
            peer.HeadNumber = headNumber;
            peer.HeadHash = header.Hash!;
            peer.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<BlockHeader?>(header));
            Pool.AddPeer(peer);
            Pool.UpdateSyncPeerHeadIfHeaderIsBetter(peer, (await peer.GetHeadBlockHeader(header.Hash!, default))!, Keccak.Zero);
            return peer;
        }

        public void ConfigureProvider(ISyncPeer provider) => ConfigureReply(provider, async () =>
        {
            int request = Interlocked.Increment(ref ProviderRequests);
            if (request == 1) return IOwnedReadOnlyList<byte[]?>.Empty;
            if (request == 2)
            {
                SecondRequested.SetResult();
                return await SecondReply.Task;
            }
            if (request != 3) throw new InvalidOperationException("The empty provider must rotate after three replies.");
            ISyncPeer honest = await AddPeer(201, true);
            ConfigureReply(honest, () =>
            {
                Interlocked.Increment(ref HonestRequests);
                return Task.FromResult(ValidReply());
            });
            return IOwnedReadOnlyList<byte[]?>.Empty;
        });

        private static void ConfigureReply(ISyncPeer peer, Func<Task<IOwnedReadOnlyList<byte[]?>>> reply)
        {
            peer.GetBlockAccessLists(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>()).Returns(_ => reply());
            if (peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer snap))
                snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
                    .Returns(async _ => (IByteArrayList)new ByteArrayListAdapter((await reply())!));
        }

        public IOwnedReadOnlyList<byte[]?> ValidReply() => new ArrayPoolList<byte[]?>([_validBal]);

        public async ValueTask DisposeAsync()
        {
            await _node.DisposeAsync();
            _balDb.Dispose();
        }
    }
}
