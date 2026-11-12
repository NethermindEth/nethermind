// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Contract.P2P;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
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
        balRequest.BlockAccessLists = missingBal && truncated
            ? new ArrayPoolList<byte[]?>(0)
            : BuildBlockAccessLists(missingBal ? null : bal);
        // A peer that answers without the list sleeps for lists. Without another list peer, the block executes without it.
        downloader.HandleResponse(balRequest, peer);
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(receiptRequest!.ReceiptsRequests, Has.Count.EqualTo(1));
                Assert.That(suggested, Is.False);
            }
            for (int attempt = 0; attempt < (missingReceipts ? 3 : 1); attempt++)
            {
                receiptRequest!.Receipts = missingReceipts && truncated
                    ? new ArrayPoolList<TxReceipt[]?>(0)
                    : new ArrayPoolList<TxReceipt[]?>(1) { missingReceipts ? null : receipts };
                downloader.HandleResponse(receiptRequest, peer);
                receiptRequest = await downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
                if (missingReceipts && attempt < 2)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(receiptRequest!.ReceiptsRequests, Has.Count.EqualTo(1));
                        Assert.That(suggested, Is.False);
                    }
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

    // Without a peer that serves lists, or with one behind the block, the block executes at once, without a list or receipts.
    // A block whose list is stored waits for its receipts, so it reconstructs.
    [TestCase(EthVersions.Eth70, 1UL, false)]
    [TestCase(EthVersions.Eth71, 0UL, false)]
    [TestCase(EthVersions.Eth70, 1UL, true)]
    [TestCase(EthVersions.Eth71, 0UL, true)]
    public async Task Finalized_catchup_executes_at_once_without_a_peer_that_serves_the_list(byte protocolVersion, ulong peerHead, bool stored)
    {
        await using CatchUp catchUp = CreateCatchUp(1);
        catchUp.Peer.SyncPeer.ProtocolVersion.Returns(protocolVersion);
        catchUp.Peer.SyncPeer.HeadNumber.Returns(peerHead);
        Block block = catchUp.Blocks[0];
        if (stored) catchUp.BalStore.Insert(block.Number, block.Hash!, catchUp.Bal);
        bool suggested = false;
        bool receiptsAtSuggestion = false;
        catchUp.Ctx.BlockTree.NewBestSuggestedBlock += (_, args) =>
        {
            if (args.Block.Hash != block.Hash) return;
            suggested = true;
            receiptsAtSuggestion = catchUp.Ctx.ReceiptStorage.HasBlock(block.Number, block.Hash!);
        };

        BlocksRequest bodyRequest = (await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        bodyRequest.OwnedBodies = new OwnedBlockBodies([block.Body]);
        catchUp.Downloader.HandleResponse(bodyRequest, catchUp.Peer);
        BlocksRequest? next = await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
        if (stored)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(next?.ReceiptsRequests.Select(static h => h.Number), Is.EqualTo(new[] { block.Number }));
                Assert.That(suggested, Is.False);
            }
            next!.Receipts = new ArrayPoolList<TxReceipt[]?>(1) { catchUp.Receipts };
            catchUp.Downloader.HandleResponse(next, catchUp.Peer);
            next = await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(next, Is.Null);
            Assert.That(suggested, Is.True);
            Assert.That(receiptsAtSuggestion, Is.EqualTo(stored));
        }
    }

    [Test]
    public async Task Finalized_catchup_downloads_receipts_of_a_served_list_while_the_list_peer_sleeps()
    {
        await using CatchUp catchUp = CreateCatchUp(2);
        Block block = catchUp.Blocks[0];
        bool receiptsAtSuggestion = false;
        catchUp.Ctx.BlockTree.NewBestSuggestedBlock += (_, args) =>
        {
            if (args.Block.Hash == block.Hash) receiptsAtSuggestion = catchUp.Ctx.ReceiptStorage.HasBlock(block.Number, block.Hash!);
        };

        BlocksRequest bodyRequest = (await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        bodyRequest.OwnedBodies = new OwnedBlockBodies([.. catchUp.Blocks.Select(static b => b.Body)]);
        catchUp.Downloader.HandleResponse(bodyRequest, catchUp.Peer);
        // The only list peer serves the first list and withholds the second, so it sleeps for lists.
        BlocksRequest balRequest = (await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        balRequest.BlockAccessLists = BuildBlockAccessLists(catchUp.Bal, null);
        catchUp.Downloader.HandleResponse(balRequest, catchUp.Peer);

        BlocksRequest? receiptRequest = await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
        Assert.That(receiptRequest?.ReceiptsRequests.Select(static h => h.Number), Is.EqualTo(new[] { block.Number }));
        receiptRequest!.Receipts = new ArrayPoolList<TxReceipt[]?>(1) { catchUp.Receipts };
        catchUp.Downloader.HandleResponse(receiptRequest, catchUp.Peer);
        await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);

        Assert.That(receiptsAtSuggestion, Is.True);
    }

    [Test]
    public async Task Finalized_catchup_takes_a_late_list_reply_for_released_blocks_as_progress()
    {
        await using CatchUp catchUp = CreateCatchUp(2);
        catchUp.Ctx.PeerPool
            .EstimateRequestLimit(RequestType.BlockAccessLists, Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<int?>(1));
        BlocksRequest bodyRequest = (await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        bodyRequest.OwnedBodies = new OwnedBlockBodies([.. catchUp.Blocks.Select(static b => b.Body)]);
        catchUp.Downloader.HandleResponse(bodyRequest, catchUp.Peer);

        // A peer can hold two requests at once. The receipts are asked between the two list requests.
        BlocksRequest first = (await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
        BlocksRequest second = (await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.BlockAccessListsRequests.Select(static h => h.Number), Is.EqualTo(new[] { catchUp.Blocks[0].Number }));
            Assert.That(second.BlockAccessListsRequests.Select(static h => h.Number), Is.EqualTo(new[] { catchUp.Blocks[1].Number }));
        }
        first.BlockAccessLists = BuildBlockAccessLists((byte[]?)null);
        catchUp.Downloader.HandleResponse(first, catchUp.Peer);
        // The only list peer sleeps for lists, so both blocks leave the buffer without their lists.
        await catchUp.Downloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None);
        second.BlockAccessLists = BuildBlockAccessLists(catchUp.Bal);

        Assert.That(catchUp.Downloader.HandleResponse(second, catchUp.Peer), Is.EqualTo(SyncResponseHandlingResult.OK));
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
    private CatchUp CreateCatchUp(int blockCount, bool reconstruct = true, IBlocksConfig? blocksConfig = null, Action<ContainerBuilder>? configurer = null)
    {
        IForwardHeaderProvider headers = Substitute.For<IForwardHeaderProvider>();
        IBeaconSyncStrategy beacon = Substitute.For<IBeaconSyncStrategy>();
        byte[] bal = Rlp.Encode(new ReadOnlyBlockAccessList()).Bytes;
        IContainer node = CreateMergeNode(builder =>
            {
                builder
                    .AddSingleton<IForwardHeaderProvider>(headers)
                    .AddSingleton<IBeaconSyncStrategy>(beacon)
                    .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance));
                configurer?.Invoke(builder);
            },
            new SyncConfig { ReconstructFinalizedStateFromBlockAccessLists = reconstruct }, blocksConfig ?? new BlocksConfig());
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
        public IBlockAccessListStore BalStore => _node.Resolve<IBlockAccessListStore>();

        public CatchUp(IContainer node, Block[] blocks, TxReceipt[] receipts, byte[] bal)
        {
            _node = node;
            Ctx = node.Resolve<Context>();
            Blocks = blocks;
            Receipts = receipts;
            Bal = bal;
            Downloader = (BlockDownloader)Ctx.FullSyncFeedComponent.BlockDownloader;
            Peer.SyncPeer.ProtocolVersion.Returns(EthVersions.Eth71);
            Peer.SyncPeer.HeadNumber.Returns(blocks[^1].Number);
            Ctx.PeerPool.InitializedPeers.Returns([Peer]);
        }

        public ValueTask DisposeAsync() => _node.DisposeAsync();
    }
}
