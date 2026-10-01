// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        IForwardHeaderProvider headers = Substitute.For<IForwardHeaderProvider>();
        IBeaconSyncStrategy beacon = Substitute.For<IBeaconSyncStrategy>();
        IHistoryPruner history = Substitute.For<IHistoryPruner>();
        byte[] bal = Rlp.Encode(new ReadOnlyBlockAccessList()).Bytes;
        SyncConfig config = new() { ReconstructFinalizedStateFromBlockAccessLists = true };
        await using IContainer node = CreateMergeNode(builder => builder
            .AddSingleton<IForwardHeaderProvider>(headers)
            .AddSingleton<IBeaconSyncStrategy>(beacon)
            .AddSingleton<IHistoryPruner>(history)
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance)), config);
        Context ctx = node.Resolve<Context>();
        BlockHeader parent = ctx.BlockTree.Genesis!;
        TxReceipt[] receipts = [new TxReceipt { StatusCode = 1, GasUsedTotal = 21000, Bloom = Bloom.Empty, Logs = [] }];
        Hash256 receiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, Amsterdam.Instance, null);
        Block block = Build.A.Block.WithParent(new Block(parent))
            .WithDifficulty(0).WithStateRoot(TestItem.KeccakC).WithBlockAccessListHash(Keccak.Compute(bal))
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).WithReceiptsRoot(receiptsRoot).TestObject;
        ctx.BlockTree.Insert(block.Header, BlockTreeInsertHeaderOptions.BeaconHeaderInsert);
        bool suggested = false;
        bool receiptsAtSuggestion = false;
        ctx.BlockTree.NewBestSuggestedBlock += (_, args) =>
        {
            if (args.Block.Hash != block.Hash) return;
            suggested = true;
            receiptsAtSuggestion = ctx.ReceiptStorage.HasBlock(block.Number, block.Hash!);
        };
        beacon.MergeTransitionFinished.Returns(true);
        beacon.GetFinalizedHash().Returns(block.Hash);
        headers.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(new ArrayPoolList<BlockHeader?>(2) { parent, block.Header }));
        BlockDownloader downloader = (BlockDownloader)ctx.FullSyncFeedComponent.BlockDownloader;
        PeerInfo peer = new(Substitute.For<ISyncPeer>());
        peer.SyncPeer.ProtocolVersion.Returns(EthVersions.Eth71);

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
}
