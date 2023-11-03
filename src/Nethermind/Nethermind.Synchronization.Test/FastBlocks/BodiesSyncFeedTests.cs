// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastBlocks;

public class BodiesSyncFeedTests
{
    private IBlockTree _syncingFromBlockTree = null!;
    private IBlockTree _syncingToBlockTree = null!;
    private TestMemDb _blocksDb = null!;
    private BodiesSyncFeed _feed = null!;
    private ISyncConfig _syncConfig = null!;
    private MemDb _metadataDb = null!;
    private Block _pivotBlock = null!;

    [SetUp]
    public void Setup()
    {
        _syncingFromBlockTree = Build.A.BlockTree()
            .OfChainLength(100)
            .TestObject;

        _blocksDb = new TestMemDb();
        _metadataDb = new MemDb();
        _syncingToBlockTree = Build.A.BlockTree()
            .WithBlocksDb(_blocksDb)
            .TestObject;

        for (int i = 1; i < 100; i++)
        {
            Block block = _syncingFromBlockTree.FindBlock(i, BlockTreeLookupOptions.None)!;
            _syncingToBlockTree.Insert(block.Header);
        }

        _pivotBlock = _syncingFromBlockTree.FindBlock(99, BlockTreeLookupOptions.None)!;

        _syncConfig = new SyncConfig()
        {
            FastSync = true,
            PivotHash = _pivotBlock.Hash!.ToString(),
            PivotNumber = _pivotBlock.Number.ToString(),
            AncientBodiesBarrier = 0,
            FastBlocks = true,
            DownloadBodiesInFastSync = true,
        };

        _feed = new BodiesSyncFeed(
            MainnetSpecProvider.Instance,
            _syncingToBlockTree,
            Substitute.For<ISyncPeerPool>(),
            _syncConfig,
            new NullSyncReport(),
            _blocksDb,
            _metadataDb,
            LimboLogs.Instance,
            flushDbInterval: 10
        );
    }

    [Test]
    public async Task ShouldCallFlushPeriodically()
    {
        _feed.InitializeFeed();
        BodiesSyncBatch req = (await _feed.PrepareRequest())!;
        _blocksDb.FlushCount.Should().Be(1);

        async Task HandleAndPrepareNextRequest()
        {
            req.Response = new OwnedBlockBodies(req.Infos.Take(8).Select((info) =>
                _syncingFromBlockTree.FindBlock(info!.BlockNumber, BlockTreeLookupOptions.None)!.Body).ToArray());

            _feed.HandleResponse(req);
            req = (await _feed.PrepareRequest())!;
        }

        await HandleAndPrepareNextRequest();
        _blocksDb.FlushCount.Should().Be(1);

        await HandleAndPrepareNextRequest();
        _blocksDb.FlushCount.Should().Be(2);

        await HandleAndPrepareNextRequest();
        _blocksDb.FlushCount.Should().Be(2);

        await HandleAndPrepareNextRequest();
        _blocksDb.FlushCount.Should().Be(3);
    }

    [Test]
    public async Task ShouldRecoverOnInsertFailure()
    {
        _feed.InitializeFeed();
        BodiesSyncBatch req = (await _feed.PrepareRequest())!;

        req.Response = new OwnedBlockBodies(req.Infos.Take(8).Select((info) =>
            _syncingFromBlockTree.FindBlock(info!.BlockNumber, BlockTreeLookupOptions.None)!.Body).ToArray());

        int writeCount = 0;
        _blocksDb.WriteFunc = (k, value) =>
        {
            writeCount++;
            if (writeCount == 5)
            {
                throw new Exception("test failure");
            }
            return true;
        };

        Func<SyncResponseHandlingResult> act = () => _feed.HandleResponse(req);
        act.Should().Throw<Exception>();

        req = (await _feed.PrepareRequest())!;

        req.Infos[0]!.BlockNumber.Should().Be(95);
    }

    [TestCase(100, false, null, false)]
    [TestCase(11052930, false, null, true)]
    [TestCase(11052984, false, null, true)]
    [TestCase(11052985, false, null, false)]
    [TestCase(100, false, 11052984, false)]
    [TestCase(11052930, false, 11052984, true)]
    [TestCase(11052984, false, 11052984, true)]
    [TestCase(11052985, false, 11052984, false)]
    [TestCase(100, true, null, false)]
    [TestCase(11052930, true, null, false)]
    [TestCase(11052984, true, null, false)]
    [TestCase(11052985, true, null, false)]
    [TestCase(100, false, 0, false)]
    [TestCase(11052930, false, 0, false)]
    [TestCase(11052984, false, 0, false)]
    [TestCase(11052985, false, 0, false)]
    public async Task When_finished_sync_with_old_default_barrier_then_finishes_imedietely(
            long? lowestInsertedBlockNumber,
            bool JustStarted,
            long? previousBarrierInDb,
            bool shouldfinish)
    {
        _syncConfig.AncientReceiptsBarrier = 0;
        _syncingToBlockTree.LowestInsertedBodyNumber = JustStarted ? _pivotBlock.Number : _pivotBlock.Number - 1;
        if (previousBarrierInDb != null)
            _metadataDb.Set(MetadataDbKeys.BodiesBarrierWhenStarted, previousBarrierInDb.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        _feed.InitializeFeed();
        _syncingToBlockTree.LowestInsertedBodyNumber = lowestInsertedBlockNumber;

        BodiesSyncBatch? request = await _feed.PrepareRequest();
        if (shouldfinish)
        {
            request.Should().BeNull();
            _feed.CurrentState.Should().Be(SyncFeedState.Finished);
        }
        else
        {
            request.Should().NotBeNull();
            _feed.CurrentState.Should().NotBe(SyncFeedState.Finished);
        }
    }
}
