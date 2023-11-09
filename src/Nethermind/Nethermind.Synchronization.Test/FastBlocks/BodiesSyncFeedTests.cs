// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastBlocks;

public class BodiesSyncFeedTests
{
    private BlockTree _syncingFromBlockTree = null!;
    private TestMemDb _blocksDb = null!;
    private BodiesSyncFeed _syncFeed = null!;

    [SetUp]
    public void Setup()
    {
        _syncingFromBlockTree = Build.A.BlockTree()
            .OfChainLength(100)
            .TestObject;

        _blocksDb = new TestMemDb();
        BlockTree syncingTooBlockTree = Build.A.BlockTree()
            .WithBlocksDb(_blocksDb)
            .TestObject;

        for (int i = 1; i < 100; i++)
        {
            Block block = _syncingFromBlockTree.FindBlock(i, BlockTreeLookupOptions.None)!;
            syncingTooBlockTree.Insert(block.Header);
        }

        Block pivot = _syncingFromBlockTree.FindBlock(99, BlockTreeLookupOptions.None)!;

        SyncConfig syncConfig = new SyncConfig()
        {
            FastSync = true,
            PivotHash = pivot.Hash!.ToString(),
            PivotNumber = pivot.Number.ToString(),
            AncientBodiesBarrier = 0,
            FastBlocks = true,
            DownloadBodiesInFastSync = true,
        };

        _syncFeed = new BodiesSyncFeed(
            syncingTooBlockTree,
            Substitute.For<ISyncPeerPool>(),
            syncConfig,
            new NullSyncReport(),
            _blocksDb,
            LimboLogs.Instance,
            flushDbInterval: 10
        );
        _syncFeed.InitializeFeed();
    }

    [Test]
    public async Task ShouldCallFlushPeriodically()
    {
        BodiesSyncBatch req = (await _syncFeed.PrepareRequest())!;
        _blocksDb.FlushCount.Should().Be(1);

        async Task HandleAndPrepareNextRequest()
        {
            req.Response = new OwnedBlockBodies(req.Infos.Take(8).Select((info) =>
                _syncingFromBlockTree.FindBlock(info!.BlockNumber, BlockTreeLookupOptions.None)!.Body).ToArray());

            _syncFeed.HandleResponse(req);
            req = (await _syncFeed.PrepareRequest())!;
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
        BodiesSyncBatch req = (await _syncFeed.PrepareRequest())!;

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

        Func<SyncResponseHandlingResult> act = () => _syncFeed.HandleResponse(req);
        act.Should().Throw<Exception>();

        req = (await _syncFeed.PrepareRequest())!;

        req.Infos[0]!.BlockNumber.Should().Be(95);
    }
}
