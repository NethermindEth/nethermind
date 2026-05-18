// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
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
    private BlockAccessListsSyncFeed _feed = null!;

    [SetUp]
    public void Setup()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _blockAccessListStore = Substitute.For<IBlockAccessListStore>();
        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _metadataDb = new TestMemDb();

        _blockTree.SyncPivot.Returns((1, TestItem.KeccakA));

        _feed = new BlockAccessListsSyncFeed(
            MainnetSpecProvider.Instance,
            _blockTree,
            _blockAccessListStore,
            new MemorySyncPointers(),
            _syncPeerPool,
            new TestSyncConfig { FastSync = true, PivotNumber = 1 },
            new NullSyncReport(),
            _metadataDb,
            LimboLogs.Instance);
        _feed.InitializeFeed();
    }

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
        _blockAccessListStore.DidNotReceive().Insert(Arg.Any<Hash256>(), Arg.Any<byte[]>());
        _syncPeerPool.Received(1).ReportBreachOfProtocol(
            peerInfo,
            DisconnectReason.InvalidTxOrUncle,
            Arg.Is<string>(static message => message.Contains("invalid block access list")));
    }

    [Test]
    public void Treats_pivot_without_block_access_list_hash_as_available()
    {
        const long previousBarrier = 123;
        _metadataDb.Set(MetadataDbKeys.BlockAccessListsBarrierWhenStarted, previousBarrier.ToBigEndianByteArrayWithoutLeadingZeros());

        BlockHeader pivotHeader = Build.A.BlockHeader
            .WithBlockAccessListHash(null)
            .TestObject;
        _blockTree.SyncPivot.Returns((2, TestItem.KeccakB));
        _blockTree.FindHeader(TestItem.KeccakB, blockNumber: 2).Returns(pivotHeader);

        _feed.InitializeFeed();

        long barrier = _metadataDb.Get(MetadataDbKeys.BlockAccessListsBarrierWhenStarted).ToLongFromBigEndianByteArrayWithoutLeadingZeros();
        Assert.That(barrier, Is.EqualTo(previousBarrier));
        _blockAccessListStore.DidNotReceive().Exists(TestItem.KeccakB);
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
        _blockAccessListStore.DidNotReceive().Insert(Arg.Any<Hash256>(), Arg.Any<byte[]>());
        _syncPeerPool.DidNotReceive().ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            Arg.Any<DisconnectReason>(),
            Arg.Any<string>());
    }

    private static IByteArrayList BuildBlockAccessLists(params byte[]?[] blockAccessLists)
    {
        using DeferredRlpItemList.Builder builder = new(entryCapacity: blockAccessLists.Length);
        using (DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer())
        {
            for (int i = 0; i < blockAccessLists.Length; i++)
            {
                writer.WriteValue(blockAccessLists[i] ?? ReadOnlySpan<byte>.Empty);
            }
        }

        return new RlpByteArrayList(builder.ToRlpItemList());
    }
}
