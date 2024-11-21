// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using Nethermind.Stats.Model;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Nethermind.Db;

namespace Nethermind.Synchronization.Test;

[Parallelizable(ParallelScope.All)]
public partial class BlockDownloaderTests
{
    [TestCase(1L, DownloaderOptions.Full, 0)]
    [TestCase(32L, DownloaderOptions.Full, 0)]
    [TestCase(32L, DownloaderOptions.Fast, 0)]
    [TestCase(1L, DownloaderOptions.Fast, 0)]
    [TestCase(2L, DownloaderOptions.Fast, 0)]
    [TestCase(3L, DownloaderOptions.Fast, 0)]
    [TestCase(32L, DownloaderOptions.Fast, 0)]
    [TestCase(SyncBatchSize.Max * 8, DownloaderOptions.Fast, 0)]
    [TestCase(SyncBatchSize.Max * 8, DownloaderOptions.Full, 0)]
    [TestCase(1L, DownloaderOptions.Full, 32)]
    [TestCase(32L, DownloaderOptions.Full, 32)]
    [TestCase(32L, DownloaderOptions.Fast, 32)]
    [TestCase(1L, DownloaderOptions.Fast, 32)]
    [TestCase(2L, DownloaderOptions.Fast, 32)]
    [TestCase(3L, DownloaderOptions.Fast, 32)]
    [TestCase(32L, DownloaderOptions.Fast, 32)]
    [TestCase(SyncBatchSize.Max * 8, DownloaderOptions.Fast, 32)]
    [TestCase(SyncBatchSize.Max * 8, DownloaderOptions.Full, 32)]
    public async Task Happy_path(long headNumber, int options, int fastSyncLag)
    {
        await using IContainer thisNode = BuildContainer();
        Context ctx = thisNode.Resolve<Context>();
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        bool withReceipts = downloaderOptions == DownloaderOptions.Fast;
        BlockDownloader downloader = ctx.BlockDownloader;

        Response responseOptions = Response.AllCorrect;
        if (withReceipts)
        {
            responseOptions |= Response.WithTransactions;
        }

        // normally chain length should be head number + 1 so here we setup a slightly shorter chain which
        // will only be fixed slightly later
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeer = new(chainLength, withReceipts, responseOptions);

        PeerInfo peerInfo = new(syncPeer);

        // Set head
        BlockHeader head = syncPeer.BlockTree.FindHeader(headNumber, BlockTreeLookupOptions.None)!;
        ctx.BlockTree.Insert(head).Should().Be(AddBlockResult.Added);

        syncPeer.ExtendTree(chainLength * 2);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions, fastSyncLag), CancellationToken.None);

        long expectedLastDownloadedBlock = peerInfo.HeadNumber - fastSyncLag;
        if (expectedLastDownloadedBlock < headNumber)
        {
            ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(headNumber);
            ctx.BlockTree.IsMainChain(ctx.BlockTree.BestSuggestedHeader!.Hash!).Should().Be(true);
        }
        else
        {
            ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(Math.Max(0, peerInfo.HeadNumber - fastSyncLag));
            ctx.BlockTree.IsMainChain(ctx.BlockTree.BestSuggestedHeader!.Hash!).Should().Be(downloaderOptions != DownloaderOptions.Full);
        }

        int receiptCount = 0;
        for (int i = (int)Math.Max(0, headNumber); i < peerInfo.HeadNumber - fastSyncLag; i++)
        {
            if (i % 3 == 0)
            {
                receiptCount += 2;
            }
        }

        ctx.ReceiptStorage.Count.Should().Be(withReceipts ? receiptCount : 0);
    }

    [Test]
    public async Task Ancestor_lookup_simple()
    {
        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(1024).TestObject)
            .Build();

        Context ctx = container.Resolve<Context>();

        BlockDownloader downloader = ctx.BlockDownloader;

        Response blockResponseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2048 + 1, false, blockResponseOptions);

        PeerInfo peerInfo = new(syncPeer);

        Block block1024 = Build.A.Block.WithParent(ctx.BlockTree.Head!).WithDifficulty(ctx.BlockTree.Head!.Difficulty + 1).TestObject;
        Block block1025 = Build.A.Block.WithParent(block1024).WithDifficulty(block1024.Difficulty + 1).TestObject;
        Block block1026 = Build.A.Block.WithParent(block1025).WithDifficulty(block1025.Difficulty + 1).TestObject;
        ctx.BlockTree.SuggestBlock(block1024);
        ctx.BlockTree.SuggestBlock(block1025);
        ctx.BlockTree.SuggestBlock(block1026);

        for (int i = 0; i < 1023; i++)
        {
            Assert.That(syncPeer.BlockTree.FindBlock(i, BlockTreeLookupOptions.None)!.Hash, Is.EqualTo(ctx.BlockTree.FindBlock(i, BlockTreeLookupOptions.None)!.Hash), i.ToString());
        }

        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), CancellationToken.None);
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(peerInfo.HeadNumber);
        ctx.BlockTree.IsMainChain(ctx.BlockTree.BestSuggestedHeader.GetOrCalculateHash()).Should().Be(true);
    }

    [Test]
    public async Task Ancestor_lookup_headers()
    {
        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(1024).TestObject)
            .Build();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        Response responseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2048 + 1, false, responseOptions);
        PeerInfo peerInfo = new(syncPeer);

        Block block1024 = Build.A.Block.WithParent(ctx.BlockTree.Head!).WithDifficulty(ctx.BlockTree.Head!.Difficulty + 1).TestObject;
        Block block1025 = Build.A.Block.WithParent(block1024).WithDifficulty(block1024.Difficulty + 1).TestObject;
        Block block1026 = Build.A.Block.WithParent(block1025).WithDifficulty(block1025.Difficulty + 1).TestObject;
        ctx.BlockTree.SuggestBlock(block1024);
        ctx.BlockTree.SuggestBlock(block1025);
        ctx.BlockTree.SuggestBlock(block1026);

        for (int i = 0; i < 1023; i++)
        {
            Assert.That(syncPeer.BlockTree.FindBlock(i, BlockTreeLookupOptions.None)!.Hash, Is.EqualTo(ctx.BlockTree.FindBlock(i, BlockTreeLookupOptions.None)!.Hash), i.ToString());
        }

        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None);
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(peerInfo.HeadNumber);
    }

    [Test]
    public async Task Ancestor_failure()
    {
        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(2048 + 1).TestObject)
            .Build();

        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        Response blockResponseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2072 + 1, true, blockResponseOptions);

        PeerInfo peerInfo = new(syncPeer);

        Assert.ThrowsAsync<EthSyncException>(() => downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None));
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(2048);
    }

    [Test]
    public async Task Ancestor_failure_blocks()
    {
        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(2048 + 1).TestObject)
            .Build();

        Context ctx = container.Resolve<Context>();

        BlockDownloader downloader = ctx.BlockDownloader;

        Response responseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2072 + 1, true, responseOptions);

        PeerInfo peerInfo = new(syncPeer);

        Assert.ThrowsAsync<EthSyncException>(() => downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None));
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(2048);
    }

    [TestCase(32, true)]
    [TestCase(1, true)]
    [TestCase(0, true)]
    [TestCase(32, false)]
    [TestCase(1, false)]
    [TestCase(0, false)]
    public async Task Can_sync_with_peer_when_it_times_out_on_full_batch(int ignoredBlocks, bool mergeDownloader)
    {
        ContainerBuilder builder = mergeDownloader ? BuildMergeContainerBuilder() : BuildContainerBuilder();
        SyncBatchSize syncBatchSize = new SyncBatchSize(LimboLogs.Instance);
        syncBatchSize.ExpandUntilMax();
        builder.AddSingleton<SyncBatchSize>(syncBatchSize);
        await using IContainer container = builder.Build();
        Context ctx = container.Resolve<Context>();

        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.TimeoutOnFullBatch));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.TimeoutOnFullBatch));

        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns((int)Math.Ceiling(SyncBatchSize.Max * SyncBatchSize.AdjustmentFactor) + ignoredBlocks);

        PeerInfo peerInfo = new(syncPeer);

        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, ignoredBlocks), CancellationToken.None).ContinueWith(_ => { });
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, ignoredBlocks), CancellationToken.None);
        Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(Math.Max(0, peerInfo.HeadNumber - ignoredBlocks)));

        syncPeer.HeadNumber.Returns((int)Math.Ceiling(SyncBatchSize.Max * SyncBatchSize.AdjustmentFactor) + ignoredBlocks);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None).ContinueWith(continuationAction: _ => { });
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None);
        Assert.That(ctx.BlockTree.BestSuggestedHeader.Number, Is.EqualTo(Math.Max(0, peerInfo.HeadNumber)));
    }

    [TestCase(32, 32, 0, true)]
    [TestCase(32, 16, 0, true)]
    [TestCase(500, 250, 0, true)]
    [TestCase(32, 32, 0, false)]
    [TestCase(32, 16, 0, false)]
    [TestCase(500, 250, 0, false)]
    [TestCase(32, 16, 100, true)]
    [TestCase(32, 16, 100, false)]
    public async Task Can_sync_partially_when_only_some_bodies_is_available(int blockCount, int availableBlock, int minResponseLength, bool mergeDownloader)
    {
        await using IContainer container = mergeDownloader ? BuildMergeContainer() : BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.WithTransactions & ~Response.AllKnown));

        List<Hash256> requestedHashes = new();
        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IList<Hash256> blockHashes = ci.ArgAt<IList<Hash256>>(0);
                int toTake = availableBlock - requestedHashes.Count;
                blockHashes = blockHashes.Take(toTake).ToList();
                requestedHashes.AddRange(blockHashes);

                if (blockHashes.Count == 0)
                {
                    return new OwnedBlockBodies(Array.Empty<BlockBody>());
                }

                BlockBody?[] response = ctx.ResponseBuilder
                    .BuildBlocksResponse(blockHashes, Response.AllCorrect | Response.WithTransactions & ~Response.AllKnown)
                    .Result
                    .Bodies!;

                if (response.Length < minResponseLength)
                {
                    BlockBody?[] nullPaddedResponse = new BlockBody[minResponseLength];
                    for (int i = 0; i < response.Length; i++)
                    {
                        nullPaddedResponse[i] = response[i];
                    }
                    response = nullPaddedResponse;
                }

                return new OwnedBlockBodies(response);
            });

        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(blockCount);

        PeerInfo peerInfo = new(syncPeer);

        ctx.BlockTree.BestSuggestedBody!.Number.Should().Be(0);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Full), CancellationToken.None).ContinueWith(_ => { });
        ctx.BlockTree.BestSuggestedBody.Number.Should().Be(availableBlock);
    }

    [Test]
    public async Task Headers_already_known()
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.AllKnown));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.AllKnown));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(64);

        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), CancellationToken.None)
            .ContinueWith(t => Assert.That(t.IsCompletedSuccessfully, Is.True));

        syncPeer.HeadNumber.Returns(128);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None)
            .ContinueWith(t => Assert.That(t.IsCompletedSuccessfully, Is.True));
    }

    [Test]
    public async Task Peer_only_advertise_one_header()
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ctx.ResponseBuilder.BuildHeaderResponse(0, 1, Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1);

        long blockSynced = await downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None);

        Assert.That(blockSynced, Is.EqualTo(0));
    }

    [TestCase(33L)]
    [TestCase(65L)]
    public async Task Peer_sends_just_one_item_when_advertising_more_blocks_but_no_bodies(long headNumber)
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.NoBody));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.JustFirst));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(headNumber);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);

        Task task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None);
        await task.ContinueWith(t => Assert.That(t.IsFaulted, Is.False));

        Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(headNumber));
    }

    [Test]
    public async Task Throws_on_null_best_peer()
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;
        Task task1 = downloader.DownloadBlocks(null, new BlocksRequest(DownloaderOptions.Fast, 0), CancellationToken.None);
        await task1.ContinueWith(t => Assert.That(t.IsFaulted, Is.True));

        Task task2 = downloader.DownloadBlocks(null, new BlocksRequest(), CancellationToken.None);
        await task2.ContinueWith(t => Assert.That(t.IsFaulted, Is.True));
    }

    [Test]
    public async Task Throws_on_inconsistent_batch()
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect ^ Response.Consistent));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1024);

        BlockDownloader downloader = ctx.BlockDownloader;
        Task task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), CancellationToken.None);
        await task.ContinueWith(t => Assert.That(t.IsFaulted, Is.True));
    }

    [Test]
    public async Task Throws_on_invalid_seal()
    {
        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(2048 + 1).TestObject)
            .AddSingleton<ISealValidator>(Always.Invalid)
            .Build();

        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000);

        Task task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), CancellationToken.None);
        await task.ContinueWith(t => Assert.That(t.IsFaulted, Is.True));
    }

    [Test]
    public async Task Throws_on_invalid_header()
    {
        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(2048 + 1).TestObject)
            .AddSingleton<IBlockValidator>(Always.Invalid)
            .Build();

        Context ctx = container.Resolve<Context>();

        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000);

        Task task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), CancellationToken.None);
        await task.ContinueWith(t => Assert.That(t.IsFaulted, Is.True));
    }

    private class SlowSealValidator : ISealValidator
    {
        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
        {
            Thread.Sleep(1000);
            return true;
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            Thread.Sleep(1000);
            return true;
        }
    }

    private class SlowHeaderValidator : IBlockValidator
    {

        public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle)
        {
            Thread.Sleep(1000);
            return true;
        }

        public bool Validate(BlockHeader header, bool isUncle)
        {
            Thread.Sleep(1000);
            return true;
        }

        public bool ValidateSuggestedBlock(Block block)
        {
            Thread.Sleep(1000);
            return true;
        }

        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
        {
            Thread.Sleep(1000);
            return true;
        }

        public bool ValidateWithdrawals(Block block, out string? error)
        {
            Thread.Sleep(1000);
            error = string.Empty;
            return true;
        }

        public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error)
        {
            Thread.Sleep(1000);
            error = null;
            return true;
        }

        public bool ValidateSuggestedBlock(Block block, [NotNullWhen(false)] out string? error, bool validateHashes = true)
        {
            Thread.Sleep(1000);
            error = null;
            return true;
        }

        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error)
        {
            Thread.Sleep(1000);
            error = null;
            return true;
        }

        public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, [NotNullWhen(false)] out string? error)
        {
            Thread.Sleep(1000);
            error = null;
            return true;
        }

        public bool Validate(BlockHeader header, bool isUncle, [NotNullWhen(false)] out string? error)
        {
            Thread.Sleep(1000);
            error = null;
            return true;
        }
    }

    [Test, MaxTime(7000)]
    [Ignore("Fails OneLoggerLogManager Travis only")]
    public async Task Can_cancel_seal_validation()
    {
        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(2048 + 1).TestObject)
            .AddSingleton<ISealValidator>(new SlowSealValidator())
            .Build();

        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1000);

        CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(1000);
        Task task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), cancellation.Token);
        await task.ContinueWith(t => Assert.That(t.IsCanceled, Is.True, $"blocks {t.Status}"));

        syncPeer.HeadNumber.Returns(2000);
        cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(1000);
        task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(), cancellation.Token);
        await task.ContinueWith(t => Assert.That(t.IsCanceled, Is.True, $"blocks {t.Status}"));
    }

    [Test, MaxTime(15000)]
    public async Task Can_cancel_adding_bodies()
    {
        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<ISealValidator>(new SlowSealValidator())
            .Build();

        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect));

        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1000);

        PeerInfo peerInfo = new(syncPeer);

        CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(990);
        Task task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), cancellation.Token);
        await task.ContinueWith(t => Assert.That(t.IsCanceled, Is.True, "blocks"));

        syncPeer.HeadNumber.Returns(2000);
        // peerInfo.HeadNumber *= 2;
        cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(990);
        task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), cancellation.Token);
        await task.ContinueWith(t => Assert.That(t.IsCanceled, Is.True, "blocks"));
    }

    [Test]
    public async Task Validate_always_the_last_seal_and_random_seal_in_the_package()
    {
        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);

        await using IContainer container = BuildContainerBuilder()
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(2048 + 1).TestObject)
            .AddSingleton<ISealValidator>(sealValidator)
            .Build();

        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        using IOwnedReadOnlyList<BlockHeader>? blockHeaders = await ctx.ResponseBuilder.BuildHeaderResponse(0, 512, Response.AllCorrect);
        BlockHeader[] blockHeadersCopy = blockHeaders?.ToArray() ?? Array.Empty<BlockHeader>();
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(blockHeaders);

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(511);

        Task task = downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), CancellationToken.None);
        await task;

        sealValidator.Received(2).ValidateSeal(Arg.Any<BlockHeader>(), true);
        sealValidator.Received(510).ValidateSeal(Arg.Any<BlockHeader>(), false);
        sealValidator.Received().ValidateSeal(blockHeadersCopy![^1], true);
    }

    private class ThrowingPeer : ISyncPeer
    {
        public ThrowingPeer(long number, UInt256? totalDiff, Hash256? headHash = null)
        {
            HeadNumber = number;
            TotalDifficulty = totalDiff ?? UInt256.MaxValue;
            HeadHash = headHash ?? Keccak.Zero;
        }

        public string Name => "Throwing";
        public string ClientId => "EX peer";
        public Node Node { get; } = null!;
        public string ProtocolCode { get; } = null!;
        public byte ProtocolVersion { get; } = default;
        public Hash256 HeadHash { get; set; }
        public long HeadNumber { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsPriority { get; set; }

        public void Disconnect(DisconnectReason reason, string details)
        {
            throw new NotImplementedException();
        }

        public Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            throw new Exception();
        }

        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
            throw new NotImplementedException();
        }

        public PublicKey Id => Node.Id;

        public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }
    }

    [Test]
    public async Task Faults_on_get_headers_faulting()
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = new ThrowingPeer(1000, UInt256.MaxValue);
        PeerInfo peerInfo = new(syncPeer);

        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, 0), CancellationToken.None)
            .ContinueWith(t => Assert.That(t.IsFaulted, Is.True));
    }

    [Test]
    public async Task Throws_on_block_task_exception()
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OwnedBlockBodies>(new TimeoutException()));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1);

        Func<Task> action = async () => await downloader.DownloadBlocks(peerInfo, new BlocksRequest(), CancellationToken.None);
        await action.Should().ThrowAsync<TimeoutException>();
    }

    [TestCase(DownloaderOptions.Fast, true)]
    [TestCase(DownloaderOptions.Full, false)]
    public async Task Throws_on_receipt_task_exception_when_downloading_receipts(int options, bool shouldThrow)
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);

        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.WithTransactions | Response.AllKnown));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions | Response.AllKnown));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IOwnedReadOnlyList<TxReceipt[]?>>(new TimeoutException()));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1);
        Func<Task> action = async () => await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);
        if (shouldThrow)
        {
            await action.Should().ThrowAsync<TimeoutException>();
        }
        else
        {
            await action.Should().NotThrowAsync();
        }
    }

    [TestCase(DownloaderOptions.Fast, true)]
    [TestCase(DownloaderOptions.Full, false)]
    public async Task Throws_on_null_receipt_downloaded(int options, bool shouldThrow)
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        bool withReceipts = downloaderOptions == DownloaderOptions.Fast;
        BlockDownloader downloader = ctx.BlockDownloader;

        Response responseOptions = Response.AllCorrect;
        if (withReceipts)
        {
            responseOptions |= Response.WithTransactions;
        }

        int headNumber = 5;

        // normally chain length should be head number + 1 so here we setup a slightly shorter chain which
        // will only be fixed slightly later
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeerInternal = new(chainLength, withReceipts, responseOptions);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockHeaders(ci.ArgAt<long>(0), ci.ArgAt<int>(1), ci.ArgAt<int>(2), ci.ArgAt<CancellationToken>(3)));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockBodies(ci.ArgAt<IReadOnlyList<Hash256>>(0), ci.ArgAt<CancellationToken>(1)));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                ArrayPoolList<TxReceipt[]?> receipts = (await syncPeerInternal
                    .GetReceipts(ci.ArgAt<IReadOnlyList<Hash256>>(0), ci.ArgAt<CancellationToken>(1)))
                    .ToPooledList();
                receipts[^1] = null;
                return (IOwnedReadOnlyList<TxReceipt[]?>)receipts;
            });

        syncPeer.TotalDifficulty.Returns(_ => syncPeerInternal.TotalDifficulty);
        syncPeer.HeadHash.Returns(_ => syncPeerInternal.HeadHash);
        syncPeer.HeadNumber.Returns(_ => syncPeerInternal.HeadNumber);

        PeerInfo peerInfo = new(syncPeer);
        syncPeerInternal.ExtendTree(chainLength * 2);

        Func<Task> action = async () => await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);

        if (shouldThrow)
        {
            await action.Should().ThrowAsync<EthSyncException>();
        }
        else
        {
            await action.Should().NotThrowAsync();
        }
    }

    [Test]
    public async Task Throws_on_block_bodies_count_higher_than_receipts_list_count()
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);

        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions).Result.Skip(1).ToPooledList(10));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1);
        Func<Task> action = async () => await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast), CancellationToken.None);
        await action.Should().ThrowAsync<EthSyncException>();
    }

    [TestCase(32)]
    [TestCase(1)]
    public async Task Does_throw_on_transaction_count_different_than_receipts_count_in_block(int threshold)
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();
        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions)
                .Result.Select(r => r is null || r.Length == 0 ? r : r.Skip(1).ToArray()).ToPooledList(10));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, threshold), CancellationToken.None);

        syncPeer.HeadNumber.Returns(2);

        Func<Task> action = async () => await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast), CancellationToken.None);
        await action.Should().ThrowAsync<EthSyncException>();
    }

    [TestCase(32)]
    [TestCase(1)]
    public async Task Throws_on_incorrect_receipts_root(int threshold)
    {
        await using IContainer container = BuildContainer();
        Context ctx = container.Resolve<Context>();

        BlockDownloader downloader = ctx.BlockDownloader;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);

        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.IncorrectReceiptRoot));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions).Result);

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast, threshold), CancellationToken.None);

        syncPeer.HeadNumber.Returns(2);

        Func<Task> action = async () => await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Fast), CancellationToken.None);
        await action.Should().ThrowAsync<EthSyncException>();
    }

    [Flags]
    private enum Response
    {
        Consistent = 1,
        AllCorrect = 7,
        JustFirst = 8,
        AllKnown = 16,
        TimeoutOnFullBatch = 32,
        NoBody = 64,
        WithTransactions = 128,
        IncorrectReceiptRoot = 256
    }

    private ContainerBuilder BuildContainerBuilder()
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        Dictionary<long, Hash256> testHeaderMapping = new();
        testHeaderMapping[0] = genesis.Hash!;

        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new SyncConfig()
                {
                    MaxProcessingThreads = 0,
                    SyncDispatcherEmptyRequestDelayMs = 1,
                    SyncDispatcherAllocateTimeoutMs = 1
                }
            ))
            .AddSingleton(testHeaderMapping)
            .AddSingleton<ResponseBuilder>()
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<IReceiptStorage, InMemoryReceiptStorage>()
            .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
            .AddSingleton<IPivot, Pivot>() // TODO: Check if can move to DI
            // Need actual blocktree
            // Lazily in case tests need to override
            .AddSingleton<IBlockTree>(ctx => Build.A.BlockTree()
                .WithoutSettingHead
                .WithSpecProvider(ctx.Resolve<ISpecProvider>())
                .WithBlockInfoDb(ctx.ResolveNamed<IDb>(DbNames.BlockInfos))
                .TestObject)
            .Add<Context>();


        builder.RegisterBuildCallback((ctx) =>
        {
            ctx.Resolve<IBlockTree>().SuggestBlock(genesis);
        });
        return builder;
    }

    private IContainer BuildContainer()
    {
        return BuildContainerBuilder().Build();
    }

    private class Context(ILifetimeScope container)
    {
        public IBlockTree BlockTree => container.Resolve<IBlockTree>();
        public ISyncPeerPool PeerPool => container.Resolve<ISyncPeerPool>();
        public ResponseBuilder ResponseBuilder => container.Resolve<ResponseBuilder>();
        public ActivatedSyncFeed<BlocksRequest?> Feed => (ActivatedSyncFeed<BlocksRequest?>)FullSyncFeedComponent.Feed;
        private SyncFeedComponent<BlocksRequest> FullSyncFeedComponent => container.ResolveKeyed<SyncFeedComponent<BlocksRequest>>(nameof(FullSyncFeed));
        public BlockDownloader BlockDownloader => FullSyncFeedComponent.BlockDownloader;
        public SyncDispatcher<BlocksRequest> Dispatcher => FullSyncFeedComponent.Dispatcher;
        public InMemoryReceiptStorage ReceiptStorage => container.Resolve<InMemoryReceiptStorage>();
    }

    private class SyncPeerMock : ISyncPeer
    {
        private readonly bool _withReceipts;
        private readonly bool _withWithdrawals;
        private readonly BlockHeadersMessageSerializer _headersSerializer = new();
        private readonly BlockBodiesMessageSerializer _bodiesSerializer = new();
        private readonly ReceiptsMessageSerializer _receiptsSerializer = new(MainnetSpecProvider.Instance);
        private readonly Response _flags;

        public BlockTree BlockTree { get; private set; } = null!;
        private IReceiptStorage _receiptStorage = new InMemoryReceiptStorage();

        public string Name => "Mock";

        public SyncPeerMock(long chainLength, bool withReceipts, Response flags, bool withWithdrawals = false)
        {
            _withReceipts = withReceipts;
            _withWithdrawals = withWithdrawals;
            _flags = flags;
            BuildTree(chainLength, withReceipts);
        }

        public SyncPeerMock(BlockTree blockTree, bool withReceipts, Response flags, UInt256 peerTotalDifficulty, bool withWithdrawals = false)
        {
            _withReceipts = withReceipts;
            _withWithdrawals = withWithdrawals;
            _flags = flags;
            BlockTree = blockTree;
            HeadNumber = BlockTree.Head!.Number;
            HeadHash = BlockTree.HeadHash!;
            TotalDifficulty = peerTotalDifficulty;
        }

        private void BuildTree(long chainLength, bool withReceipts)
        {
            _receiptStorage = new InMemoryReceiptStorage();
            BlockTreeBuilder builder = Build.A.BlockTree(MainnetSpecProvider.Instance);
            if (withReceipts)
            {
                builder = builder.WithTransactions(_receiptStorage);
            }

            builder = builder.OfChainLength((int)chainLength, 0, 0, _withWithdrawals);
            BlockTree = builder.TestObject;

            HeadNumber = BlockTree.Head!.Number;
            HeadHash = BlockTree.HeadHash!;
            TotalDifficulty = BlockTree.Head.TotalDifficulty ?? 0;
        }

        public void ExtendTree(long newLength)
        {
            BuildTree(newLength, _withReceipts);
        }

        public Node Node { get; } = null!;
        public string ClientId { get; } = null!;
        public byte ProtocolVersion { get; } = default;
        public string ProtocolCode { get; } = null!;
        public Hash256 HeadHash { get; set; } = null!;
        public PublicKey Id => Node.Id;
        public long HeadNumber { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsPriority { get; set; }

        public async Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        {
            BlockBody[] headers = new BlockBody[blockHashes.Count];
            int i = 0;
            foreach (Hash256 blockHash in blockHashes)
            {
                headers[i++] = BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.None)!.Body;
            }

            using BlockBodiesMessage message = new(headers);
            byte[] messageSerialized = _bodiesSerializer.Serialize(message);
            return await Task.FromResult(_bodiesSerializer.Deserialize(messageSerialized).Bodies!);
        }

        public async Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            bool justFirst = _flags.HasFlag(Response.JustFirst);
            bool timeoutOnFullBatch = _flags.HasFlag(Response.TimeoutOnFullBatch);

            if (timeoutOnFullBatch && number == SyncBatchSize.Max)
            {
                throw new TimeoutException();
            }

            BlockHeader[] headers = new BlockHeader[maxBlocks];
            for (int i = 0; i < (justFirst ? 1 : maxBlocks); i++)
            {
                headers[i] = BlockTree.FindHeader(number + i, BlockTreeLookupOptions.None)!;
            }

            using BlockHeadersMessage message = new(headers.ToPooledList());
            byte[] messageSerialized = _headersSerializer.Serialize(message);
            return await Task.FromResult(_headersSerializer.Deserialize(messageSerialized).BlockHeaders);
        }

        public async Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token)
        {
            TxReceipt[][] receipts = new TxReceipt[blockHash.Count][];
            int i = 0;
            foreach (Hash256 keccak in blockHash)
            {
                Block? block = BlockTree.FindBlock(keccak, BlockTreeLookupOptions.None);
                TxReceipt[] blockReceipts = _receiptStorage.Get(block!);
                receipts[i++] = blockReceipts;
            }

            using ReceiptsMessage message = new(receipts.ToPooledList());
            byte[] messageSerialized = _receiptsSerializer.Serialize(message);
            return await Task.FromResult(_receiptsSerializer.Deserialize(messageSerialized).TxReceipts);
        }

        public void Disconnect(DisconnectReason reason, string details)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
            throw new NotImplementedException();
        }

        public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }
    }

    private class ResponseBuilder
    {
        private readonly IBlockTree _blockTree;
        private readonly Dictionary<long, Hash256> _testHeaderMapping;

        public ResponseBuilder(IBlockTree blockTree, Dictionary<long, Hash256> testHeaderMapping)
        {
            _blockTree = blockTree;
            _testHeaderMapping = testHeaderMapping;
        }

        public async Task<IOwnedReadOnlyList<BlockHeader>?> BuildHeaderResponse(long startNumber, int number, Response flags)
        {
            bool consistent = flags.HasFlag(Response.Consistent);
            bool justFirst = flags.HasFlag(Response.JustFirst);
            bool allKnown = flags.HasFlag(Response.AllKnown);
            bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);
            bool withTransaction = flags.HasFlag(Response.WithTransactions);

            if (timeoutOnFullBatch && number == SyncBatchSize.Max)
            {
                throw new TimeoutException();
            }

            BlockHeader? startBlock = _blockTree.FindHeader(_testHeaderMapping[startNumber], BlockTreeLookupOptions.None)!;
            startBlock ??= _headers[_testHeaderMapping[startNumber]];
            BlockHeader[] headers = new BlockHeader[number];
            headers[0] = startBlock;
            if (!justFirst)
            {
                for (int i = 1; i < number; i++)
                {
                    Hash256 receiptRoot = i == 1 ? Keccak.EmptyTreeHash : new Hash256("0x9904791428367d3f36f2be68daf170039dd0b3d6b23da00697de816a05fb5cc1");
                    BlockHeaderBuilder blockHeaderBuilder = consistent
                        ? Build.A.BlockHeader.WithReceiptsRoot(receiptRoot).WithParent(headers[i - 1])
                        : Build.A.BlockHeader.WithReceiptsRoot(receiptRoot).WithNumber(headers[i - 1].Number + 1);

                    if (withTransaction)
                    {
                        // We don't know the TX root yet, it should be populated by `BuildBlocksResponse` and `BuildReceiptsResponse`.
                        blockHeaderBuilder.WithTransactionsRoot(Keccak.Compute("something"));
                        blockHeaderBuilder.WithReceiptsRoot(Keccak.Compute("something"));
                    }

                    headers[i] = blockHeaderBuilder.TestObject;

                    if (allKnown)
                    {
                        _blockTree.SuggestHeader(headers[i]);
                    }

                    _testHeaderMapping[startNumber + i] = headers[i].Hash!;
                }
            }

            foreach (BlockHeader header in headers)
            {
                _headers[header.Hash!] = header;
            }

            using BlockHeadersMessage message = new(headers.ToPooledList());
            byte[] messageSerialized = _headersSerializer.Serialize(message);
            return await Task.FromResult(_headersSerializer.Deserialize(messageSerialized).BlockHeaders);
        }

        private readonly BlockHeadersMessageSerializer _headersSerializer = new();
        private readonly BlockBodiesMessageSerializer _bodiesSerializer = new();
        private readonly ReceiptsMessageSerializer _receiptsSerializer = new(MainnetSpecProvider.Instance);
        private readonly Dictionary<Hash256, BlockHeader> _headers = new();
        private readonly Dictionary<Hash256, BlockBody> _bodies = new();

        public async Task<OwnedBlockBodies> BuildBlocksResponse(IList<Hash256> blockHashes, Response flags)
        {
            bool consistent = flags.HasFlag(Response.Consistent);
            bool justFirst = flags.HasFlag(Response.JustFirst);
            bool allKnown = flags.HasFlag(Response.AllKnown);
            bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);
            bool withTransactions = flags.HasFlag(Response.WithTransactions);

            if (timeoutOnFullBatch && blockHashes.Count == SyncBatchSize.Max)
            {
                throw new TimeoutException();
            }

            BlockHeader? startHeader = _blockTree.FindHeader(blockHashes[0], BlockTreeLookupOptions.None);
            startHeader ??= _headers[blockHashes[0]];

            BlockHeader[] blockHeaders = new BlockHeader[blockHashes.Count];
            BlockBody[] blockBodies = new BlockBody[blockHashes.Count];

            Block BuildBlockForHeader(BlockHeader header, int txSeed)
            {
                BlockBuilder blockBuilder = Build.A.Block.WithHeader(header);

                if (withTransactions && header.TxRoot != Keccak.EmptyTreeHash)
                {
                    blockBuilder.WithTransactions(Build.A.Transaction.WithValue(txSeed * 2).SignedAndResolved().TestObject,
                        Build.A.Transaction.WithValue(txSeed * 2 + 1).SignedAndResolved().TestObject);
                }

                return blockBuilder.TestObject;
            }

            Block newBlock = BuildBlockForHeader(startHeader, 0);
            blockBodies[0] = newBlock.Body;
            blockHeaders[0] = startHeader;

            _bodies[startHeader.Hash!] = blockBodies[0];
            _headers[startHeader.Hash!] = blockHeaders[0];
            if (!justFirst)
            {
                for (int i = 0; i < blockHashes.Count; i++)
                {
                    blockHeaders[i] = consistent
                        ? _headers[blockHashes[i]]
                        : Build.A.BlockHeader.WithNumber(blockHeaders[i - 1].Number + 1).WithHash(blockHashes[i]).TestObject;

                    _testHeaderMapping[startHeader.Number + i] = blockHeaders[i].Hash!;

                    BlockHeader header = consistent
                        ? blockHeaders[i]
                        : blockHeaders[i - 1];

                    Block block = BuildBlockForHeader(header, i);
                    blockBodies[i] = block.Body;
                    _bodies[blockHashes[i]] = blockBodies[i];

                    if (allKnown)
                    {
                        _blockTree.SuggestBlock(block);
                    }
                }
            }

            using BlockBodiesMessage message = new(blockBodies);
            byte[] messageSerialized = _bodiesSerializer.Serialize(message);
            return await Task.FromResult(_bodiesSerializer.Deserialize(messageSerialized).Bodies!);
        }

        public async Task<IOwnedReadOnlyList<TxReceipt[]?>> BuildReceiptsResponse(IList<Hash256> blockHashes, Response flags = Response.AllCorrect)
        {
            TxReceipt[][] receipts = new TxReceipt[blockHashes.Count][];
            for (int i = 0; i < receipts.Length; i++)
            {
                BlockBody body = _bodies[blockHashes[i]];
                receipts[i] = body.Transactions
                    .Select(t => Build.A.Receipt
                        .WithStatusCode(StatusCode.Success)
                        .WithGasUsed(10)
                        .WithBloom(Bloom.Empty)
                        .WithLogs(Build.A.LogEntry.WithAddress(t.SenderAddress!).WithTopics(TestItem.KeccakA).TestObject)
                        .TestObject)
                    .ToArray();

                _headers[blockHashes[i]].ReceiptsRoot = flags.HasFlag(Response.IncorrectReceiptRoot)
                    ? Keccak.EmptyTreeHash
                    : ReceiptTrie<TxReceipt>.CalculateRoot(MainnetSpecProvider.Instance.GetSpec((ForkActivation)_headers[blockHashes[i]].Number), receipts[i], Rlp.GetStreamDecoder<TxReceipt>()!);
            }

            using ReceiptsMessage message = new(receipts.ToPooledList());
            byte[] messageSerialized = _receiptsSerializer.Serialize(message);
            return await Task.FromResult(_receiptsSerializer.Deserialize(messageSerialized).TxReceipts);
        }
    }
}
