// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class ReceiptSyncFeedTests
{
    private IBlockTree _syncingFromBlockTree = null!;
    private IBlockTree _syncingToBlockTree = null!;
    private ReceiptsSyncFeed _feed = null!;
    private ISyncConfig _syncConfig = null!;
    private Block _pivotBlock = null!;
    private InMemoryReceiptStorage _syncingFromReceiptStore;
    private IReceiptStorage _receiptStorage;
    private ISyncPeerPool _syncPeerPool = null!;

    [SetUp]
    public void Setup()
    {
        _syncingFromReceiptStore = new InMemoryReceiptStorage();
        _syncingFromBlockTree = Build.A.BlockTree()
            .WithTransactions(_syncingFromReceiptStore)
            .OfChainLength(100)
            .TestObject;

        _receiptStorage = Substitute.For<IReceiptStorage>();
        _syncingToBlockTree = Build.A.BlockTree()
            .TestObject;

        for (int i = 1; i < 100; i++)
        {
            Block block = _syncingFromBlockTree.FindBlock(i, BlockTreeLookupOptions.None)!;
            _syncingToBlockTree.Insert(block.Header);
            _syncingToBlockTree.Insert(block);
        }

        _pivotBlock = _syncingFromBlockTree.FindBlock(99, BlockTreeLookupOptions.None)!;

        _syncConfig = new TestSyncConfig()
        {
            FastSync = true,
            PivotHash = _pivotBlock.Hash!.ToString(),
            PivotNumber = _pivotBlock.Number.ToString(),
            AncientBodiesBarrier = 0,
            DownloadBodiesInFastSync = true,
        };
        _syncingToBlockTree.SyncPivot = (_pivotBlock.Number, _pivotBlock.Hash);

        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _feed = new ReceiptsSyncFeed(
            MainnetSpecProvider.Instance,
            _syncingToBlockTree,
            _receiptStorage,
            new MemorySyncPointers(),
            _syncPeerPool,
            _syncConfig,
            new NullSyncReport(),
            new MemDb(),
            LimboLogs.Instance
        );
    }

    [TearDown]
    public void TearDown()
    {
        _feed.Dispose();
        _syncPeerPool.DisposeAsync();
    }

    [Test]
    public async Task ShouldRecoverOnInsertFailure()
    {
        _feed.InitializeFeed();

        using ReceiptsSyncBatch req = (await _feed.PrepareRequest())!;
        req.Response = req.Infos.Take(8).Select(info => _syncingFromReceiptStore.Get(info!.BlockHash)).ToPooledList(8)!;

        _receiptStorage
            .When((it) => it.Insert(Arg.Any<Block>(), Arg.Any<TxReceipt[]?>(), Arg.Any<bool>()))
            .Do((callInfo) =>
            {
                Block block = (Block)callInfo[0];
                if (block.Number == 95) throw new Exception("test exception");
            });

        Func<SyncResponseHandlingResult> act = () => _feed.HandleResponse(req);
        act.Should().Throw<Exception>();
        using ReceiptsSyncBatch req2 = (await _feed.PrepareRequest())!;
        req2.Infos[0]!.BlockNumber.Should().Be(95);
    }

    [Test]
    public async Task ShouldNotRedownloadExistingReceipts()
    {
        _feed.InitializeFeed();
        _receiptStorage.HasBlock(Arg.Is(_pivotBlock.Number - 2), Arg.Any<Hash256>()).Returns(true);
        _receiptStorage.HasBlock(Arg.Is(_pivotBlock.Number - 4), Arg.Any<Hash256>()).Returns(true);

        using ReceiptsSyncBatch req = (await _feed.PrepareRequest())!;

        req.Infos
            .Where(static (bi) => bi is not null)
            .Select(static (bi) => bi!.BlockNumber)
            .Take(4)
            .Should()
            .BeEquivalentTo([
                _pivotBlock.Number,
                _pivotBlock.Number - 1,
                // Skipped
                _pivotBlock.Number - 3,
                // Skipped
                _pivotBlock.Number - 5]);
    }

    [Test]
    public async Task ShouldLimitBatchSizeToPeerEstimate()
    {
        _feed.InitializeFeed();
        _syncPeerPool.EstimateRequestLimit(RequestType.Receipts, Arg.Any<IPeerAllocationStrategy>(), AllocationContexts.Receipts, default)
            .Returns(Task.FromResult<int?>(5));
        ReceiptsSyncBatch req = (await _feed.PrepareRequest())!;
        req.Infos.Length.Should().Be(5);
    }
}
