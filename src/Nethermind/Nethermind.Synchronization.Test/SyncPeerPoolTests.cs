// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.Test.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;

using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Timers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Db;
using Nethermind.History;
using Nethermind.Network.Config;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.TxPool;
using Nethermind.Specs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Test.Mocks;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Synchronization.Test;

[Parallelizable(ParallelScope.All)]
public class SyncPeerPoolTests
{
    private class Context : IAsyncDisposable
    {
        public INodeStatsManager Stats { get; }
        public IBlockTree BlockTree { get; }
        public IBetterPeerStrategy PeerStrategy { get; }
        public SyncPeerPool Pool { get; set; }

        public Context()
        {
            BlockTree = Substitute.For<IBlockTree>();
            Stats = Substitute.For<INodeStatsManager>();
            Stats.GetStatsOrNull(Arg.Any<Node>()).Returns(call => Stats.GetOrAdd(call.Arg<Node>()));
            PeerStrategy = new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance);
            Pool = new SyncPeerPool(BlockTree, Stats, PeerStrategy, LimboLogs.Instance, 25, 50);
        }

        public async ValueTask DisposeAsync() =>
            await Pool.DisposeAsync();
    }

    private class SimpleSyncPeerMock(PublicKey publicKey, string description = "simple mock") : ISyncPeer
    {
        public string Name => "SimpleMock";

        private static readonly BlockHeader DefaultHeader = Build.A.BlockHeader.TestObject;
        private readonly Lock _headLock = new();
        private Hash256 _headHash = DefaultHeader.Hash!;
        private ulong _headNumber;
        public Hash256 HeadHash
        {
            get { lock (_headLock) return _headHash; }
            set { lock (_headLock) _headHash = value; }
        }
        public byte ProtocolVersion { get; set; }
        public string ProtocolCode { get; } = null!;
        public Node Node { get; } = new Node(publicKey, "127.0.0.1", 30303);
        public string ClientId { get; } = description;
        public ulong HeadNumber
        {
            get { lock (_headLock) return _headNumber; }
            set { lock (_headLock) _headNumber = value; }
        }
        public void SetHead(Hash256 hash, ulong number)
        {
            lock (_headLock) (_headHash, _headNumber) = (hash, number);
        }
        public bool TrySetHead(Hash256 expectedHash, ulong expectedNumber, Hash256 hash, ulong number)
        {
            lock (_headLock)
            {
                if (_headHash != expectedHash || _headNumber != expectedNumber) return false;
                (_headHash, _headNumber) = (hash, number);
                return true;
            }
        }
        public Func<Hash256?, CancellationToken, Task<BlockHeader?>>? HeaderReply { get; set; }
        public UInt256? TotalDifficulty { get; set; } = 1;
        public bool IsInitialized { get; set; }
        public bool IsPriority { get; set; }

        public bool DisconnectRequested { get; private set; }

        public void Disconnect(DisconnectReason reason, string details) =>
            DisconnectRequested = true;

        public Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token) =>
            Task.FromResult(new OwnedBlockBodies([]));

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(ulong number, int maxBlocks, int skip, CancellationToken token) =>
            Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(ArrayPoolList<BlockHeader>.Empty());

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token) =>
            _headersByHashFailure is not null ? Task.FromException<IOwnedReadOnlyList<BlockHeader>?>(_headersByHashFailure)
                : Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(
                    AnswersWithNullHeader ? [with([null!])]
                        : HeaderToReturn is null ? ArrayPoolList<BlockHeader>.Empty()
                            : [with([HeaderToReturn])]);

        /// <summary>
        /// Header this peer answers header requests with, whatever hash was asked for. When unset, head-header
        /// requests are answered with an arbitrary header and by-hash requests with an empty list.
        /// </summary>
        public BlockHeader? HeaderToReturn { get; set; }

        /// <summary>
        /// Makes the peer answer head-header requests with nothing, as a peer that does not have the block does.
        /// </summary>
        public bool HeadHeaderUnavailable { get; set; }

        /// <summary>
        /// Makes the peer answer with a single null header, which is how an empty list item decodes off the wire.
        /// </summary>
        public bool AnswersWithNullHeader { get; set; }

        public async Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            if (HeaderReply is not null) return await HeaderReply(hash, token);
            if (_headHeaderFailure is not null)
            {
                throw _headHeaderFailure;
            }

            if (_shouldTimeout)
            {
                throw new TimeoutException("Timed out");
            }

            if (_headerResponseTime.HasValue)
            {
                await Task.Delay(_headerResponseTime.Value);
            }

            IsInitialized = true;
            return await Task.FromResult(HeadHeaderUnavailable ? null : HeaderToReturn ?? DefaultHeader);
        }

        public void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
        }

        public PublicKey Id => Node.Id;

        public void SendNewTransactions(ReadOnlySpan<Transaction> txs, bool sendFullTx) { }

        public Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token) =>
            Task.FromResult<IOwnedReadOnlyList<TxReceipt[]?>>(ArrayPoolList<TxReceipt[]?>.Empty());

        public Task<IByteArrayList> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token) =>
            Task.FromResult<IByteArrayList>(EmptyByteArrayList.Instance);

        private int? _headerResponseTime;

        private Exception? _headHeaderFailure;

        private Exception? _headersByHashFailure;

        private bool _shouldTimeout;

        public void SetHeaderResponseTime(int responseTime)
        {
            if (responseTime > 5000)
            {
                _shouldTimeout = true;
            }

            _headerResponseTime = responseTime;
        }

        /// <summary>
        /// Makes the peer fail by-hash header requests with <paramref name="failure"/>, and head-header requests
        /// too unless <paramref name="byHashOnly"/> is set.
        /// </summary>
        public void SetHeaderFailure(Exception? failure, bool byHashOnly = false)
        {
            _headHeaderFailure = byHashOnly ? null : failure;
            _headersByHashFailure = failure;
        }

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class =>
            throw new NotImplementedException();

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class =>
            throw new NotImplementedException();
    }

    // Peer wake-up from shallow sleep is time-based and does not signal waiting allocations,
    // so the retry delay must stay bounded or a long-peerless node reacts arbitrarily late.
    [TestCase(1, 10)]
    [TestCase(50, 500)]
    [TestCase(100, 1000)]
    [TestCase(10_000, 1000)]
    [TestCase(int.MaxValue, 1000)]
    public void Allocate_retry_backoff_is_capped(int tryCount, int expectedWaitTime) =>
        Assert.That(SyncPeerPool.GetAllocationWaitTime(tryCount), Is.EqualTo(expectedWaitTime));

    [Test]
    public async Task Can_remove_peer_whose_refresh_token_is_already_disposed()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        SimpleSyncPeerMock peer = new(TestItem.PublicKeyA);
        peer.SetHeaderResponseTime(5000);
        ctx.Pool.AddPeer(peer);

        // The refresh continuation disposes the source concurrently with RemovePeer's cancel;
        // model the lost race by planting an already-disposed source. The slow header response
        // parks the real refresh so nothing removes the planted entry.
        CancellationTokenSource disposedSource = new();
        disposedSource.Dispose();
        Assert.That(
            () => ctx.Pool.TryReplaceRefreshCancellation(peer.Node.Id, disposedSource),
            Is.True.After(10_000, 10),
            "guard: the refresh must register its cancellation source first");
        Assert.That(ctx.Pool.RefreshCancellationIs(peer.Node.Id, disposedSource), Is.True,
            "guard: the planted source must still be registered when RemovePeer runs, or the cancel path is never exercised");

        Assert.That(() => ctx.Pool.RemovePeer(peer), Throws.Nothing);
    }

    [Test]
    public void Default_head_update_preserves_custom_peer_updates([Values] bool matchingHash, [Values] bool matchingNumber)
    {
        SimpleSyncPeerMock inner = new(TestItem.PublicKeyA);
        inner.SetHead(TestItem.KeccakA, 7);
        ISyncPeer peer = new DefaultHeadPeer(inner);
        bool changed = peer.TrySetHead(matchingHash ? TestItem.KeccakA : TestItem.KeccakB,
            matchingNumber ? 7UL : 6UL, TestItem.KeccakC, 8);
        bool expected = matchingHash && matchingNumber;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed, Is.EqualTo(expected));
            Assert.That((peer.HeadHash, peer.HeadNumber),
                Is.EqualTo(expected ? (TestItem.KeccakC, 8UL) : (TestItem.KeccakA, 7UL)));
        }
    }

    [TestCase(0, false, true)]
    [TestCase(9, false, true)]
    [TestCase(10, false, false)]
    [TestCase(0, true, true)]
    [TestCase(9, true, true)]
    [TestCase(10, true, true)]
    public void Stored_head_newcomer_has_bounded_served_height_grace(
        int ageSeconds, bool noProgress, bool dropIncumbent)
    {
        ManualTimeProvider clock = new();
        using IContainer node = CreateNode(clock);
        IBlockTree tree = node.Resolve<IBlockTree>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        tree.Insert(header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        SimpleSyncPeerMock incumbent = new(TestItem.PublicKeyA) { IsInitialized = true };
        SimpleSyncPeerMock newcomer = new(TestItem.PublicKeyB) { IsInitialized = true };
        incumbent.SetHead(header.Hash!, header.Number);
        newcomer.SetHead(header.Hash!, header.Number);
        pool.Start();
        pool.AddPeer(incumbent);
        pool.UpdateSyncPeerHeadIfHeaderIsBetter(incumbent, header, Keccak.Zero);
        PeerInfo incumbentInfo = pool.GetPeer(incumbent.Node)!;
        if (noProgress) pool.ReportNoSyncProgress(incumbentInfo, AllocationContexts.Headers);
        else pool.ReportRefreshFailed(incumbent, "timeout", new TimeoutException());
        clock.Advance(TimeSpan.FromSeconds(10));
        pool.AddPeer(newcomer);
        PeerInfo newcomerInfo = pool.GetPeer(newcomer.Node)!;
        Assert.That(newcomerInfo.ServedHeadNumber, Is.Zero, "an announcement is not delivery credit");
        clock.Advance(TimeSpan.FromSeconds(ageSeconds));

        pool.DropUselessPeers(true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(incumbent.DisconnectRequested, Is.EqualTo(dropIncumbent));
            Assert.That(newcomer.DisconnectRequested, Is.EqualTo(!dropIncumbent));
            Assert.That(newcomerInfo.ServedHeadNumber, Is.Zero);
        }
    }

    private sealed class DefaultHeadPeer(ISyncPeer inner) : ISyncPeer
    {
        public Node Node => inner.Node;
        public string Name => inner.Name;
        public PublicKey Id => inner.Id;
        public Hash256 HeadHash { get => inner.HeadHash; set => inner.HeadHash = value; }
        public ulong HeadNumber { get => inner.HeadNumber; set => inner.HeadNumber = value; }
        public UInt256? TotalDifficulty { get => inner.TotalDifficulty; set => inner.TotalDifficulty = value; }
        public bool IsInitialized { get => inner.IsInitialized; set => inner.IsInitialized = value; }
        public bool IsPriority { get => inner.IsPriority; set => inner.IsPriority = value; }
        public byte ProtocolVersion => inner.ProtocolVersion;
        public string ProtocolCode => inner.ProtocolCode;
        public void Disconnect(DisconnectReason reason, string details) => inner.Disconnect(reason, details);
        public Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> hashes, CancellationToken token) => inner.GetBlockBodies(hashes, token);
        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(ulong number, int count, int skip, CancellationToken token) => inner.GetBlockHeaders(number, count, skip, token);
        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 hash, int count, int skip, CancellationToken token) => inner.GetBlockHeaders(hash, count, skip, token);
        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token) => inner.GetHeadBlockHeader(hash, token);
        public void NotifyOfNewBlock(Block block, SendBlockMode mode) => inner.NotifyOfNewBlock(block, mode);
        public Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> hashes, CancellationToken token) => inner.GetReceipts(hashes, token);
        public Task<IByteArrayList> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token) => inner.GetNodeData(hashes, token);
        public void SendNewTransactions(ReadOnlySpan<Transaction> transactions, bool full) => inner.SendNewTransactions(transactions, full);
        public void RegisterSatelliteProtocol<T>(string protocol, T handler) where T : class => inner.RegisterSatelliteProtocol(protocol, handler);
        public bool TryGetSatelliteProtocol<T>(string protocol, out T handler) where T : class => inner.TryGetSatelliteProtocol(protocol, out handler);
    }

    [Test]
    public async Task Cannot_add_when_not_started()
    {
        await using Context ctx = new();
        for (int i = 0; i < 3; i++)
        {
            Assert.That(ctx.Pool.PeerCount, Is.EqualTo(0));
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeys[i]));
        }
    }

    [Test]
    public async Task Will_disconnect_one_when_at_max()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 25);
        await WaitForPeersInitialization(ctx);
        ctx.Pool.DropUselessPeers(true);
        Assert.That(peers.Any(static p => p.DisconnectRequested), Is.True);
    }

    [Test, CancelAfter(30000)]
    public async Task Anchored_honest_peer_keeps_its_slot_over_an_extreme_unserved_claim([Values] bool hasTotalDifficulty, CancellationToken token)
    {
        ManualTimeProvider clock = new();
        TaskCompletionSource refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ILogManager logs = ObserveRefreshCompletion(() => refreshed);
        using IContainer node = SyncPeerPoolTests.CreateNode(clock, logs);
        IBlockTree tree = node.Resolve<IBlockTree>();
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        INodeStatsManager stats = node.Resolve<INodeStatsManager>();
        BlockHeader honestHeader = Build.A.BlockHeader.WithNumber(100).TestObject;
        tree.Insert(honestHeader, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        BlockHeader inventedHeader = Build.A.BlockHeader.WithNumber(100000).WithParentHash(TestItem.KeccakB).TestObject;
        ISyncPeer honest = Substitute.For<ISyncPeer>();
        ISyncPeer liar = Substitute.For<ISyncPeer>();
        honest.IsInitialized.Returns(true);
        liar.IsInitialized.Returns(true);
        honest.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        liar.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303));
        honest.HeadHash = honestHeader.Hash!;
        honest.HeadNumber = honestHeader.Number;
        honest.TotalDifficulty = hasTotalDifficulty ? new UInt256(100) : null;
        liar.HeadHash = inventedHeader.Hash!;
        liar.HeadNumber = ulong.MaxValue;
        liar.TotalDifficulty = hasTotalDifficulty ? UInt256.MaxValue : null;
        honest.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<BlockHeader?>(honestHeader));
        liar.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<BlockHeader?>(inventedHeader));
        stats.GetOrAdd(honest.Node).AddTransferSpeedCaptureEvent(TransferSpeedType.Latency, 100);
        stats.GetOrAdd(liar.Node).AddTransferSpeedCaptureEvent(TransferSpeedType.Latency, 1);
        pool.Start();
        pool.AddPeer(honest);
        pool.UpdateSyncPeerHeadIfHeaderIsBetter(honest, (await honest.GetHeadBlockHeader(honest.HeadHash, default))!, Keccak.Zero);
        pool.DropUselessPeers(true);
        pool.AddPeer(liar);
        await refreshed.Task.WaitAsync(token);
        Assert.That(pool.InitializedPeersCount, Is.EqualTo(2));
        PeerInfo liarInfo = pool.GetPeer(liar.Node)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(liarInfo.ServedHeadNumber, Is.Zero);
            Assert.That(liarInfo.HeadNumber, Is.EqualTo(tree.BestKnownNumber + 1024));
            Assert.That(liar.HeadNumber, Is.EqualTo(ulong.MaxValue), "the raw claim is retained independently of the scheduling horizon");
            Assert.That(liarInfo.HasNoProgress(AllocationContexts.Headers), Is.False, "serving an unknown head is not an allocated empty reply");
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        pool.DropUselessPeers(true);

        liar.Received(1).Disconnect(DisconnectReason.DropWorstPeer, Arg.Any<string>());
        honest.DidNotReceive().Disconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test, CancelAfter(30000)]
    public async Task Evicts_unproductive_peer_despite_extreme_claim_and_fast_ping([Values] bool hasTotalDifficulty,
        CancellationToken token)
    {
        using IContainer node = SyncPeerPoolTests.CreateNode();
        IBlockTree tree = node.Resolve<IBlockTree>();
        BlockHeader reference = Build.A.BlockHeader.WithNumber(100).TestObject;
        tree.Insert(reference, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        INodeStatsManager stats = node.Resolve<INodeStatsManager>();
        ISyncPeer honest = Substitute.For<ISyncPeer>();
        ISyncPeer liar = Substitute.For<ISyncPeer>();
        honest.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        liar.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303));
        honest.IsInitialized.Returns(true);
        liar.IsInitialized.Returns(true);
        honest.HeadHash = reference.Hash!;
        honest.HeadNumber = 100;
        liar.HeadHash = reference.Hash!;
        liar.HeadNumber = ulong.MaxValue;
        honest.TotalDifficulty = hasTotalDifficulty ? new UInt256(100) : null;
        liar.TotalDifficulty = hasTotalDifficulty ? UInt256.MaxValue : null;
        liar.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IOwnedReadOnlyList<BlockHeader>?>(new TimeoutException()));
        stats.GetOrAdd(honest.Node).AddTransferSpeedCaptureEvent(TransferSpeedType.Latency, 100);
        stats.GetOrAdd(liar.Node).AddTransferSpeedCaptureEvent(TransferSpeedType.Latency, 1);
        pool.Start();
        pool.AddPeer(liar);
        PeerInfo liarInfo = pool.GetPeer(liar.Node)!;
        using HeadersSyncBatch request = new() { StartNumber = 100, RequestSize = 1 };
        await SyncDispatcherTests.DispatchOnce(request, new HeadersSyncDownloader(LimboLogs.Instance), pool,
            AllocationContexts.Headers, token);
        pool.AddPeer(honest);
        PeerInfo honestInfo = pool.GetPeer(honest.Node)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(liarInfo.ServedHeadNumber, Is.Zero);
            Assert.That(honestInfo.ServedHeadNumber, Is.Zero);
            Assert.That(liarInfo.HasWeakness(AllocationContexts.Headers), Is.True);
            Assert.That(liarInfo.HasNoProgress(AllocationContexts.Headers), Is.False);
            Assert.That(honestInfo.HasWeakness(AllocationContexts.Headers), Is.False);
        }

        pool.DropUselessPeers(true);

        liar.Received(1).Disconnect(DisconnectReason.DropWorstPeer, Arg.Any<string>());
        honest.DidNotReceive().Disconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public void Conditional_pool_head_writers_preserve_a_concurrent_protocol_announcement(
        [Values] bool refresh, [Values] bool interleave, [Values] bool higherNumber)
    {
        using IContainer node = SyncPeerPoolTests.CreateNode();
        IBlockTree tree = node.Resolve<IBlockTree>();
        BlockHeader candidate = Build.A.BlockHeader.WithNumber(11).WithTotalDifficulty(100).TestObject;
        tree.Insert(candidate, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        using SyncPeerPoolTests.HeadRacePeer remote = new(candidate.Hash!, 10);
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        pool.Start();
        if (refresh)
        {
            pool.AddPeer(remote.Peer);
            remote.Protocol.SetHead(TestItem.KeccakA, 10);
        }
        if (interleave)
            remote.OnNumberRead = () => remote.Protocol.SetHead(TestItem.KeccakB, higherNumber ? 20UL : 10UL);

        if (refresh) pool.UpdateSyncPeerHeadIfHeaderIsBetter(remote.Peer, candidate, Keccak.Zero);
        else pool.AddPeer(remote.Peer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(remote.Protocol.HeadHash, Is.EqualTo(interleave ? TestItem.KeccakB : candidate.Hash));
            Assert.That(remote.Protocol.HeadNumber, Is.EqualTo(interleave ? higherNumber ? 20UL : 10UL : 11UL));
        }
    }

    [Test]
    public async Task Equal_served_height_does_not_give_a_relayed_extreme_claim_an_eviction_lead()
    {
        using IContainer node = SyncPeerPoolTests.CreateNode();
        IBlockTree tree = node.Resolve<IBlockTree>();
        BlockHeader known = Build.A.BlockHeader.WithNumber(100).TestObject;
        tree.Insert(known, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        INodeStatsManager stats = node.Resolve<INodeStatsManager>();
        ISyncPeer honest = Substitute.For<ISyncPeer>();
        ISyncPeer relayedClaim = Substitute.For<ISyncPeer>();
        honest.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        relayedClaim.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303));
        pool.Start();
        foreach (ISyncPeer remote in new[] { honest, relayedClaim })
        {
            remote.HeadHash = known.Hash!;
            remote.HeadNumber = ReferenceEquals(remote, honest) ? known.Number : ulong.MaxValue;
            remote.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<BlockHeader?>(known));
            pool.AddPeer(remote);
            pool.UpdateSyncPeerHeadIfHeaderIsBetter(remote, (await remote.GetHeadBlockHeader(known.Hash!, default))!, Keccak.Zero);
        }
        stats.GetOrAdd(honest.Node).AddTransferSpeedCaptureEvent(TransferSpeedType.Latency, 1);
        stats.GetOrAdd(relayedClaim.Node).AddTransferSpeedCaptureEvent(TransferSpeedType.Latency, 100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.GetPeer(honest.Node)!.ServedHeadNumber, Is.EqualTo(100UL));
            Assert.That(pool.GetPeer(relayedClaim.Node)!.ServedHeadNumber, Is.EqualTo(100UL));
            Assert.That(pool.GetPeer(relayedClaim.Node)!.HeadNumber, Is.EqualTo(1124UL));
        }
        pool.DropUselessPeers(true);
        using (Assert.EnterMultipleScope())
        {
            relayedClaim.Received(1).Disconnect(DisconnectReason.DropWorstPeer, Arg.Any<string>());
            honest.DidNotReceive().Disconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    [Test, CancelAfter(30000)]
    public async Task Failed_head_refresh_cannot_demote_a_newer_announcement([Range(0, 2)] int newerClaim, CancellationToken token)
    {
        ManualTimeProvider clock = new();
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ILogManager logManager = ObserveRefreshCompletion(() => completed);
        using IContainer node = SyncPeerPoolTests.CreateNode(clock, logManager);
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        BlockHeader known = Build.A.BlockHeader.WithNumber(100).TestObject;
        node.Resolve<IBlockTree>().Insert(known, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        using SyncPeerPoolTests.HeadRacePeer remote = new(known.Hash!, known.Number);
        pool.Start();
        pool.AddPeer(remote.Peer);
        pool.UpdateSyncPeerHeadIfHeaderIsBetter(remote.Peer, known, Keccak.Zero);
        PeerInfo peer = pool.GetPeer(remote.Peer.Node)!;
        remote.Protocol.SetHead(TestItem.KeccakA, 1000);
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            if (attempt == 4) clock.Advance(TimeSpan.FromMinutes(4));
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<BlockHeader?> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.Peer.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                requested.TrySetResult();
                return reply.Task.WaitAsync(call.ArgAt<CancellationToken>(1));
            });
            pool.RefreshTotalDifficulty(remote.Peer, remote.Peer.HeadHash);
            try
            {
                await requested.Task.WaitAsync(token);
                if (attempt == 3 && newerClaim != 0)
                    remote.Protocol.SetHead(TestItem.KeccakB, newerClaim == 1 ? 2000UL : 1000UL);
                clock.AdvanceAndFireTimer(Timeouts.Eth);
                await completed.Task.WaitAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.EqualTo(attempt >= 3));
                    Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.True);
                    Assert.That(remote.Protocol.HeadNumber, Is.EqualTo(attempt < 3 ? 1000UL
                        : attempt == 4 || newerClaim == 0 ? 100UL : newerClaim == 1 ? 2000UL : 1000UL));
                    Assert.That(remote.Protocol.HeadHash, Is.EqualTo(attempt < 3 || newerClaim == 0 ? TestItem.KeccakA : TestItem.KeccakB));
                    Assert.That(peer.ServedHeadNumber, Is.EqualTo(100UL));
                    Assert.That(peer.HasWeakness(AllocationContexts.Bodies | AllocationContexts.State), Is.False);
                }
            }
            finally
            {
                reply.TrySetCanceled();
            }
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.True, "a later real timeout renews the evidence lifetime");
        clock.Advance(TimeSpan.FromMinutes(4));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.False);
            Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.False);
        }
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task Will_disconnect_when_refresh_exception_is_not_cancelled(bool isExceptionOperationCanceled, bool isDisconnectRequested)
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 25);
        SimpleSyncPeerMock peer = peers[0];

        Exception refreshException = isExceptionOperationCanceled ? new OperationCanceledException() : new Exception();
        ctx.Pool.ReportRefreshFailed(peer, "test with cancellation", refreshException);
        Assert.That(peer.DisconnectRequested, Is.EqualTo(isDisconnectRequested));
    }

    [Test]
    public async Task Wrapped_refresh_failures_preserve_classification([Values] bool genuineFault)
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 1);
        Exception exception = genuineFault ? new InvalidOperationException() : new TimeoutException();
        ctx.Pool.ReportRefreshFailed(peers[0], "wrapped", new AggregateException(
            new OperationCanceledException(), new AggregateException(exception)));
        Assert.That(peers[0].DisconnectRequested, Is.EqualTo(genuineFault));
    }

    [Test]
    public async Task Refresh_hints_coalesce_and_keep_the_newest_hash([Values] bool removePeer)
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Hash256?> nextHash = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        CancellationToken requestToken = default;
        BlockHeader header = Build.A.BlockHeader.TestObject;
        peer.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                requestToken = call.Arg<CancellationToken>();
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(requestToken);
            }
            else nextHash.TrySetResult(call.Arg<Hash256?>());
            return (BlockHeader?)header;
        });
        ctx.Pool.AddPeer(peer);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 100; i++) ctx.Pool.RefreshTotalDifficulty(peer, TestItem.KeccakA);
        ctx.Pool.RefreshTotalDifficulty(peer, TestItem.KeccakB);

        try
        {
            if (removePeer)
            {
                ctx.Pool.RemovePeer(peer);
                Assert.That(requestToken.IsCancellationRequested, Is.True);
                ctx.Pool.RefreshTotalDifficulty(peer, TestItem.KeccakC);
            }
            Assert.That(Volatile.Read(ref requests), Is.EqualTo(1));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        if (!removePeer)
        {
            Assert.That(await nextHash.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(TestItem.KeccakB));
        }
        await ctx.Pool.DisposeAsync();
        Assert.That(Volatile.Read(ref requests), Is.EqualTo(removePeer ? 1 : 2));
    }

    [Test]
    public async Task Old_refresh_completion_cannot_remove_replacement_cancellation()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        ISyncPeer oldPeer = Substitute.For<ISyncPeer>();
        ISyncPeer newPeer = Substitute.For<ISyncPeer>();
        Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        oldPeer.Node.Returns(node);
        newPeer.Node.Returns(node);
        TaskCompletionSource oldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource newStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<BlockHeader?> oldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<BlockHeader?> newResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken newToken = default;
        oldPeer.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            oldStarted.TrySetResult();
            return oldResponse.Task;
        });
        newPeer.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            newToken = call.Arg<CancellationToken>();
            newStarted.TrySetResult();
            return newResponse.Task;
        });
        ctx.Pool.AddPeer(oldPeer);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ctx.Pool.RemovePeer(oldPeer);
        ctx.Pool.AddPeer(newPeer);
        await newStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        oldResponse.SetResult(Build.A.BlockHeader.TestObject);
        ctx.Pool.RemovePeer(newPeer);
        Assert.That(newToken.IsCancellationRequested, Is.True);
        newResponse.TrySetCanceled();
    }

    [Test]
    public async Task Allocates_honest_alternative_to_unproductive_highest_td_peer([Values] bool allPeersFailed)
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 2);
        peers[0].TotalDifficulty = UInt256.MaxValue;
        peers[1].TotalDifficulty = 100;
        PeerInfo liar = ctx.Pool.InitializedPeers.Single(p => p.SyncPeer == peers[0]);
        PeerInfo honest = ctx.Pool.InitializedPeers.Single(p => p.SyncPeer == peers[1]);
        ctx.Pool.ReportNoSyncProgress(liar, AllocationContexts.ForwardHeader);
        if (allPeersFailed) ctx.Pool.ReportNoSyncProgress(honest, AllocationContexts.ForwardHeader);

        using SyncPeerAllocation allocation = await ctx.Pool.Allocate(
            new ByTotalDifficultyPeerAllocationStrategy(null), AllocationContexts.ForwardHeader);

        Assert.That(allocation.Current, Is.SameAs(allPeersFailed ? liar : honest));
    }

    [Test]
    public async Task Will_not_disconnect_any_priority_peer_if_their_amount_is_lower_than_max([Values(0, 10, 24)] byte number)
    {
        const int peersMaxCount = 25;
        const int priorityPeersMaxCount = 25;
        await using Context ctx = new();
        ctx.Pool = new SyncPeerPool(ctx.BlockTree, ctx.Stats, ctx.PeerStrategy, LimboLogs.Instance, peersMaxCount, priorityPeersMaxCount, 50);
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, peersMaxCount);

        // setting priority to all peers except one - peers[number]
        for (int i = 0; i < priorityPeersMaxCount; i++)
        {
            if (i != number)
            {
                ctx.Pool.SetPeerPriority(peers[i].Id);
            }
        }
        await WaitForPeersInitialization(ctx);
        ctx.Pool.DropUselessPeers(true);
        Assert.That(peers[number].DisconnectRequested, Is.True);
    }

    [Test]
    public async Task Can_disconnect_priority_peer_if_their_amount_is_max()
    {
        const int peersMaxCount = 25;
        const int priorityPeersMaxCount = 25;
        await using Context ctx = new();
        ctx.Pool = new SyncPeerPool(ctx.BlockTree, ctx.Stats, ctx.PeerStrategy, LimboLogs.Instance, peersMaxCount, priorityPeersMaxCount, 50);
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, peersMaxCount);

        foreach (SimpleSyncPeerMock peer in peers)
        {
            ctx.Pool.SetPeerPriority(peer.Id);
        }
        await WaitForPeersInitialization(ctx);
        ctx.Pool.DropUselessPeers(true);
        Assert.That(peers.Any(static p => p.DisconnectRequested), Is.True);
    }

    // Neither static nor trusted peers are dropped by worst-peer eviction; only plain peers are.
    [Test]
    public async Task Will_not_disconnect_static_or_trusted_peer([Values((byte)0, (byte)24)] byte number)
    {
        const int peersMaxCount = 25;
        await using Context ctx = new();
        ctx.Pool = new SyncPeerPool(ctx.BlockTree, ctx.Stats, ctx.PeerStrategy, LimboLogs.Instance, peersMaxCount, 0, 50);
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, peersMaxCount);

        // Every peer is static or trusted except peers[number], the only droppable (plain) one.
        for (int i = 0; i < peersMaxCount; i++)
        {
            if (i == number) continue;
            if (i % 2 == 0) peers[i].Node.IsStatic = true;
            else peers[i].Node.IsTrusted = true;
        }

        await WaitForPeersInitialization(ctx);
        ctx.Pool.DropUselessPeers(true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers[number].DisconnectRequested, Is.True, "the only plain peer is droppable");
            Assert.That(peers.Where((p, i) => i != number).Any(static p => p.DisconnectRequested), Is.False, "static and trusted peers are never dropped");
        }
    }

    [Test]
    public async Task Should_increment_PriorityPeerCount_when_added_priority_peer_and_decrement_after_removal()
    {
        const int peersMaxCount = 1;
        const int priorityPeersMaxCount = 1;
        await using Context ctx = new();
        ctx.Pool = new SyncPeerPool(ctx.BlockTree, ctx.Stats, ctx.PeerStrategy, LimboLogs.Instance, peersMaxCount, priorityPeersMaxCount, 50);

        SimpleSyncPeerMock peer = new(TestItem.PublicKeyA) { IsPriority = true };
        ctx.Pool.Start();
        ctx.Pool.AddPeer(peer);
        await WaitForPeersInitialization(ctx);
        Assert.That(ctx.Pool.PriorityPeerCount, Is.EqualTo(1));

        ctx.Pool.RemovePeer(peer);
        Assert.That(ctx.Pool.PriorityPeerCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Should_increment_PriorityPeerCount_when_called_SetPriorityPeer()
    {
        const int peersMaxCount = 1;
        const int priorityPeersMaxCount = 1;
        await using Context ctx = new();
        ctx.Pool = new SyncPeerPool(ctx.BlockTree, ctx.Stats, ctx.PeerStrategy, LimboLogs.Instance, peersMaxCount, priorityPeersMaxCount, 50);

        SimpleSyncPeerMock peer = new(TestItem.PublicKeyA) { IsPriority = false };
        ctx.Pool.Start();
        ctx.Pool.AddPeer(peer);
        await WaitForPeersInitialization(ctx);
        Assert.That(ctx.Pool.PriorityPeerCount, Is.EqualTo(0));

        ctx.Pool.SetPeerPriority(peer.Id);
        Assert.That(ctx.Pool.PriorityPeerCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Cannot_remove_when_stopped()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        ISyncPeer[] syncPeers = new ISyncPeer[3];
        for (int i = 0; i < 3; i++)
        {
            syncPeers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
            ctx.Pool.AddPeer(syncPeers[i]);
        }

        await ctx.Pool.DisposeAsync();

        for (int i = 3; i > 0; i--)
        {
            Assert.That(ctx.Pool.PeerCount, Is.EqualTo(3), $"Remove {i}");
            ctx.Pool.RemovePeer(syncPeers[i - 1]);
        }
    }

    [Test]
    public async Task Peer_count_is_valid_when_adding()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        for (int i = 0; i < 3; i++)
        {
            Assert.That(ctx.Pool.PeerCount, Is.EqualTo(i));
            ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeys[i]));
        }
    }

    [Test]
    public async Task Does_not_crash_when_adding_twice_same_peer()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
        ctx.Pool.AddPeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));

        Assert.That(ctx.Pool.PeerCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Does_not_crash_when_removing_non_existing_peer()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        ctx.Pool.RemovePeer(new SimpleSyncPeerMock(TestItem.PublicKeyA));
        Assert.That(ctx.Pool.PeerCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Peer_count_is_valid_when_removing()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        ISyncPeer[] syncPeers = new ISyncPeer[3];
        for (int i = 0; i < 3; i++)
        {
            syncPeers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
            ctx.Pool.AddPeer(syncPeers[i]);
        }

        for (int i = 3; i > 0; i--)
        {
            Assert.That(ctx.Pool.PeerCount, Is.EqualTo(i), $"Remove {i}");
            ctx.Pool.RemovePeer(syncPeers[i - 1]);
        }
    }

    [Test]
    public async Task Can_start()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
    }

    [Test]
    public async Task Can_start_and_stop()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
    }

    [Test]
    public async Task Can_refresh()
    {
        await using Context ctx = new();
        ctx.Pool.Start();
        ISyncPeer? syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));

        TaskCompletionSource firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        syncPeer.When(p => p.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                firstCall.TrySetResult();
                if (Interlocked.Increment(ref callCount) == 2)
                    secondCall.TrySetResult();
            });

        ctx.Pool.AddPeer(syncPeer);
        await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(30));
        ctx.Pool.RefreshTotalDifficulty(syncPeer, Keccak.Zero);

        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That(callCount, Is.EqualTo(2));
    }

    [Test]
    public async Task Can_list_all_peers()
    {
        await using Context ctx = new();
        _ = await SetupPeers(ctx, 3);
        Assert.That(ctx.Pool.AllPeers.Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task Can_borrow_peer()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 1);

        using SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));

        Assert.That(allocation.Current?.SyncPeer, Is.SameAs(peers[0]));
    }

    [Test]
    public async Task Can_borrow_return_and_borrow_again()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 1);

        using (SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true)))
        {
            Assert.That(allocation.Current?.SyncPeer, Is.SameAs(peers[0]));
        }

        using (SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true)))
        {
            allocation.Dispose();
            allocation.Dispose();
        }

        using SyncPeerAllocation reallocated = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        Assert.That(reallocated.Current?.SyncPeer, Is.SameAs(peers[0]));
    }

    [Test]
    public async Task Can_borrow_many()
    {
        await using Context ctx = new();
        await SetupPeers(ctx, 2);

        using (SyncPeerAllocation firstAllocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true)))
        using (SyncPeerAllocation secondAllocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true)))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(secondAllocation.Current, Is.Not.SameAs(firstAllocation.Current), "first");
                Assert.That(firstAllocation.Current, Is.Not.Null, "first A");
            }
            Assert.That(secondAllocation.Current, Is.Not.Null, "first B");
        }

        using SyncPeerAllocation allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        using SyncPeerAllocation allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocation2.Current, Is.Not.SameAs(allocation1.Current));
            Assert.That(allocation1.Current, Is.Not.Null, "second A");
        }
        Assert.That(allocation2.Current, Is.Not.Null, "second B");
    }

    [Test]
    public async Task Does_not_allocate_sleeping_peers()
    {
        await using Context ctx = new();
        _ = await SetupPeers(ctx, 3);
        for (int i = 0; i < PeerInfo.SleepThreshold + 1; i++)
        {
            ctx.Pool.ReportNoSyncProgress(ctx.Pool.InitializedPeers.First(), AllocationContexts.All);
        }

        using SyncPeerAllocation allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        using SyncPeerAllocation allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        using SyncPeerAllocation allocation3 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocation1.HasPeer, Is.True);
            Assert.That(allocation2.HasPeer, Is.True);
            Assert.That(allocation3.HasPeer, Is.False);
        }
    }

    [Test]
    public async Task Can_wake_up_all_sleeping_peers()
    {
        await using Context ctx = new();
        _ = await SetupPeers(ctx, 3);
        ctx.Pool.ReportNoSyncProgress(ctx.Pool.InitializedPeers.First(), AllocationContexts.All);
        ctx.Pool.ReportNoSyncProgress(ctx.Pool.InitializedPeers.Last(), AllocationContexts.All);

        ctx.Pool.WakeUpAll();

        using SyncPeerAllocation allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        using SyncPeerAllocation allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        using SyncPeerAllocation allocation3 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocation1.HasPeer, Is.True);
            Assert.That(allocation2.HasPeer, Is.True);
            Assert.That(allocation3.HasPeer, Is.True);
        }
    }

    [Test]
    public async Task Initialized_peers()
    {
        await using Context ctx = new();
        _ = await SetupPeers(ctx, 3);
        Assert.That(ctx.Pool.InitializedPeers.Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task Report_invalid_invokes_disconnection()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] _ = await SetupPeers(ctx, 3);
        PeerInfo peerInfo = ctx.Pool.InitializedPeers.First();
        ctx.Pool.ReportBreachOfProtocol(peerInfo, DisconnectReason.Other, "issue details");

        Assert.That(((SimpleSyncPeerMock)peerInfo.SyncPeer).DisconnectRequested, Is.True);
    }

    [TestCase(AllocationContexts.All)]
    [TestCase(AllocationContexts.BlockAccessLists)]
    public async Task Will_not_allocate_same_peer_to_two_allocations(AllocationContexts contexts)
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 1);
        peers[0].ProtocolVersion = EthVersions.Eth71;

        using SyncPeerAllocation allocation1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), contexts);
        using SyncPeerAllocation allocation2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), contexts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocation1.Current?.SyncPeer, Is.SameAs(peers[0]));
            Assert.That(allocation2.Current, Is.Null);
        }
    }

    [Test]
    public async Task Will_weaken_peer_if_times_out_on_init()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock peer = new(TestItem.PublicKeyA);
        peer.SetHeaderResponseTime(int.MaxValue);
        ctx.Pool.Start();
        ctx.Pool.AddPeer(peer);

        await Wait.ForCondition(() => ctx.Pool.GetPeer(peer.Node)?.HasWeakness(AllocationContexts.All) == true, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.Pool.GetPeer(peer.Node)!.HasWeakness(AllocationContexts.All), Is.True);
            Assert.That(peer.DisconnectRequested, Is.False);
        }
    }

    [Test]
    public async Task It_is_fine_to_fail_init()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock peer = new(TestItem.PublicKeyA);
        peer.SetHeaderFailure(new Exception("Failed"));
        ctx.Pool.Start();
        ctx.Pool.AddPeer(peer);
        // GetHeadBlockHeader throws, so refresh routes through ReportRefreshFailed -> Disconnect.
        bool refreshAttempted = await Wait.ForCondition(() => peer.DisconnectRequested, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
        Assert.That(refreshAttempted, Is.True, "refresh loop did not attempt the failing peer in time");

        using SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        ctx.Pool.RemovePeer(peer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocation.Current, Is.EqualTo(null));
            Assert.That(ctx.Pool.PeerCount, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Does_not_fail_when_receiving_a_new_block_and_allocation_has_no_peer()
    {
        await using Context ctx = new();
        await SetupPeers(ctx, 1);

        using SyncPeerAllocation allocation = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true));
        allocation.Dispose();

        ctx.BlockTree.NewHeadBlock += Raise.EventWith(new object(), new BlockEventArgs(Build.A.Block.WithTotalDifficulty(1L).TestObject));
    }

    [Test]
    public async Task Can_borrow_async_many()
    {
        await using Context ctx = new();
        await SetupPeers(ctx, 2);

        Task<SyncPeerAllocation>[] allocationTasks = new Task<SyncPeerAllocation>[3];
        for (int i = 0; i < allocationTasks.Length; i++)
        {
            allocationTasks[i] = ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 50);
        }

        await Task.WhenAll(allocationTasks);

        SyncPeerAllocation[] allocations = allocationTasks.Select(static t => t.Result).ToArray();
        SyncPeerAllocation[] successfulAllocations = allocations.Where(static r => r.Current is not null).ToArray();

        // we had only two peers and 3 borrow calls so only two are successful
        Assert.That(successfulAllocations.Length, Is.EqualTo(2));

        foreach (SyncPeerAllocation allocation in successfulAllocations)
        {
            allocation.Dispose();
        }

        foreach (SyncPeerAllocation allocation in allocations)
        {
            // no peer assigned any more after calling free
            Assert.That(allocation.Current, Is.Null, "null A");
        }
    }

    [Test]
    public async Task Return_request_limit()
    {
        await using Context ctx = new();
        await SetupPeers(ctx, 1);

        PeerInfo thePeer = ctx.Pool.InitializedPeers.First();
        INodeStats nodeStat = Substitute.For<INodeStats>();
        ctx.Stats.GetOrAdd(thePeer.SyncPeer.Node).Returns(nodeStat);
        nodeStat.GetCurrentRequestLimit(RequestType.Headers).Returns(999);

        int? limit = await ctx.Pool.EstimateRequestLimit(RequestType.Headers, new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.Headers, default);

        Assert.That(limit, Is.EqualTo(999));
    }

    [Test]
    public async Task When_no_peer_will_cancel_on_cancellation_token()
    {
        await using Context ctx = new();
        using CancellationTokenSource cts = new();
        cts.CancelAfter(100);

        IOwnedReadOnlyList<BlockHeader>? result = await ctx.Pool.AllocateAndRun(
            static (peer) => { return peer.GetBlockHeaders(0, 1, 1, CancellationToken.None); },
            BySpeedStrategy.FastestHeader, AllocationContexts.Headers, cts.Token);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Waiter_wakes_on_signal_after_free()
    {
        await using Context ctx = new();
        await SetupPeers(ctx, 1);

        // Allocate the only peer
        using SyncPeerAllocation first = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 1000);
        Assert.That(first.HasPeer, Is.True);

        // Start a second allocation that must wait (only 1 peer, already allocated)
        Task<SyncPeerAllocation> secondTask = ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 2000);

        // Dispose the first — this signals peers changed and wakes the waiter
        first.Dispose();

        using SyncPeerAllocation second = await secondTask;
        Assert.That(second.HasPeer, Is.True);
    }

    [Test]
    public async Task Cancellation_while_waiting_returns_failed_allocation()
    {
        await using Context ctx = new();
        ctx.Pool.Start();

        // No peers added — allocation will wait until cancelled
        using CancellationTokenSource cts = new();
        cts.CancelAfter(100);

        using SyncPeerAllocation result = await ctx.Pool.Allocate(
            new BySpeedStrategy(TransferSpeedType.Headers, true),
            AllocationContexts.All,
            5000,
            cts.Token);

        Assert.That(result.HasPeer, Is.False);
    }

    [Test]
    public async Task Dispose_while_waiting_does_not_hang()
    {
        Context ctx = new();
        ctx.Pool.Start();

        // No peers — allocation will block
        Task<SyncPeerAllocation> allocTask = ctx.Pool.Allocate(
            new BySpeedStrategy(TransferSpeedType.Headers, true),
            AllocationContexts.All,
            10000);

        await Task.Delay(50);
        await ctx.DisposeAsync();

        // Must complete (not hang) and return failed
        using SyncPeerAllocation result = await allocTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result.HasPeer, Is.False);
    }

    [Test]
    public async Task Concurrent_allocations_capped_by_available_peers()
    {
        await using Context ctx = new();
        await SetupPeers(ctx, 3);

        // Request 5 allocations with only 3 peers
        Task<SyncPeerAllocation>[] tasks = new Task<SyncPeerAllocation>[5];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = ctx.Pool.Allocate(
                new BySpeedStrategy(TransferSpeedType.Headers, true),
                AllocationContexts.All,
                100);
        }

        SyncPeerAllocation[] allocations = await Task.WhenAll(tasks);

        int successful = 0;
        foreach (SyncPeerAllocation allocation in allocations)
        {
            if (allocation.HasPeer) successful++;
        }

        Assert.That(successful, Is.EqualTo(3));

        foreach (SyncPeerAllocation allocation in allocations)
        {
            allocation.Dispose();
        }
    }

    [Test]
    public async Task All_waiters_wake_when_peers_freed()
    {
        await using Context ctx = new();
        await SetupPeers(ctx, 2);

        // Allocate both peers — pool exhausted
        using SyncPeerAllocation a1 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 1000);
        using SyncPeerAllocation a2 = await ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 1000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a1.HasPeer, Is.True);
            Assert.That(a2.HasPeer, Is.True);
        }

        // Start 2 waiters — both blocked, no peers available
        Task<SyncPeerAllocation> w1 = ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 3000);
        Task<SyncPeerAllocation> w2 = ctx.Pool.Allocate(new BySpeedStrategy(TransferSpeedType.Headers, true), AllocationContexts.All, 3000);

        // Dispose both peers — both waiters must wake
        a1.Dispose();
        a2.Dispose();

        using SyncPeerAllocation r1 = await w1;
        using SyncPeerAllocation r2 = await w2;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(r1.HasPeer, Is.True);
            Assert.That(r2.HasPeer, Is.True);
        }
    }

    /// <summary>
    /// Every peer answers with a header, but only <paramref name="peerWithRequestedBlock"/> answers with the one
    /// that was asked for (-1 when no peer has it). The rest stand in for peers serving some other block.
    /// </summary>
    [TestCase(0, 0, true, TestName = "Fetch_header_accepts_the_requested_block")]
    [TestCase(-1, 0, false, TestName = "Fetch_header_rejects_a_different_block")]
    [TestCase(1, 100, true, TestName = "Fetch_header_waits_past_a_peer_answering_with_a_different_block")]
    public async Task Fetch_header_only_accepts_the_requested_block(int peerWithRequestedBlock, int responseDelay, bool shouldFind)
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 2);

        BlockHeader requested = Build.A.BlockHeader.WithNumber(10).TestObject;
        for (int i = 0; i < peers.Length; i++)
        {
            peers[i].HeaderToReturn = Build.A.BlockHeader.WithNumber(20 + i).TestObject;
        }

        if (peerWithRequestedBlock >= 0)
        {
            peers[peerWithRequestedBlock].HeaderToReturn = requested;
            peers[peerWithRequestedBlock].SetHeaderResponseTime(responseDelay);
        }

        BlockHeader? result = await ctx.Pool.FetchHeaderFromPeer(requested.Hash!);

        Assert.That(result, Is.SameAs(shouldFind ? requested : null));
    }

    public enum PeerAnswer { RequestedBlock, DifferentBlock, Nothing }

    /// <summary>
    /// Substituting another block is a protocol breach and costs the peer its connection, but not having the
    /// block is the normal answer while a head is unknown and must leave the peer alone.
    /// </summary>
    [TestCase(PeerAnswer.RequestedBlock, false)]
    [TestCase(PeerAnswer.DifferentBlock, true)]
    [TestCase(PeerAnswer.Nothing, false)]
    public async Task Fetch_header_only_reports_a_peer_answering_with_a_different_block(PeerAnswer answer, bool shouldReport)
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 1);

        BlockHeader requested = Build.A.BlockHeader.WithNumber(10).TestObject;
        switch (answer)
        {
            case PeerAnswer.RequestedBlock:
                peers[0].HeaderToReturn = requested;
                break;
            case PeerAnswer.DifferentBlock:
                peers[0].HeaderToReturn = Build.A.BlockHeader.WithNumber(20).TestObject;
                break;
            case PeerAnswer.Nothing:
                peers[0].HeadHeaderUnavailable = true;
                break;
        }

        await ctx.Pool.FetchHeaderFromPeer(requested.Hash!);

        Assert.That(peers[0].DisconnectRequested, Is.EqualTo(shouldReport));
    }

    [Test]
    public async Task Fetch_header_treats_a_null_header_answer_as_the_block_being_absent()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 1);

        BlockHeader requested = Build.A.BlockHeader.WithNumber(10).TestObject;
        peers[0].HeadHeaderUnavailable = true;
        peers[0].AnswersWithNullHeader = true;

        BlockHeader? result = await ctx.Pool.FetchHeaderFromPeer(requested.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(peers[0].DisconnectRequested, Is.False);
        }
    }

    [Test]
    public async Task Fetch_header_ignores_a_peer_at_its_concurrent_request_limit()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 2);

        BlockHeader requested = Build.A.BlockHeader.WithNumber(10).TestObject;
        peers[0].SetHeaderFailure(new ConcurrencyLimitReachedException("Concurrent request limit reached."));
        peers[1].HeaderToReturn = requested;
        peers[1].SetHeaderResponseTime(100);

        BlockHeader? result = await ctx.Pool.FetchHeaderFromPeer(requested.Hash!);

        Assert.That(result, Is.SameAs(requested));
    }

    [Test]
    public async Task Fetch_header_returns_null_when_the_allocated_peer_is_at_its_concurrent_request_limit()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 2);

        BlockHeader requested = Build.A.BlockHeader.WithNumber(10).TestObject;
        foreach (SimpleSyncPeerMock peer in peers)
        {
            peer.SetHeaderFailure(new ConcurrencyLimitReachedException("Concurrent request limit reached."));
        }

        BlockHeader? result = await ctx.Pool.FetchHeaderFromPeer(requested.Hash!);

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Only expected request failures mean "this peer has no header". Anything else is a bug and must not be
    /// reported as an unavailable header. <see cref="InvalidOperationException"/> is the base type of
    /// <see cref="ConcurrencyLimitReachedException"/>, so this also checks that the catch is not widened to it.
    /// </summary>
    [TestCase(false, TestName = "Fetch_header_propagates_an_unexpected_peer_failure")]
    [TestCase(true, TestName = "Fetch_header_propagates_an_unexpected_allocated_peer_failure")]
    public async Task Fetch_header_propagates_an_unexpected_request_failure(bool inFallback)
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 2);

        BlockHeader requested = Build.A.BlockHeader.WithNumber(10).TestObject;
        foreach (SimpleSyncPeerMock peer in peers)
        {
            peer.HeaderToReturn = requested;
            peer.HeadHeaderUnavailable = true;
            // In the fallback case every peer answers the head-header request with nothing, so the allocated
            // peer's by-hash request is the one that fails; otherwise one peer fails its head-header request.
            if (inFallback || peer == peers[0])
            {
                peer.SetHeaderFailure(new InvalidOperationException("Unexpected"), byHashOnly: inFallback);
            }
        }

        Assert.That(async () => await ctx.Pool.FetchHeaderFromPeer(requested.Hash!),
            Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("Unexpected"));
    }

    [Test]
    public async Task Fetch_header_falls_back_to_an_allocated_peer_when_no_head_header_is_returned()
    {
        await using Context ctx = new();
        SimpleSyncPeerMock[] peers = await SetupPeers(ctx, 2);

        BlockHeader requested = Build.A.BlockHeader.WithNumber(10).TestObject;
        foreach (SimpleSyncPeerMock peer in peers)
        {
            peer.HeaderToReturn = requested;
            peer.HeadHeaderUnavailable = true;
        }

        BlockHeader? result = await ctx.Pool.FetchHeaderFromPeer(requested.Hash!);

        Assert.That(result, Is.SameAs(requested));
    }

    private async Task<SimpleSyncPeerMock[]> SetupPeers(Context ctx, int count)
    {
        SimpleSyncPeerMock[] peers = new SimpleSyncPeerMock[count];
        for (int i = 0; i < count; i++)
        {
            peers[i] = new SimpleSyncPeerMock(TestItem.PublicKeys[i]);
        }

        ctx.Pool.Start();

        for (int i = 0; i < count; i++)
        {
            ctx.Pool.AddPeer(peers[i]);
        }

        await WaitForPeersInitialization(ctx);
        return peers;
    }

    private async Task WaitForPeersInitialization(Context ctx)
    {
        bool initialized = await Wait.ForCondition(
            () => ctx.Pool.AllPeers.All(p => p.IsInitialized),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(50));
        Assert.That(initialized, Is.True, "peers did not initialize in time");
    }

    [Test]
    public async Task Eviction_only_uses_failures_for_expected_block_data(
        [Values(AllocationContexts.State, AllocationContexts.Snap, AllocationContexts.BlockAccessLists,
            AllocationContexts.Headers, AllocationContexts.Bodies, AllocationContexts.Receipts, AllocationContexts.ForwardHeader)] AllocationContexts context)
    {
        using IContainer node = CreateNode();
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        IBlockTree tree = node.Resolve<IBlockTree>();
        ISyncPeer[] peers = [Substitute.For<ISyncPeer>(), Substitute.For<ISyncPeer>()];
        pool.Start();
        for (int index = 0; index < peers.Length; index++)
        {
            BlockHeader header = Build.A.BlockHeader.WithNumber((ulong)(100 + 100 * index)).TestObject;
            tree.Insert(header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
            peers[index].Node.Returns(new Node(TestItem.PublicKeys[index], "127.0.0.1", 30303));
            peers[index].HeadHash = header.Hash!;
            peers[index].HeadNumber = header.Number;
            peers[index].GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<BlockHeader?>(header));
            pool.AddPeer(peers[index]);
            Assert.That(pool.GetPeer(peers[index].Node)!.ServedHeadNumber, Is.Zero, "local lookup is not serving evidence");
            pool.UpdateSyncPeerHeadIfHeaderIsBetter(peers[index], (await peers[index].GetHeadBlockHeader(header.Hash!, default))!, Keccak.Zero);
        }
        Assert.That(pool.AllPeers.Count(peer => peer.ServedHeadNumber != 0), Is.EqualTo(2));
        pool.ReportNoSyncProgress(pool.GetPeer(peers[1].Node)!, context);

        pool.DropUselessPeers(true);

        bool expectedBlockData = context is AllocationContexts.Headers or AllocationContexts.Bodies or AllocationContexts.Receipts or AllocationContexts.ForwardHeader;
        peers[expectedBlockData ? 1 : 0].Received(1).Disconnect(DisconnectReason.DropWorstPeer, Arg.Any<string>());
        peers[expectedBlockData ? 0 : 1].DidNotReceive().Disconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test, CancelAfter(30000)]
    public async Task Refresh_credits_only_a_locally_known_or_consensus_requested_header(
        [Values] bool stored, [Values] bool consensusRequested, [Values] bool parentStored, CancellationToken token)
    {
        TaskCompletionSource refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ILogManager logs = ObserveRefreshCompletion(() => refreshed);
        using IContainer node = CreateNode(logManager: logs);
        IBlockTree tree = node.Resolve<IBlockTree>();
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        BlockHeader parent = Build.A.BlockHeader.WithNumber(99).TestObject;
        parent.TotalDifficulty = new UInt256(1000);
        if (parentStored) tree.Insert(parent, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        BlockHeader response = Build.A.BlockHeader.WithParent(parent).WithNumber(100).WithDifficulty(UInt256.MaxValue / 2).TestObject;
        UInt256 expectedDifficulty = parent.TotalDifficulty.Value + response.Difficulty;
        if (stored)
        {
            response.TotalDifficulty = expectedDifficulty;
            tree.Insert(response, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        }
        using HeadRacePeer protocolPeer = new(response.Hash!, response.Number);
        ISyncPeer remote = protocolPeer.Peer;
        remote.IsInitialized = true;
        remote.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<BlockHeader?>(response));
        pool.Start();
        pool.AddPeer(remote);
        if (!stored) await refreshed.Task.WaitAsync(token);
        remote.TotalDifficulty = UInt256.One;
        PeerInfo peer = pool.GetPeer(remote.Node)!;
        pool.ReportNoSyncProgress(peer, AllocationContexts.Headers);
        pool.ReportRefreshFailed(remote, "timeout", new TimeoutException());

        if (consensusRequested) pool.UpdateSyncPeerHeadIfHeaderIsBetter(remote, response, response.Hash!);
        else pool.UpdateSyncPeerHeadIfHeaderIsBetter(remote, response, Keccak.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.ServedHeadNumber, Is.EqualTo(stored || consensusRequested ? 100UL : 0UL));
            Assert.That(peer.HasTimeoutEvidence, Is.EqualTo(!stored && !consensusRequested));
            Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.True, "refresh cannot erase an allocated failure");
            Assert.That(protocolPeer.Protocol.HeadHash, Is.EqualTo(response.Hash));
            Assert.That(protocolPeer.Protocol.HeadNumber, Is.EqualTo(response.Number));
            Assert.That(protocolPeer.Protocol.TotalDifficulty,
                Is.EqualTo((UInt256?)(stored || consensusRequested && parentStored ? expectedDifficulty : UInt256.One)),
                "only a trusted response may update cumulative work through the real protocol head CAS");
        }
    }

    [Test, CancelAfter(30000)]
    public async Task Pruning_during_body_request_cannot_reset_unknown_history_grace([Values] bool pruned, [Values] bool recover, CancellationToken token)
    {
        IBlockTree source = Build.A.BlockTree().WithTransactions(new InMemoryReceiptStorage()).OfChainLength(4).TestObject;
        using TestMemDb blocksDb = new();
        using MemDb metadataDb = new();
        IBlockTree target = Build.A.BlockTree().WithBlocksDb(blocksDb).TestObject;
        for (ulong number = 1; number < 4; number++) target.Insert(source.FindHeader(number, BlockTreeLookupOptions.None)!);
        BlockHeader pivot = source.FindHeader(3, BlockTreeLookupOptions.None)!;
        target.SyncPivot = (3, pivot.Hash!);
        TestSyncConfig config = new() { FastSync = true, PivotNumber = 3, PivotHash = pivot.Hash!.ToString(), AncientBodiesBarrier = 0 };
        using IContainer node = CreateNode();
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        node.Resolve<IBlockTree>().Insert(pivot, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        using BodiesSyncFeed feed = new(MainnetSpecProvider.Instance, target,
            new BlockValidator(Always.Valid, Always.Valid, Always.Valid, MainnetSpecProvider.Instance, LimboLogs.Instance),
            new MemorySyncPointers(), pool, config, new NullSyncReport(), Substitute.For<IHistoryPruner>(), blocksDb, metadataDb, LimboLogs.Instance);
        ISyncPeer remote = Substitute.For<ISyncPeer>();
        remote.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        remote.HeadHash = pivot.Hash!;
        remote.HeadNumber = pivot.Number;
        remote.IsInitialized.Returns(true);
        remote.GetBlockBodies(Arg.Any<System.Collections.Generic.IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                remote.EarliestBlockNumber.Returns(pruned ? 4UL : 0UL);
                return Task.FromResult<OwnedBlockBodies>(new OwnedBlockBodies([]));
            });
        pool.Start();
        pool.AddPeer(remote);
        PeerInfo peer = pool.GetPeer(remote.Node)!;
        feed.InitializeFeed();
        ulong[]? requestedNumbers = null;
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            remote.EarliestBlockNumber.Returns(0UL);
            using BodiesSyncBatch request = (await feed.PrepareRequest())!;
            ulong[] numbers = request.Infos.Where(info => info is not null).Select(info => info!.BlockNumber).ToArray();
            requestedNumbers ??= numbers;
            Assert.That(numbers, Is.EquivalentTo(requestedNumbers));
            await SyncDispatcherTests.DispatchOnce(request, new BodiesSyncDownloader(LimboLogs.Instance), pool,
                AllocationContexts.Bodies, token, feed);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.HasNoProgress(AllocationContexts.Bodies), Is.EqualTo(attempt == 4));
                Assert.That(peer.HistoryFloor(AllocationContexts.Bodies), Is.Zero);
            }
        }
        if (!recover)
        {
            ISyncPeer honest = Substitute.For<ISyncPeer>();
            honest.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303));
            honest.HeadHash = pivot.Hash!;
            honest.HeadNumber = pivot.Number;
            honest.IsInitialized.Returns(true);
            pool.AddPeer(honest);
            pool.DropUselessPeers(true);
            using (Assert.EnterMultipleScope())
            {
                remote.Received(1).Disconnect(DisconnectReason.DropWorstPeer, Arg.Any<string>());
                honest.DidNotReceive().Disconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
            }
            return;
        }
        remote.EarliestBlockNumber.Returns(0UL);
        using BodiesSyncBatch retry = (await feed.PrepareRequest())!;
        remote.GetBlockBodies(Arg.Any<System.Collections.Generic.IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnedBlockBodies>(new OwnedBlockBodies(retry.Infos.TakeWhile(info => info is not null)
                .Select(info => source.FindBlock(info!.BlockNumber, BlockTreeLookupOptions.None)!.Body).ToArray())));
        await SyncDispatcherTests.DispatchOnce(retry, new BodiesSyncDownloader(LimboLogs.Instance), pool,
            AllocationContexts.Bodies, token, feed);
        Assert.That(peer.HasNoProgress(AllocationContexts.Bodies), Is.False);
        foreach (ulong number in requestedNumbers!)
            Assert.That(target.FindBlock(number, BlockTreeLookupOptions.None)!.IsBodyMissing, Is.False);
    }

    [Test]
    public void History_allocations_respect_the_inclusive_advertised_range(
        [Values(TransferSpeedType.Headers, TransferSpeedType.Bodies, TransferSpeedType.Receipts)] TransferSpeedType type,
        [Values(99UL, 100UL, 101UL)] ulong requestedNumber,
        [Values] bool alreadyAllocated)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        syncPeer.IsInitialized = true;
        syncPeer.HeadNumber = 200;
        syncPeer.EarliestBlockNumber.Returns(100UL);
        PeerInfo peer = new(syncPeer);
        FastBlocksAllocationStrategy strategy = new(type, requestedNumber, true);
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        PeerInfo? allocated = strategy.Allocate(alreadyAllocated ? peer : null, [peer], stats, Substitute.For<IBlockTree>());

        bool available = type == TransferSpeedType.Headers || requestedNumber >= 100;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocated, Is.SameAs(available ? peer : null));
            Assert.That(peer.HasNoProgress(AllocationContexts.All), Is.False);
        }
    }

    [Test]
    public void No_progress_expires_per_context_without_wall_clock_dependence([Values] bool jumpWallClock)
    {
        ManualTimeProvider clock = new();
        PeerInfo peer = new(Substitute.For<ISyncPeer>(), null, clock);
        peer.RecordNoProgress(AllocationContexts.Headers);
        clock.Advance(TimeSpan.FromMinutes(4));
        peer.RecordNoProgress(AllocationContexts.Bodies);
        if (jumpWallClock) clock.JumpUtc(TimeSpan.FromDays(-1));
        clock.Advance(TimeSpan.FromMinutes(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.False);
            Assert.That(peer.HasNoProgress(AllocationContexts.Bodies), Is.True);
        }
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.That(peer.HasNoProgress(AllocationContexts.All), Is.False);
    }

    [Test]
    public void Useful_work_preserves_failure_evidence_in_other_contexts(
        [Values(AllocationContexts.Headers, AllocationContexts.State, AllocationContexts.BlockAccessLists)] AllocationContexts successfulContext)
    {
        PeerInfo peer = new(Substitute.For<ISyncPeer>());
        peer.RecordNoProgress(AllocationContexts.Bodies);
        peer.IncreaseWeakness(AllocationContexts.Bodies);
        peer.IncreaseWeakness(successfulContext);

        peer.RecordProgress(successfulContext);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HasNoProgress(AllocationContexts.Bodies), Is.True);
            Assert.That(peer.HasWeakness(successfulContext), Is.False);
            Assert.That(peer.HasWeakness(AllocationContexts.Bodies), Is.True);
        }
    }
    public enum HeightFallbackGuard { None, NullDifficulty, TransitionFinished, TerminalDifficulty, BeaconControl, ForwardFailure, ZeroDifficultyTransitionFinished }

    [Test]
    public async Task Stale_difficulty_selection_uses_the_production_known_height_and_transition_guards(
        [Values(50UL, 200UL)] ulong suggestedHeight, [Values(75UL, 150UL)] ulong peerHeight, [Values] HeightFallbackGuard guard)
    {
        using IContainer node = CreateNode();
        IBlockTree tree = node.Resolve<IBlockTree>();
        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);
        BlockHeader known = Build.A.BlockHeader.WithNumber(100).TestObject;
        tree.Insert(known, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        ISyncPeer remote = Substitute.For<ISyncPeer>();
        remote.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        remote.IsInitialized.Returns(true);
        remote.HeadHash = tree.Genesis!.Hash!;
        remote.HeadNumber.Returns(peerHeight);
        remote.TotalDifficulty.Returns(guard == HeightFallbackGuard.NullDifficulty ? (UInt256?)null
            : guard == HeightFallbackGuard.ZeroDifficultyTransitionFinished ? UInt256.Zero : UInt256.One);
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        pool.Start();
        pool.AddPeer(remote);
        PeerInfo peer = pool.GetPeer(remote.Node)!;
        if (guard == HeightFallbackGuard.ForwardFailure) peer.RecordNoProgress(AllocationContexts.ForwardHeader);
        Assert.That(peer.KnownHeadNumber, Is.EqualTo(100UL));
        ISyncProgressResolver progress = Substitute.For<ISyncProgressResolver>();
        progress.FindBestHeader().Returns(suggestedHeight);
        progress.FindBestFullBlock().Returns(suggestedHeight);
        progress.FindBestFullState().Returns(suggestedHeight);
        progress.FindBestProcessedBlock().Returns(suggestedHeight);
        progress.ChainDifficulty.Returns(guard == HeightFallbackGuard.ZeroDifficultyTransitionFinished ? UInt256.Zero : new UInt256(200));
        IBeaconSyncStrategy beacon = Substitute.For<IBeaconSyncStrategy>();
        beacon.MergeTransitionFinished.Returns(guard is HeightFallbackGuard.TransitionFinished or HeightFallbackGuard.ZeroDifficultyTransitionFinished);
        beacon.ShouldBeInBeaconModeControl().Returns(guard == HeightFallbackGuard.BeaconControl);
        IBetterPeerStrategy strategy = Substitute.For<IBetterPeerStrategy>();
        TotalDifficultyBetterPeerStrategy ordering = new(LimboLogs.Instance);
        strategy.Compare(Arg.Any<(UInt256?, ulong)>(), Arg.Any<(UInt256?, ulong)>())
            .Returns(call => ordering.Compare(call.ArgAt<(UInt256?, ulong)>(0), call.ArgAt<(UInt256?, ulong)>(1)));
        strategy.IsLowerThanTerminalTotalDifficulty(Arg.Any<UInt256>()).Returns(guard != HeightFallbackGuard.TerminalDifficulty);
        using MultiSyncModeSelector selector = new(progress, pool, new TestSyncConfig { FastSync = false },
            beacon, strategy, LimboLogs.Instance);
        await selector.StopAsync();

        selector.Update();

        Assert.That(selector.Current == SyncMode.Full, Is.EqualTo(guard == HeightFallbackGuard.None && peerHeight > 100),
            "a non-null stale claim can probe beyond BestKnown before the terminal transition, even when BestSuggested differs");
    }

    internal static void AssertInferredHistoryAllocation(PeerInfo peer, AllocationContexts context, IBlockTree tree, ulong floor)
    {
        peer.SyncPeer.IsInitialized = true;
        peer.SyncPeer.HeadNumber = ulong.MaxValue;
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        TransferSpeedType speed = context == AllocationContexts.Bodies ? TransferSpeedType.Bodies : TransferSpeedType.Receipts;
        FastBlocksAllocationStrategy below = new(speed, floor - 1, false, FirstFree.Instance);
        FastBlocksAllocationStrategy above = new(speed, floor, false, FirstFree.Instance);
        FastBlocksAllocationStrategy headers = new(TransferSpeedType.Headers, floor - 1, false, FirstFree.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(below.Allocate(null, [peer], stats, tree), Is.Null);
            Assert.That(above.Allocate(null, [peer], stats, tree), Is.SameAs(peer));
            Assert.That(headers.Allocate(null, [peer], stats, tree), Is.SameAs(peer), "body or receipt evidence cannot exclude header backfill");
        }
    }

    internal static async Task AssertBoundedEmptyHistory(Func<bool, Task<SyncResponseHandlingResult>> respond, bool usefulBetweenEmpties)
    {
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            SyncResponseHandlingResult empty = await respond(false);
            Assert.That(empty, Is.EqualTo(usefulBetweenEmpties || attempt <= 3
                ? SyncResponseHandlingResult.Emptish : SyncResponseHandlingResult.NoProgress));
            if (usefulBetweenEmpties)
                Assert.That(await respond(true), Is.EqualTo(SyncResponseHandlingResult.OK));
        }
    }

    [Test]
    public async Task Consensus_refresh_credits_only_the_original_requested_head([Values] bool matching)
    {
        using IContainer node = CreateNode();
        IBlockTree tree = node.Resolve<IBlockTree>();
        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);
        BlockHeader parent = tree.Genesis!;
        BlockHeader requested = Build.A.BlockHeader.WithParent(parent).WithDifficulty(0).TestObject;
        BlockHeader answer = matching ? requested : Build.A.BlockHeader.WithParent(parent).WithExtraData([1]).TestObject;
        using HeadRacePeer remote = new(parent.Hash!, 0);
        remote.Peer.GetBlockHeaders(parent.Hash!, 2, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(new ArrayPoolList<BlockHeader>([parent, answer])));
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        pool.Start();
        pool.AddPeer(remote.Peer);
        PeerInfo peer = pool.GetPeer(remote.Peer.Node)!;
        pool.ReportNoSyncProgress(peer, AllocationContexts.Headers);
        await using PeerRefresher refresher = new(pool, Substitute.For<ITimerFactory>(), LimboLogs.Instance);

        refresher.RefreshPeers(requested.Hash!, parent.Hash!, Keccak.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.ServedHeadNumber, Is.EqualTo(matching ? requested.Number : 0UL));
            Assert.That(remote.Protocol.HeadHash, Is.EqualTo(matching ? requested.Hash : parent.Hash));
            Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.True, "CL refresh is not allocated-header recovery");
            Assert.That(peer.HistoryFloor(AllocationContexts.Bodies), Is.Zero, "header-only serving cannot establish body availability");
            Assert.That(peer.HistoryFloor(AllocationContexts.Receipts), Is.Zero);
            Assert.That(tree.FindHeader(requested.Hash!, BlockTreeLookupOptions.None), Is.Null);
        }
    }

    [Test, CancelAfter(30000)]
    public async Task Configured_and_stored_pivots_keep_a_dynamic_allocatable_horizon(
        [Values(0UL, 5000UL)] ulong configuredPivot, [Values(0UL, 9000UL)] ulong storedPivot, CancellationToken token)
    {
        using IContainer node = CreateNode(syncConfig: new SyncConfig { SnapSync = true, PivotNumber = configuredPivot });
        IBlockTree tree = node.Resolve<IBlockTree>();
        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);
        tree.SyncPivot = (storedPivot, TestItem.KeccakC);
        using HeadRacePeer remote = new(tree.Genesis!.Hash!, ulong.MaxValue);
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        pool.Start();
        pool.AddPeer(remote.Peer);
        // AddPeer fills known metadata, so publish the subsequent distant announcement through the protocol tuple.
        remote.Protocol.SetHead(TestItem.KeccakA, ulong.MaxValue);
        PeerInfo peer = pool.GetPeer(remote.Peer.Node)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HeadNumber, Is.EqualTo(Math.Max(configuredPivot, storedPivot) + 1024));
            Assert.That(remote.Protocol.HeadNumber, Is.EqualTo(ulong.MaxValue));
            Assert.That(peer.ServedHeadNumber, Is.Zero);
        }
        using (SyncPeerAllocation allocation = await pool.Allocate(FirstFree.Instance, AllocationContexts.ForwardHeader, 0, token))
            Assert.That(allocation.Current, Is.SameAs(peer));
        tree.Insert(Build.A.BlockHeader.WithNumber(12000).TestObject, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HeadNumber, Is.EqualTo(13024UL));
            Assert.That(remote.Protocol.HeadNumber, Is.EqualTo(ulong.MaxValue));
            Assert.That(peer.ServedHeadNumber, Is.Zero);
        }
        using SyncPeerAllocation advanced = await pool.Allocate(FirstFree.Instance, AllocationContexts.ForwardHeader, 0, token);
        Assert.That(advanced.Current, Is.SameAs(peer));
    }

    internal sealed class HeadRacePeer : IDisposable
    {
        public Eth62ProtocolHandler Protocol { get; }
        public ISyncPeer Peer { get; } = Substitute.For<ISyncPeer>();
        public Action? OnNumberRead { get; set; }

        public HeadRacePeer(Hash256 hash, ulong number)
        {
            ISession session = Substitute.For<ISession>();
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            session.Node.Returns(node);
            Protocol = new(session, Substitute.For<IMessageSerializationService>(), Substitute.For<INodeStatsManager>(),
                Substitute.For<ISyncServer>(), RunImmediatelyScheduler.Instance, Substitute.For<ITxPool>(),
                Substitute.For<IGossipPolicy>(), LimboLogs.Instance);
            Protocol.SetHead(hash, number);
            Protocol.TotalDifficulty = UInt256.One;
            Protocol.IsInitialized = true;
            Peer.Node.Returns(node);
            Peer.IsInitialized.Returns(true);
            Peer.HeadHash.Returns(_ => Protocol.HeadHash);
            Peer.HeadNumber.Returns(_ =>
            {
                ulong snapshot = Protocol.HeadNumber;
                Action? action = OnNumberRead;
                OnNumberRead = null;
                action?.Invoke();
                return snapshot;
            });
            Peer.TotalDifficulty.Returns(_ => Protocol.TotalDifficulty);
            Peer.When(peer => peer.TotalDifficulty = Arg.Any<UInt256?>()).Do(call => Protocol.TotalDifficulty = call.Arg<UInt256?>());
            Peer.When(peer => peer.SetHead(Arg.Any<Hash256>(), Arg.Any<ulong>()))
                .Do(call => Protocol.SetHead(call.Arg<Hash256>(), call.Arg<ulong>()));
            Peer.TrySetHead(Arg.Any<Hash256>(), Arg.Any<ulong>(), Arg.Any<Hash256>(), Arg.Any<ulong>())
                .Returns(call => Protocol.TrySetHead(call.ArgAt<Hash256>(0), call.ArgAt<ulong>(1),
                    call.ArgAt<Hash256>(2), call.ArgAt<ulong>(3)));
        }

        public void Dispose() => Protocol.Dispose();
    }

    internal static IContainer CreateNode(ManualTimeProvider? clock = null, ILogManager? logManager = null, ISyncConfig? syncConfig = null, Module? extraModule = null)
    {
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(syncConfig is null ? new TestNethermindModule() : new TestNethermindModule(syncConfig))
            .AddSingleton<ILogManager>(logManager ?? LimboLogs.Instance)
            .AddSingleton<ISyncConfig>(syncConfig ?? new SyncConfig())
            .AddSingleton<IBetterPeerStrategy>(new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance))
            .AddSingleton<INetworkConfig>(new NetworkConfig { MaxActivePeers = 2 })
            .AddSingleton<SyncPeerPool>(context => new SyncPeerPool(context.Resolve<IBlockTree>(),
                context.Resolve<INodeStatsManager>(), context.Resolve<IBetterPeerStrategy>(), context.Resolve<INetworkConfig>(),
                context.Resolve<ISyncConfig>(), context.Resolve<ILogManager>())
            { RefreshTimeProvider = clock ?? TimeProvider.System });
        if (extraModule is not null) builder.AddModule(extraModule);
        return builder.Build();
    }

    internal static ILogManager ObserveRefreshCompletion(Func<TaskCompletionSource> completion)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsTrace.Returns(true);
        logger.When(log => log.Trace(Arg.Is<string>(message => message.StartsWith("Refreshed peer info for "))))
            .Do(_ => completion().TrySetResult());
        return new OneLoggerLogManager(new ILogger(logger));
    }

    [Test, CancelAfter(30000)]
    public async Task Already_dispatched_header_timeouts_count_once_until_another_request_is_sent(CancellationToken token)
    {
        ISyncPeer remote = Substitute.For<ISyncPeer>();
        PeerInfo peer = new(remote);
        await AssertTimeoutCohort<IOwnedReadOnlyList<BlockHeader>?, HeadersSyncBatch>(peer,
            replies => remote.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => replies()),
            () => new HeadersSyncBatch { StartNumber = 1, RequestSize = 1 },
            new HeadersSyncDownloader(LimboLogs.Instance), AllocationContexts.Headers, token);
    }

    internal static async Task AssertTimeoutCohort<TReply, TBatch>(PeerInfo peer,
        Action<Func<Task<TReply>>> configure, Func<TBatch> prepare, ISyncDownloader<TBatch> downloader,
        AllocationContexts context, CancellationToken token, ISyncFeed<TBatch?>? productionFeed = null) where TBatch : FastBlocksBatch
    {
        ISyncPeerPool pool = CreateIssuedRequestPool(peer);
        TaskCompletionSource<TReply>[] replies = [NewReply(), NewReply(), NewReply()];
        TaskCompletionSource[] sentSignals =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        ];
        int sent = 0;
        configure(() =>
        {
            int index = Interlocked.Increment(ref sent) - 1;
            sentSignals[index].TrySetResult();
            return replies[index].Task;
        });
        TBatch[] requests = [prepare(), prepare(), prepare()];
        Task[] dispatches = [Task.CompletedTask, Task.CompletedTask, Task.CompletedTask];
        try
        {
            for (int index = 0; index < requests.Length; index++)
            {
                dispatches[index] = SyncDispatcherTests.DispatchOnce(requests[index], downloader, pool,
                    context, token, productionFeed);
                await Task.WhenAny(dispatches[index], sentSignals[index].Task).WaitAsync(token);
                if (dispatches[index].IsCompleted) await dispatches[index];
                Assert.That(sentSignals[index].Task.IsCompleted, Is.True, "each real request must be sent before completing the outstanding cohort");
            }
            for (int index = 0; index < replies.Length; index++)
            {
                replies[index].SetException(new TimeoutException());
                await dispatches[index].WaitAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(peer.HasNoProgress(context), Is.False, "one outstanding cohort cannot count as three successive failures");
                    Assert.That(peer.HasWeakness(context), Is.True);
                    Assert.That(peer.HasTimeoutEvidence, Is.True);
                }
            }
        }
        finally
        {
            foreach (TaskCompletionSource<TReply> reply in replies) reply.TrySetException(new TimeoutException());
            await Task.WhenAll(dispatches).WaitAsync(token);
            foreach (TBatch request in requests) request.Dispose();
        }
        configure(() => Task.FromException<TReply>(new TimeoutException()));
        for (int later = 1; later <= 2; later++)
        {
            using TBatch request = prepare();
            await SyncDispatcherTests.DispatchOnce(request, downloader, pool, context, token, productionFeed);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.HasNoProgress(context), Is.EqualTo(later == 2));
                Assert.That(peer.HasWeakness(context), Is.True);
                Assert.That(peer.HasWeakness((AllocationContexts.Headers | AllocationContexts.Bodies | AllocationContexts.Receipts) & ~context), Is.False);
            }
        }
        static TaskCompletionSource<TReply> NewReply() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static ISyncPeerPool CreateIssuedRequestPool(PeerInfo peer, ISyncPeerPool? accountingPool = null)
    {
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        // Independent already-issued protocol requests share evidence, not an exclusive feed allocation.
        pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                SyncPeerAllocation allocation = new(AllocationContexts.None);
                allocation.AllocatePeer(peer);
                return Task.FromResult(allocation);
            });
        if (accountingPool is not null)
        {
            pool.When(value => value.ReportWeakPeer(peer, Arg.Any<AllocationContexts>()))
                .Do(call => accountingPool.ReportWeakPeer(peer, call.Arg<AllocationContexts>()));
            pool.When(value => value.ReportNoSyncProgress(peer, Arg.Any<AllocationContexts>()))
                .Do(call => accountingPool.ReportNoSyncProgress(peer, call.Arg<AllocationContexts>()));
        }
        return pool;
    }

    [Test]
    public async Task Header_probes_recover_from_transient_failure_but_require_header_progress_after_bad_data(
        [Values] bool concreteFault, [Values] bool successfulOtherContext)
    {
        ManualTimeProvider clock = new();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        IBlockTree tree = Build.A.BlockTree().TestObject;
        await using SyncPeerPool pool = new(tree, stats, new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), LimboLogs.Instance);
        PeerInfo unproven = CreatePeer(0, null, stats, clock);
        PeerInfo proven = CreatePeer(1, 10, stats, clock);
        BySpeedStrategy strategy = new(TransferSpeedType.Headers, false);
        pool.ReportNoSyncProgress(unproven, AllocationContexts.Headers);
        // Bad data reported after the stall keeps its own evidence lifetime once the stall evidence has expired.
        clock.Advance(TimeSpan.FromMinutes(1));
        if (concreteFault) pool.ReportWeakPeer(unproven, AllocationContexts.Headers);
        Assert.That(CountProbes(), Is.Zero);

        if (successfulOtherContext) unproven.RecordProgress(AllocationContexts.State);
        else clock.Advance(TimeSpan.FromMinutes(4));
        unproven.TryToWakeUp(DateTime.UtcNow.AddHours(1), TimeSpan.FromSeconds(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unproven.HasNoProgress(AllocationContexts.Headers), Is.EqualTo(successfulOtherContext));
            Assert.That(CountProbes(), Is.EqualTo(concreteFault || successfulOtherContext ? 0 : 2));
        }
        unproven.RecordProgress(AllocationContexts.Headers);
        Assert.That(CountProbes(), Is.EqualTo(2));

        int CountProbes()
        {
            int probes = 0;
            for (int i = 0; i < 64; i++)
                if (strategy.Allocate(null, [unproven, proven], stats, tree) == unproven) probes++;
            return probes;
        }
    }

    [Test, CancelAfter(30000)]
    public async Task Hint_and_consensus_refresh_cannot_erase_allocated_header_timeouts(CancellationToken token)
    {
        TaskCompletionSource refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ILogManager logs = ObserveRefreshCompletion(() => refreshed);
        using IContainer node = SyncPeerPoolTests.CreateNode(logManager: logs);
        IBlockTree tree = node.Resolve<IBlockTree>();
        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);
        BlockHeader parent = tree.Genesis!;
        BlockHeader child = Build.A.BlockHeader.WithParent(parent).TestObject;
        tree.Insert(child, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        using SyncPeerPoolTests.HeadRacePeer remote = new(parent.Hash!, 0);
        remote.Peer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IOwnedReadOnlyList<BlockHeader>?>(new TimeoutException()));
        remote.Peer.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(tree.FindHeader(call.ArgAt<Hash256>(0), BlockTreeLookupOptions.None)));
        remote.Peer.GetBlockHeaders(parent.Hash!, 2, 0, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(new ArrayPoolList<BlockHeader>([parent, child])));
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        pool.Start();
        pool.AddPeer(remote.Peer);
        PeerInfo peer = pool.GetPeer(remote.Peer.Node)!;
        await using PeerRefresher consensus = new(pool, Substitute.For<ITimerFactory>(), LimboLogs.Instance);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using HeadersSyncBatch request = new() { StartNumber = 1, RequestSize = 1 };
            await SyncDispatcherTests.DispatchOnce(request, new HeadersSyncDownloader(LimboLogs.Instance), pool,
                AllocationContexts.Headers, token);
            refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.RefreshTotalDifficulty(remote.Peer, child.Hash!);
            await refreshed.Task.WaitAsync(token);
            await consensus.RefreshPeerForFcu(remote.Peer, child.Hash!, parent.Hash!, Keccak.Zero, token);
            await remote.Peer.Received(attempt).GetBlockHeaders(parent.Hash!, 2, 0, Arg.Any<CancellationToken>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.True);
                Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.EqualTo(attempt == 3));
                Assert.That(peer.ServedHeadNumber, Is.EqualTo(child.Number), "the CL response serves data without recovering an allocated origin");
            }
        }
    }

    [Test]
    public async Task Head_refresh_timeout_is_transient_header_evidence([Values(1, 3)] int timeouts, [Values] bool trustedResponse)
    {
        ManualTimeProvider clock = new();
        using IContainer node = SyncPeerPoolTests.CreateNode(clock);
        IBlockTree tree = node.Resolve<IBlockTree>();
        SyncPeerPool pool = node.Resolve<SyncPeerPool>();
        ISyncPeer remote = Substitute.For<ISyncPeer>();
        remote.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        tree.Insert(header, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        remote.HeadHash = header.Hash!;
        remote.HeadNumber = 100;
        remote.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<BlockHeader?>(header));
        pool.Start();
        pool.AddPeer(remote);
        pool.UpdateSyncPeerHeadIfHeaderIsBetter(remote, (await remote.GetHeadBlockHeader(header.Hash!, default))!, Keccak.Zero);
        PeerInfo peer = pool.GetPeer(remote.Node)!;
        for (int index = 0; index < timeouts; index++)
            pool.ReportRefreshFailed(remote, "timeout", new TimeoutException());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.EqualTo(timeouts == 3));
            Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.True);
            Assert.That(peer.HasNoProgress(AllocationContexts.State | AllocationContexts.BlockAccessLists), Is.False);
        }
        peer.RecordProgress(AllocationContexts.State);
        Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.True, "unrelated work cannot clear refresh silence");
        if (trustedResponse)
            pool.UpdateSyncPeerHeadIfHeaderIsBetter(remote, (await remote.GetHeadBlockHeader(header.Hash!, default))!, Keccak.Zero);
        else
            clock.Advance(TimeSpan.FromMinutes(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.False);
            Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.False);
        }
        remote.DidNotReceive().Disconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task Header_recovery_preserves_bad_state_or_bal_evidence(
        [Values(AllocationContexts.State, AllocationContexts.BlockAccessLists)] AllocationContexts context)
    {
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        IBlockTree tree = Build.A.BlockTree().TestObject;
        await using SyncPeerPool pool = new(tree, stats, new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), LimboLogs.Instance);
        PeerInfo peer = CreatePeer(0, null, stats, new ManualTimeProvider());
        pool.ReportNoSyncProgress(peer, AllocationContexts.Headers);
        pool.ReportWeakPeer(peer, context);

        peer.RecordProgress(AllocationContexts.Headers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HasNoProgress(AllocationContexts.All), Is.False);
            Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.False);
            Assert.That(peer.HasWeakness(context), Is.True);
        }
        peer.RecordProgress(context);
        Assert.That(peer.HasWeakness(context), Is.False);
    }

    private static PeerInfo CreatePeer(int index, long? speed, INodeStatsManager stats, ManualTimeProvider clock)
    {
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(new Node(TestItem.PublicKeys[index], "127.0.0.1", 30303 + index));
        peer.IsInitialized = true;
        NodeStatsLight measured = new(peer.Node);
        if (speed is { } value) measured.AddTransferSpeedCaptureEvent(TransferSpeedType.Headers, value);
        stats.GetStatsOrNull(peer.Node).Returns(measured);
        return new PeerInfo(peer, null, clock);
    }
}
