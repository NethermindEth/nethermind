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
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class ReceiptSyncFeedTests
{

    [Test]
    public async Task ShouldRecoverOnInsertFailure()
    {
        InMemoryReceiptStorage syncingFromReceiptStore = new InMemoryReceiptStorage();
        BlockTree syncingFromBlockTree = Build.A.BlockTree()
            .WithTransactions(syncingFromReceiptStore)
            .OfChainLength(100)
            .TestObject;

        BlockTree syncingTooBlockTree = Build.A.BlockTree()
            .TestObject;

        for (int i = 1; i < 100; i++)
        {
            Block block = syncingFromBlockTree.FindBlock(i, BlockTreeLookupOptions.None)!;
            syncingTooBlockTree.Insert(block.Header);
            syncingTooBlockTree.Insert(block);
        }

        Block pivot = syncingFromBlockTree.FindBlock(99, BlockTreeLookupOptions.None)!;

        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotHash = pivot.Hash!.ToString(),
            PivotNumber = pivot.Number.ToString(),
            AncientBodiesBarrier = 0,
            FastBlocks = true,
            DownloadBodiesInFastSync = true,
        };

        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        ReceiptsSyncFeed syncFeed = new ReceiptsSyncFeed(
            MainnetSpecProvider.Instance,
            syncingTooBlockTree,
            receiptStorage,
            Substitute.For<ISyncPeerPool>(),
            syncConfig,
            new NullSyncReport(),
            LimboLogs.Instance
        );
        syncFeed.InitializeFeed();

        ReceiptsSyncBatch req = (await syncFeed.PrepareRequest())!;
        req.Response = req.Infos.Take(8).Select((info) => syncingFromReceiptStore.Get(info!.BlockHash)).ToArray();

        receiptStorage
            .When((it) => it.Insert(Arg.Any<Block>(), Arg.Any<TxReceipt[]?>(), Arg.Any<bool>()))
            .Do((callInfo) =>
            {
                Block block = (Block)callInfo[0];
                if (block.Number == 95) throw new Exception("test exception");
            });

        Func<SyncResponseHandlingResult> act = () => syncFeed.HandleResponse(req);
        act.Should().Throw<Exception>();
        req = (await syncFeed.PrepareRequest())!;

        req.Infos[0]!.BlockNumber.Should().Be(95);
    }
}
