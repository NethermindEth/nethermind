// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.History;
using Nethermind.Network.Contract.P2P;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, false, true)]
    public async Task Finalized_catchup_downloads_retained_receipts_before_suggestion(bool missingBal, bool missingReceipts, bool truncated)
    {
        await using CatchUp catchUp = CreateCatchUp(1);
        (Context ctx, Block block, TxReceipt[] receipts, byte[] bal, BlockDownloader downloader, PeerInfo peer) =
            (catchUp.Ctx, catchUp.Blocks[0], catchUp.Receipts, catchUp.Bal, catchUp.Downloader, catchUp.Peer);
        bool suggested = false;
        bool receiptsAtSuggestion = false;
        ctx.BlockTree.NewBestSuggestedBlock += (_, args) =>
        {
            if (args.Block.Hash != block.Hash) return;
            suggested = true;
            receiptsAtSuggestion = ctx.ReceiptStorage.HasBlock(block.Number, block.Hash!);
        };

        BlocksRequest bodyRequest = (await downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        bodyRequest.OwnedBodies = new OwnedBlockBodies([block.Body]);
        downloader.HandleResponse(bodyRequest, peer);
        BlocksRequest balRequest = (await downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(balRequest.BlockAccessListsRequests, Has.Count.EqualTo(1));
            Assert.That(suggested, Is.False);
        }
        for (int attempt = 0; attempt < (missingBal ? 3 : 1); attempt++)
        {
            balRequest.BlockAccessLists = missingBal && truncated
                ? new ArrayPoolList<byte[]?>(0)
                : BuildBlockAccessLists(missingBal ? null : bal);
            downloader.HandleResponse(balRequest, peer);
            if (missingBal && attempt < 2)
            {
                balRequest = (await downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
                Assert.That(balRequest.BlockAccessListsRequests, Has.Count.EqualTo(1));
                Assert.That(suggested, Is.False);
            }
        }
        if (missingBal)
        {
            BlocksRequest fastRequest = (await downloader.PrepareRequest(DownloaderOptions.Insert, 0, CancellationToken.None))!;
            Assert.That(fastRequest.BlockAccessListsRequests, Has.Count.EqualTo(1));
            fastRequest.BlockAccessLists = BuildBlockAccessLists((byte[]?)null);
            downloader.HandleResponse(fastRequest, peer);
        }
        BlocksRequest? receiptRequest = await downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
        if (!missingBal)
        {
            Assert.That(receiptRequest!.ReceiptsRequests, Has.Count.EqualTo(1));
            Assert.That(suggested, Is.False);
            for (int attempt = 0; attempt < (missingReceipts ? 3 : 1); attempt++)
            {
                receiptRequest!.Receipts = missingReceipts && truncated
                    ? new ArrayPoolList<TxReceipt[]?>(0)
                    : new ArrayPoolList<TxReceipt[]?>(1) { missingReceipts ? null : receipts };
                downloader.HandleResponse(receiptRequest, peer);
                receiptRequest = await downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
                if (missingReceipts && attempt < 2)
                {
                    Assert.That(receiptRequest!.ReceiptsRequests, Has.Count.EqualTo(1));
                    Assert.That(suggested, Is.False);
                }
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiptRequest, Is.Null);
            Assert.That(suggested, Is.True);
            Assert.That(receiptsAtSuggestion, Is.EqualTo(!missingBal && !missingReceipts));
        }
    }

    [Test]
    public async Task Finalized_catchup_keeps_requesting_data_cut_short_by_peer_limits()
    {
        const int blockCount = 4;
        await using CatchUp catchUp = CreateCatchUp(blockCount);
        BlocksRequest request = (await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        request.OwnedBodies = new OwnedBlockBodies([.. catchUp.Blocks.Select(static b => b.Body)]);
        catchUp.Downloader.HandleResponse(request, catchUp.Peer);

        // Every reply serves only its first entry, so the last block is cut short more often than the retry limit.
        List<ulong> servedAccessLists = [];
        List<ulong> servedReceipts = [];
        for (int round = 0; round < 4 * blockCount; round++)
        {
            BlocksRequest? next = await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
            if (next is null) break;
            if (next.BlockAccessListsRequests.Count > 0)
            {
                servedAccessLists.Add(next.BlockAccessListsRequests[0].Number);
                next.BlockAccessLists = BuildBlockAccessLists(catchUp.Bal);
            }
            if (next.ReceiptsRequests.Count > 0)
            {
                servedReceipts.Add(next.ReceiptsRequests[0].Number);
                next.Receipts = new ArrayPoolList<TxReceipt[]?>(1) { catchUp.Receipts };
            }
            catchUp.Downloader.HandleResponse(next, catchUp.Peer);
        }

        ulong[] all = [.. catchUp.Blocks.Select(static b => b.Number)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(servedAccessLists, Is.EqualTo(all));
            Assert.That(servedReceipts, Is.EqualTo(all));
        }
    }

    /// <summary>Creates a node whose finalized beacon chain holds <paramref name="blockCount"/> blocks awaiting catch-up.</summary>
    private CatchUp CreateCatchUp(int blockCount)
    {
        IForwardHeaderProvider headers = Substitute.For<IForwardHeaderProvider>();
        IBeaconSyncStrategy beacon = Substitute.For<IBeaconSyncStrategy>();
        byte[] bal = Rlp.Encode(new ReadOnlyBlockAccessList()).Bytes;
        IContainer node = CreateMergeNode(builder => builder
            .AddSingleton<IForwardHeaderProvider>(headers)
            .AddSingleton<IBeaconSyncStrategy>(beacon)
            .AddSingleton<IHistoryPruner>(Substitute.For<IHistoryPruner>())
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance)),
            new SyncConfig { ReconstructFinalizedStateFromBlockAccessLists = true });
        Context ctx = node.Resolve<Context>();
        TxReceipt[] receipts = [new TxReceipt { StatusCode = 1, GasUsedTotal = 21000, Bloom = Bloom.Empty, Logs = [] }];
        Hash256 receiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, Amsterdam.Instance, null);
        Block[] blocks = new Block[blockCount];
        Block parent = new(ctx.BlockTree.Genesis!);
        for (int i = 0; i < blockCount; i++)
        {
            parent = blocks[i] = Build.A.Block.WithParent(parent)
                .WithDifficulty(0).WithStateRoot(TestItem.KeccakC).WithBlockAccessListHash(Keccak.Compute(bal))
                .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).WithReceiptsRoot(receiptsRoot).TestObject;
            ctx.BlockTree.Insert(parent.Header, BlockTreeInsertHeaderOptions.BeaconHeaderInsert);
        }

        ulong suggestedUpTo = 0;
        ctx.BlockTree.NewBestSuggestedBlock += (_, args) => suggestedUpTo = args.Block.Number;
        beacon.MergeTransitionFinished.Returns(true);
        beacon.GetFinalizedHash().Returns(blocks[^1].Hash);
        headers.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(new ArrayPoolList<BlockHeader?>(blockCount + 1,
                blocks.Select(static b => b.Header).Prepend(ctx.BlockTree.Genesis!)
                    .SkipWhile(h => h.Number < suggestedUpTo))));
        return new CatchUp(node, blocks, receipts, bal);
    }

    private sealed class CatchUp : IAsyncDisposable
    {
        private readonly IContainer _node;
        public Context Ctx { get; }
        public Block[] Blocks { get; }
        public TxReceipt[] Receipts { get; }
        public byte[] Bal { get; }
        public BlockDownloader Downloader { get; }
        public PeerInfo Peer { get; } = new(Substitute.For<ISyncPeer>());

        public CatchUp(IContainer node, Block[] blocks, TxReceipt[] receipts, byte[] bal)
        {
            _node = node;
            Ctx = node.Resolve<Context>();
            Blocks = blocks;
            Receipts = receipts;
            Bal = bal;
            Downloader = (BlockDownloader)Ctx.FullSyncFeedComponent.BlockDownloader;
            Peer.SyncPeer.ProtocolVersion.Returns(EthVersions.Eth71);
        }

        public ValueTask DisposeAsync() => _node.DisposeAsync();
    }
}
