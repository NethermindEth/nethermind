// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastBlocks;

public class BlockAccessListsSyncFeedTests
{
    private IBlockTree _blockTree = null!;
    private IBlockAccessListStore _blockAccessListStore = null!;
    private ISyncPeerPool _syncPeerPool = null!;
    private IDb _metadataDb = null!;
    private MemorySyncPointers _syncPointers = null!;
    private BlockAccessListsSyncFeed _feed = null!;

    [SetUp]
    public void Setup()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _blockAccessListStore = Substitute.For<IBlockAccessListStore>();
        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _metadataDb = new TestMemDb();
        _syncPointers = new MemorySyncPointers();

        _blockTree.SyncPivot.Returns((1UL, TestItem.KeccakA));

        _feed = CreateFeed(SpecProviderWithBlockAccessLists());
        _feed.InitializeFeed();
    }

    private BlockAccessListsSyncFeed CreateFeed(ISpecProvider specProvider) =>
        new(
            specProvider,
            _blockTree,
            _blockAccessListStore,
            _syncPointers,
            _syncPeerPool,
            new TestSyncConfig { FastSync = true, PivotNumber = 1 },
            new NullSyncReport(),
            _metadataDb,
            LimboLogs.Instance);

    private static TestSpecProvider SpecProviderWithBlockAccessLists() =>
        new(new ReleaseSpec())
        {
            ForkOnBlockNumber = 2,
            NextForkSpec = new ReleaseSpec { IsEip7928Enabled = true }
        };

    [TearDown]
    public void TearDown()
    {
        _feed.Dispose();
        _syncPeerPool.DisposeAsync();
        _metadataDb.Dispose();
    }

    [Test]
    public void Rejects_block_access_list_with_wrong_header_hash()
    {
        byte[] wrongBal = [0xc1, 0x01];
        BlockHeader header = Build.A.BlockHeader
            .WithBlockAccessListHash(TestItem.KeccakB)
            .TestObject;
        BlockInfo blockInfo = new(TestItem.KeccakA, 1) { BlockNumber = 1 };
        PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
        _blockTree.FindHeader(blockInfo.BlockHash, blockNumber: blockInfo.BlockNumber).Returns(header);

        BlockAccessListsSyncBatch batch = new([blockInfo])
        {
            Response = BuildBlockAccessLists(wrongBal),
            ResponseSourcePeer = peerInfo
        };

        SyncResponseHandlingResult result = _feed.HandleResponse(batch);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.NoProgress));
        _blockAccessListStore.DidNotReceive().Insert(Arg.Any<ulong>(), Arg.Any<Hash256>(), Arg.Any<byte[]>());
        _syncPeerPool.Received(1).ReportBreachOfProtocol(
            peerInfo,
            DisconnectReason.InvalidTxOrUncle,
            Arg.Is<string>(static message => message.Contains("invalid block access list")));
    }

    [Test]
    public async Task Treats_pivot_without_block_access_list_hash_as_temporarily_finished()
    {
        const ulong previousBarrier = 123;
        _metadataDb.Set(MetadataDbKeys.BlockAccessListsBarrierWhenStarted, previousBarrier.ToBigEndianByteArrayWithoutLeadingZeros());

        BlockHeader pivotHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(null)
            .TestObject;
        _blockTree.SyncPivot.Returns((2UL, TestItem.KeccakB));
        _blockTree.FindHeader(TestItem.KeccakB, blockNumber: 2).Returns(pivotHeader);

        _feed.InitializeFeed();

        ulong barrier = _metadataDb.Get(MetadataDbKeys.BlockAccessListsBarrierWhenStarted).ToULongFromBigEndianByteArrayWithoutLeadingZeros();
        Assert.That(barrier, Is.EqualTo(previousBarrier));
        _blockAccessListStore.DidNotReceive().Exists(Arg.Any<ulong>(), TestItem.KeccakB);
        Assert.That(_feed.IsFinished, Is.True);
        _feed.Activate();
        Assert.That(await _feed.PrepareRequest(), Is.Null);
        Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Dormant));
        Assert.That(_syncPointers.LowestInsertedBlockAccessListBlockNumber, Is.Null);

        BlockHeader balPivotHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(TestItem.KeccakA)
            .TestObject;
        _blockTree.SyncPivot.Returns((3UL, TestItem.KeccakC));
        _blockTree.FindHeader(TestItem.KeccakC, blockNumber: 3UL).Returns(balPivotHeader);

        Assert.That(_feed.IsFinished, Is.False);
        _feed.SyncModeSelectorOnChanged(SyncMode.FastBlockAccessLists);
        Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Active));
    }

    [Test]
    public async Task Treats_unscheduled_block_access_lists_as_permanently_finished()
    {
        _feed.Dispose();
        _feed = CreateFeed(new TestSpecProvider(new ReleaseSpec()));

        BlockHeader balPivotHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(TestItem.KeccakA)
            .TestObject;
        _blockTree.SyncPivot.Returns((2UL, TestItem.KeccakB));
        _blockTree.FindHeader(TestItem.KeccakB, blockNumber: 2UL).Returns(balPivotHeader);

        _feed.InitializeFeed();
        _feed.Activate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_feed.IsFinished, Is.True);
            Assert.That(await _feed.PrepareRequest(), Is.Null);
            Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Finished));
            Assert.That(_syncPointers.LowestInsertedBlockAccessListBlockNumber, Is.Null);
        }
    }

    [Test]
    public async Task Wakes_and_requests_block_access_lists_when_pivot_changes_to_bal_enabled_before_full_sync_runs()
    {
        (ulong BlockNumber, Hash256 BlockHash) syncPivot = (2UL, TestItem.KeccakB);
        BlockHeader preBalPivotHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(null)
            .TestObject;
        _blockTree.SyncPivot.Returns(_ => syncPivot);
        _blockTree.FindHeader(TestItem.KeccakB, blockNumber: 2UL).Returns(preBalPivotHeader);
        _feed.InitializeFeed();

        TestSyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = 2,
        };
        ISyncProgressResolver syncProgressResolver = CreateProgressResolver(syncConfig, () => syncPivot);
        ISyncPeerPool syncPeerPool = CreatePeerPool(10UL);
        using MultiSyncModeSelector selector = new(
            syncProgressResolver,
            syncPeerPool,
            syncConfig,
            No.BeaconSync,
            new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance),
            LimboLogs.Instance);
        await selector.StopAsync();
        selector.Changed += (_, args) => _feed.SyncModeSelectorOnChanged(args.Current);

        selector.Update();

        Assert.That(selector.Current, Is.EqualTo(SyncMode.Full));
        Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Dormant));

        BlockInfo block3Info = new(TestItem.KeccakC, 3) { BlockNumber = 3 };
        BlockInfo block2Info = new(TestItem.KeccakB, 2) { BlockNumber = 2 };
        BlockInfo block1Info = new(TestItem.KeccakA, 1) { BlockNumber = 1 };
        BlockHeader balPivotHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(TestItem.KeccakA)
            .TestObject;
        BlockHeader previousBalHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(TestItem.KeccakB)
            .TestObject;
        BlockHeader preBalHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(null)
            .TestObject;

        syncConfig.PivotNumber = 3;
        syncPivot = (3UL, TestItem.KeccakC);
        _blockTree.FindHeader(TestItem.KeccakC, blockNumber: 3UL).Returns(balPivotHeader);
        _blockTree.FindHeader(TestItem.KeccakB, blockNumber: 2UL).Returns(previousBalHeader);
        _blockTree.FindHeader(TestItem.KeccakA, blockNumber: 1UL).Returns(preBalHeader);
        _blockTree.FindCanonicalBlockInfo(3UL).Returns(block3Info);
        _blockTree.FindCanonicalBlockInfo(2UL).Returns(block2Info);
        _blockTree.FindCanonicalBlockInfo(1UL).Returns(block1Info);

        selector.Update();

        Assert.That(selector.Current, Is.EqualTo(SyncMode.Full | SyncMode.FastBlockAccessLists));
        Assert.That(_feed.CurrentState, Is.EqualTo(SyncFeedState.Active));

        using BlockAccessListsSyncBatch batch = (await _feed.PrepareRequest())!;
        Assert.That(batch, Is.Not.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Prioritized, Is.True);
            Assert.That(batch.Infos[0], Is.EqualTo(block3Info));
            Assert.That(batch.Infos[1], Is.EqualTo(block2Info));
            Assert.That(batch.Infos[2], Is.Null);
        }
    }

    [Test]
    public void Treats_absent_bal_as_unavailable_without_penalizing_peer()
    {
        BlockInfo blockInfo = new(TestItem.KeccakA, 1) { BlockNumber = 1 };
        PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());

        BlockAccessListsSyncBatch batch = new([blockInfo])
        {
            Response = BuildBlockAccessLists((byte[]?)null),
            ResponseSourcePeer = peerInfo
        };

        SyncResponseHandlingResult result = _feed.HandleResponse(batch);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.NoProgress));
        _blockAccessListStore.DidNotReceive().Insert(Arg.Any<ulong>(), Arg.Any<Hash256>(), Arg.Any<byte[]>());
        _syncPeerPool.DidNotReceive().ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            Arg.Any<DisconnectReason>(),
            Arg.Any<string>());
    }

    private ISyncProgressResolver CreateProgressResolver(
        ISyncConfig syncConfig,
        Func<(ulong BlockNumber, Hash256 BlockHash)> getSyncPivot)
    {
        ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
        syncProgressResolver.FindBestHeader().Returns(_ => syncConfig.PivotNumber);
        syncProgressResolver.FindBestFullBlock().Returns(0UL);
        syncProgressResolver.FindBestFullState().Returns(_ => syncConfig.PivotNumber);
        syncProgressResolver.FindBestProcessedBlock().Returns(0UL);
        syncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
        syncProgressResolver.GetTotalDifficulty(Arg.Any<Hash256>()).Returns((UInt256?)null);
        syncProgressResolver.SyncPivot.Returns(_ => getSyncPivot());
        syncProgressResolver.IsFastBlocksHeadersFinished().Returns(true);
        syncProgressResolver.IsFastBlocksBodiesFinished().Returns(true);
        syncProgressResolver.IsFastBlocksReceiptsFinished().Returns(true);
        syncProgressResolver.IsFastBlockAccessListsFinished().Returns(_ => _feed.IsFinished);
        _syncPeerPool
            .EstimateRequestLimit(Arg.Any<RequestType>(), Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<int?>(3));

        return syncProgressResolver;
    }

    private static ISyncPeerPool CreatePeerPool(ulong peerHeadNumber)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.HeadHash.Returns(TestItem.KeccakE);
        syncPeer.HeadNumber.Returns(peerHeadNumber);
        syncPeer.TotalDifficulty.Returns((UInt256)peerHeadNumber);
        syncPeer.IsInitialized.Returns(true);
        PeerInfo peerInfo = new(syncPeer);

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        syncPeerPool.InitializedPeers.Returns([peerInfo]);
        syncPeerPool.AllPeers.Returns([peerInfo]);
        return syncPeerPool;
    }

    private static IOwnedReadOnlyList<byte[]?> BuildBlockAccessLists(params byte[]?[] blockAccessLists) =>
        new ArrayPoolList<byte[]?>(blockAccessLists);
}
