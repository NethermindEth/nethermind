// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    [Test]
    public async Task ShouldCallFlushPeriodically()
    {
        BlockTree syncingFromBlockTree = Build.A.BlockTree()
            .OfChainLength(100)
            .TestObject;

        TestMemDb blocksDb = new TestMemDb();
        BlockTree syncingTooBlockTree = Build.A.BlockTree()
            .WithBlocksDb(blocksDb)
            .TestObject;

        for (int i = 1; i < 100; i++)
        {
            Block block = syncingFromBlockTree.FindBlock(i, BlockTreeLookupOptions.None)!;
            syncingTooBlockTree.Insert(block.Header);
        }

        Block pivot = syncingFromBlockTree.FindBlock(99, BlockTreeLookupOptions.None)!;

        SyncConfig syncConfig = new SyncConfig()
        {
            FastSync = true,
            PivotHash = pivot.Hash!.ToString(),
            PivotNumber = pivot.Number.ToString(),
            AncientBodiesBarrier = 0,
            FastBlocks = true,
            DownloadBodiesInFastSync = true,
        };

        BodiesSyncFeed syncFeed = new BodiesSyncFeed(
            Substitute.For<ISyncModeSelector>(),
            syncingTooBlockTree,
            Substitute.For<ISyncPeerPool>(),
            syncConfig,
            new NullSyncReport(),
            blocksDb,
            LimboLogs.Instance,
            flushDbInterval: 10
        );

        syncFeed.InitializeFeed();
        BodiesSyncBatch req = (await syncFeed.PrepareRequest())!;
        blocksDb.FlushCount.Should().Be(1);

        async Task HandleAndPrepareNextRequest()
        {
            req.Response = new OwnedBlockBodies(req.Infos.Take(8).Select((info) =>
                syncingFromBlockTree.FindBlock(info!.BlockNumber, BlockTreeLookupOptions.None)!.Body).ToArray());

            syncFeed.HandleResponse(req);
            req = (await syncFeed.PrepareRequest())!;
        }

        await HandleAndPrepareNextRequest();
        blocksDb.FlushCount.Should().Be(1);

        await HandleAndPrepareNextRequest();
        blocksDb.FlushCount.Should().Be(2);

        await HandleAndPrepareNextRequest();
        blocksDb.FlushCount.Should().Be(2);

        await HandleAndPrepareNextRequest();
        blocksDb.FlushCount.Should().Be(3);
    }
}
