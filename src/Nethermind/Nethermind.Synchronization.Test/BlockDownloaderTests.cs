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
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Autofac.Features.AttributeFilters;
using Humanizer;
using Nethermind.Config;
using Nethermind.Core.Events;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Utils;
using Nethermind.Merge.Plugin;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NonBlocking;
using IContainer = Autofac.IContainer;

namespace Nethermind.Synchronization.Test;

[Parallelizable(ParallelScope.All)]
public partial class BlockDownloaderTests
{
    private const int FullBatch = 24;
    private const int SyncBatchSizeMax = 128;

    [TestCase(1L, false, 0)]
    [TestCase(32L, false, 0)]
    [TestCase(32L, true, 0)]
    [TestCase(1L, true, 0)]
    [TestCase(2L, true, 0)]
    [TestCase(3L, true, 0)]
    [TestCase(32L, true, 0)]
    [TestCase(SyncBatchSizeMax * 8, true, 0)]
    [TestCase(SyncBatchSizeMax * 8, false, 0)]
    [TestCase(1L, false, 32)]
    [TestCase(32L, false, 32)]
    [TestCase(32L, true, 32)]
    [TestCase(1L, true, 32)]
    [TestCase(2L, true, 32)]
    [TestCase(3L, true, 32)]
    [TestCase(32L, true, 32)]
    [TestCase(SyncBatchSizeMax * 8, true, 32)]
    [TestCase(SyncBatchSizeMax * 8, false, 32)]
    public async Task Happy_path(long headNumber, bool enableFastSync, int fastSynclag)
    {
        await using IContainer node = CreateNode(configProvider: new ConfigProvider(new SyncConfig()
        {
            FastSync = enableFastSync,
            StateMinDistanceFromHead = fastSynclag
        }));
        Context ctx = node.Resolve<Context>();
        bool withReceipts = enableFastSync;
        Response responseOptions = Response.AllCorrect;
        if (enableFastSync)
        {
            responseOptions |= Response.WithTransactions;
        }

        // normally chain length should be head number + 1 so here we setup a slightly shorter chain which
        // will only be fixed slightly later
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeer = new(chainLength, withReceipts, responseOptions);

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        if (enableFastSync)
        {
            await ctx.FastSyncUntilNoRequest(peerInfo);
            ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(Math.Max(0, Math.Min(headNumber, headNumber - fastSynclag)));
        }

        syncPeer.ExtendTree(chainLength * 2);
        await ctx.FullSyncUntilNoRequest(peerInfo);

        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(Math.Max(0, peerInfo.HeadNumber));
        // full sync does not set main chain, but triggers it processing which eventually set main chain
        ctx.BlockTree.IsMainChain(ctx.BlockTree.BestSuggestedHeader!.Hash!).Should().Be(false);

        if (enableFastSync)
        {
            int receiptCount = 0;
            for (int i = 0; i < (int)Math.Max(0, headNumber - fastSynclag); i++)
            {
                if (i % 3 == 0)
                {
                    receiptCount += 2;
                }
            }

            ctx.ReceiptStorage.Count.Should().Be(withReceipts ? receiptCount : 0);
        }
    }

    [Test]
    public async Task Invoke_UpdateMainChain_Once()
    {
        long headNumber = 100;
        int fastSyncLag = 10;
        bool withReceipts = true;
        long chainLength = headNumber + 1;

        await using IContainer node = CreateNode(configProvider: new ConfigProvider(new SyncConfig()
        {
            FastSync = true,
            StateMinDistanceFromHead = fastSyncLag,
        }));
        Context ctx = node.Resolve<Context>();

        SyncPeerMock syncPeer = new(chainLength, withReceipts, Response.AllCorrect | Response.WithTransactions);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        List<long> newHeadSequence = new List<long>();
        ctx.BlockTree.BlockAddedToMain += (_, b) => newHeadSequence.Add(b.Block.Number);

        await ctx.FastSyncUntilNoRequest(peerInfo);
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(Math.Max(0, Math.Min(headNumber, headNumber - fastSyncLag)));

        List<long> expectedNewHeadSequence = Enumerable.Range(1, (int)(chainLength - fastSyncLag - 1)).Select((i) => (long)i).ToList();
        newHeadSequence.Should().BeEquivalentTo(expectedNewHeadSequence);
    }

    [Test]
    public async Task ForwardHeaderProvider_ReturnedSameHeaders_EvenAfterSuggestion()
    {
        long headNumber = 200;
        int fastSyncLag = 10;
        bool withReceipts = true;
        long chainLength = headNumber + 1;

        IForwardHeaderProvider mockForwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();

        await using IContainer node = CreateNode(configProvider: new ConfigProvider(new SyncConfig()
        {
            FastSync = true,
            StateMinDistanceFromHead = fastSyncLag,
        }),
            configurer: (builder) => builder.AddSingleton<IForwardHeaderProvider>(mockForwardHeaderProvider));

        Context ctx = node.Resolve<Context>();
        SyncPeerMock syncPeer = new(chainLength, withReceipts, Response.AllCorrect | Response.WithTransactions);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        mockForwardHeaderProvider.GetBlockHeaders(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())!
            .Returns((c) => syncPeer.GetBlockHeaders(0, 200, 0, default));

        Func<Task> act = async () => await ctx.FastSyncUntilNoRequest(peerInfo);
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Ancestor_lookup_simple()
    {
        IBlockTree instance = CachedBlockTreeBuilder.OfLength(1024);
        await using IContainer node = CreateNode(builder =>
        {
            builder.AddSingleton<IBlockTree>(instance);
        });
        Context ctx = node.Resolve<Context>();

        Response blockResponseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2048 + 1, false, blockResponseOptions);

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

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

        await ctx.FullSyncUntilNoRequest(peerInfo);
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(peerInfo.HeadNumber);
        ctx.WasSuggested(ctx.BlockTree.BestSuggestedHeader.GetOrCalculateHash());
    }

    [Test]
    public async Task Ancestor_failure_blocks()
    {
        using IContainer node = CreateNode(builder =>
        {
            builder.AddSingleton<IBlockTree>(CachedBlockTreeBuilder.OfLength(2048 + 1));
        });
        Context ctx = node.Resolve<Context>();

        Response responseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2072 + 1, true, responseOptions);

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FullSyncUntilNoRequest(peerInfo);
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(2048);
    }

    [TestCase(32, true)]
    [TestCase(1, true)]
    [TestCase(0, true)]
    [TestCase(32, false)]
    [TestCase(1, false)]
    [TestCase(0, false)]
    public async Task Can_sync_with_peer_when_it_times_out(int ignoredBlocks, bool mergeDownloader)
    {
        Action<ContainerBuilder> configurer = builder =>
        {
            builder.AddSingleton<ISyncPeerPool>(Substitute.For<ISyncPeerPool>());
        };

        await using IContainer node = mergeDownloader ? CreateMergeNode(configurer) : CreateNode(configurer);
        Context ctx = node.Resolve<Context>();
        ISyncPeerPool peerPool = node.Resolve<ISyncPeerPool>();

        int requestCount = 0;
        peerPool.EstimateRequestLimit(
                Arg.Any<RequestType>(),
                Arg.Any<IPeerAllocationStrategy>(),
                Arg.Any<AllocationContexts>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<int?>>((_) =>
            {
                if (requestCount < 3)
                {
                    requestCount++;
                    return Task.FromResult<int?>(null);
                }

                // Randomly not trigger timeout
                return Task.FromResult<int?>(FullBatch - 1);
            });

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.TimeoutOnFullBatch));

        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(FullBatch + ignoredBlocks + 20);

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        await ctx.FullDispatcherSync(Math.Max(0, peerInfo.HeadNumber));
        Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(Math.Max(0, peerInfo.HeadNumber)));
    }

    [TestCase(32, 32, 0, true)]
    [TestCase(32, 16, 0, true)]
    [TestCase(32, 32, 0, false)]
    [TestCase(32, 16, 0, false)]
    [TestCase(32, 16, 100, true)]
    [TestCase(32, 16, 100, false)]
    [TestCase(500, 250, 0, true)]
    [TestCase(500, 250, 0, false)]
    public async Task Can_sync_partially_when_only_some_bodies_is_available(int blockCount, int availableBlock, int minResponseLength, bool mergeDownloader)
    {
        await using IContainer node = mergeDownloader ? CreateMergeNode() : CreateNode();
        Context ctx = node.Resolve<Context>();

        Response responseOptions = Response.AllCorrect | Response.WithTransactions & ~Response.AllKnown | Response.Consistent;
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), responseOptions));

        List<Hash256> requestedHashes = new();
        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IList<Hash256> blockHashes = ci.ArgAt<IList<Hash256>>(0);
                lock (requestedHashes)
                {
                    blockHashes = blockHashes.Where((hash) =>
                    {
                        BlockHeader? header = ctx.ResponseBuilder.GetHeader(hash);
                        return header is not null && header.Number <= availableBlock;
                    }).ToList();
                    requestedHashes.AddRange(blockHashes);
                }

                if (blockHashes.Count == 0)
                {
                    return new OwnedBlockBodies([]);
                }

                BlockBody?[] response = ctx.ResponseBuilder
                    .BuildBlocksResponse(blockHashes, responseOptions)
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
        ctx.ConfigureBestPeer(peerInfo);

        ctx.BlockTree.BestSuggestedBody!.Number.Should().Be(0);
        await ctx.FullDispatcherSync(availableBlock, 10000);
        ctx.BlockTree.BestSuggestedBody.Number.Should().Be(availableBlock);
    }

    [Test]
    public async Task Peer_only_advertise_one_header()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ctx.ResponseBuilder.BuildHeaderResponse(0, 1, Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1);
        ctx.ConfigureBestPeer(peerInfo);

        await ctx.FullSyncUntilNoRequest(peerInfo);
        ctx.BlockTree.BestSuggestedBody!.Number.Should().Be(0);
    }

    [TestCase(33L)]
    [TestCase(65L)]
    [Retry(3)]
    public async Task Peer_sends_just_one_item_when_advertising_more_blocks_but_no_bodies(long headNumber)
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.NoBody));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.JustFirst));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(headNumber);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        ctx.ConfigureBestPeer(peerInfo);

        await ctx.FullSyncUntilNoRequest(peerInfo);

        Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(headNumber));
    }

    [Test]
    public async Task Throws_on_inconsistent_batch()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect ^ Response.Consistent));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1024);
        ctx.ConfigureBestPeer(peerInfo);

        await ctx.FullSyncUntilNoRequest(peerInfo);
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    [Test]
    public async Task Throws_on_invalid_seal()
    {
        await using IContainer node = CreateNode(builder => builder.AddSingleton<ISealValidator>(Always.Invalid));
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000);
        ctx.ConfigureBestPeer(peerInfo);

        await ctx.Feed.PrepareRequest(default);
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    [Test]
    public async Task Throws_on_invalid_header()
    {
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IBlockValidator>(Always.Invalid));
        Context ctx = node.Resolve<Context>();

        Response options = Response.AllCorrect | Response.Consistent;
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), options));
        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), options | Response.JustFirst));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000);
        ctx.ConfigureBestPeer(peerInfo);

        BlocksRequest blockRequest = await ctx.Feed.PrepareRequest(default);
        await ctx.FullSyncFeedComponent.Downloader.Dispatch(peerInfo, blockRequest, default);
        ctx.Feed.HandleResponse(blockRequest, peerInfo);
        _ = await ctx.Feed.PrepareRequest(default); // The block is validated here.

        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    [Test]
    public async Task Prune_download_requests_map()
    {
        await using IContainer node = CreateNode(builder => builder
            .AddDecorator<ISyncConfig>((_, syncConfig) =>
            {
                syncConfig.MaxTxInForwardSyncBuffer = 3200;
                return syncConfig;
            })
            .AddSingleton<IBlockValidator>(Always.Invalid));
        Context ctx = node.Resolve<Context>();

        SyncPeerMock syncPeer = new(40, true, Response.AllCorrect);
        SyncPeerMock syncPeer2 = new(40, true, Response.AllCorrect, withWithdrawals: true);
        SyncPeerMock syncPeer3 = new(40, false, Response.AllCorrect, withWithdrawals: true);

        IForwardSyncController forwardSyncController = ctx.ForwardSyncController;

        ctx.ConfigureBestPeer(syncPeer);
        (await forwardSyncController.PrepareRequest(DownloaderOptions.Insert, 0, default)).Should().NotBeNull();
        forwardSyncController.DownloadRequestBufferSize.Should().Be(32);

        ctx.ConfigureBestPeer(syncPeer2);
        (await forwardSyncController.PrepareRequest(DownloaderOptions.Insert, 0, default)).Should().NotBeNull();
        forwardSyncController.DownloadRequestBufferSize.Should().Be(64);

        ctx.ConfigureBestPeer(syncPeer3);
        (await forwardSyncController.PrepareRequest(DownloaderOptions.Insert, 0, default)).Should().NotBeNull();
        forwardSyncController.DownloadRequestBufferSize.Should().Be(96);

        (await forwardSyncController.PrepareRequest(DownloaderOptions.Insert, 0, default)).Should().BeNull();
        forwardSyncController.DownloadRequestBufferSize.Should().Be(32);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Can_DownloadBlockOutOfOrder(bool isMerge)
    {
        int chainLength = 1024;
        int syncPivotNumber = 128;
        int beaconPivotNumber = 256;
        int fastSyncLag = 1;

        Response responseOptions = Response.AllCorrect | Response.WithTransactions;
        SyncPeerMock syncPeer = new(chainLength, true, responseOptions);
        syncPeer.HeadNumber = beaconPivotNumber; // For POW

        BlockHeader syncPivot = syncPeer.BlockTree.FindHeader(syncPivotNumber)!;
        ISyncConfig syncConfig = new SyncConfig()
        {
            FastSync = true,
            StateMinDistanceFromHead = fastSyncLag,
            PivotNumber = syncPivot.Number.ToString(),
            PivotHash = syncPivot.Hash!.ToString(),
        };

        await using IContainer container = isMerge
            ? CreateMergeNode((_) => { }, syncConfig, new MergeConfig() { TerminalTotalDifficulty = "0" })
            : CreateNode(configProvider: new ConfigProvider(syncConfig));

        Context ctx = container.Resolve<Context>();

        // Simulate fast header
        BlockHeader syncPivotHeader = syncPeer.BlockTree.FindHeader(syncPivotNumber)!;
        ctx.BlockTree.Insert(syncPivotHeader);

        if (isMerge)
        {
            var mergeContext = container.Resolve<PostMergeContext>();
            mergeContext.BeaconPivot.EnsurePivot(syncPeer.BlockTree.FindHeader(beaconPivotNumber, BlockTreeLookupOptions.None));
            mergeContext.InsertBeaconHeaderFrom(syncPeer, beaconPivotNumber, syncPivotNumber);
            mergeContext.BeaconPivot.ProcessDestination = syncPeer.BlockTree.FindHeader(beaconPivotNumber, BlockTreeLookupOptions.None);
        }

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        var req1 = await ctx.FastSyncFeedComponent.Feed.PrepareRequest();
        req1.Should().NotBeNull();
        await ctx.FastSyncFeedComponent.Downloader.Dispatch(peerInfo, req1, default);

        while (true)
        {
            var req = await ctx.FastSyncFeedComponent.Feed.PrepareRequest();
            if (req is null) break;
            await ctx.FastSyncFeedComponent.Downloader.Dispatch(peerInfo, req, default);
            ctx.FastSyncFeedComponent.Feed.HandleResponse(req);
        }

        ctx.FastSyncFeedComponent.Feed.HandleResponse(req1);

        // Receipt for the first req
        var finalReq = await ctx.FastSyncFeedComponent.Feed.PrepareRequest();
        await ctx.FastSyncFeedComponent.Downloader.Dispatch(peerInfo, finalReq, default);
        ctx.FastSyncFeedComponent.Feed.HandleResponse(finalReq);

        _ = await ctx.FastSyncFeedComponent.Feed.PrepareRequest();

        ctx.ShouldFastSyncedUntil(beaconPivotNumber - fastSyncLag);
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

        public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? errorMessage)
        {
            Thread.Sleep(1000);
            errorMessage = null;
            return true;
        }
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
    public async Task Flag_lesser_quality_on_body_download_failure()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

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
        ctx.ConfigureBestPeer(peerInfo);
        (await ctx.HandleOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.LesserQuality);
    }

    [Test]
    public async Task Throws_on_receipt_task_exception_when_downloading_receipts()
    {
        await using IContainer node = CreateFastSyncNode(fastSyncLag: 1);
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);

        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.WithTransactions | Response.AllKnown));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IOwnedReadOnlyList<TxReceipt[]?>>(new TimeoutException()));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(10);
        ctx.ConfigureBestPeer(peerInfo);
        (await ctx.HandleFastSyncOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.OK);
        (await ctx.HandleFastSyncOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.LesserQuality);
    }

    [Test]
    public async Task On_null_receipt_will_retry()
    {
        await using IContainer node = CreateFastSyncNode(fastSyncLag: 1);
        Context ctx = node.Resolve<Context>();

        Response responseOptions = Response.AllCorrect | Response.WithTransactions;

        int headNumber = 5;

        // normally chain length should be head number + 1 so here we setup a slightly shorter chain which
        // will only be fixed slightly later
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeerInternal = new(chainLength, true, responseOptions);
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
        ctx.ConfigureBestPeer(peerInfo);
        (await ctx.HandleFastSyncOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.OK);
        (await ctx.HandleFastSyncOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.OK);

        (await ctx.FastSyncFeedComponent.Feed.PrepareRequest(default)).ReceiptsRequests.Count.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Does_mark_low_quality_on_transaction_count_different_than_receipts_count_in_block()
    {
        await using IContainer node = CreateFastSyncNode(fastSyncLag: 1);
        Context ctx = node.Resolve<Context>();

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
        syncPeer.HeadNumber.Returns(2);
        ctx.ConfigureBestPeer(peerInfo);
        (await ctx.HandleFastSyncOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.OK);
        (await ctx.HandleFastSyncOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.LesserQuality);
    }

    [Test]
    public async Task Mark_low_quality_on_incorrect_receipts_root()
    {
        await using IContainer node = CreateFastSyncNode(fastSyncLag: 1);
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);

        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildReceiptsResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect | Response.WithTransactions | Response.IncorrectReceiptRoot).Result);

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(2);
        ctx.ConfigureBestPeer(peerInfo);
        (await ctx.HandleFastSyncOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.OK);
        (await ctx.HandleFastSyncOneRequest(peerInfo)).Should().Be(SyncResponseHandlingResult.LesserQuality);
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

    private IContainer CreateFastSyncNode(int fastSyncLag = 1)
    {
        return CreateNode(configProvider: new ConfigProvider(new SyncConfig()
        {
            FastSync = true,
            StateMinDistanceFromHead = fastSyncLag,
        }));
    }

    private IContainer CreateNode(Action<ContainerBuilder>? configurer = null, IConfigProvider? configProvider = null)
    {
        configProvider ??= new ConfigProvider();

        Block genesis = Build.A.Block.Genesis.TestObject;
        ContainerBuilder b = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton<IReceiptStorage, InMemoryReceiptStorage>()
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<ISpecProvider>(new MainnetSpecProvider())
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<ISyncPeerPool>(Substitute.For<ISyncPeerPool>())
            .AddSingleton<ResponseBuilder>()
            .AddDecorator<IBlockTree>((ctx, tree) =>
            {
                if (tree.Genesis is null) tree.SuggestBlock(genesis);
                return tree;
            })

            .AddSingleton<Dictionary<long, Hash256>, IBlockTree>((blockTree) => new Dictionary<long, Hash256>()
            {
                {
                    0, blockTree.Genesis!.Hash!
                },
            })
            .AddSingleton<Context>();

        configurer?.Invoke(b);
        return b
            .Build();
    }

    private record Context
    {
        private readonly ConcurrentDictionary<Hash256, bool> _wasSuggested = new();
        public ActivatedSyncFeed<BlocksRequest> Feed => (ActivatedSyncFeed<BlocksRequest>)FullSyncFeedComponent.Feed;
        public ResponseBuilder ResponseBuilder { get; init; }
        public SyncFeedComponent<BlocksRequest> FastSyncFeedComponent { get; init; }
        public SyncFeedComponent<BlocksRequest> FullSyncFeedComponent { get; init; }
        public IForwardSyncController ForwardSyncController { get; init; }
        public IBlockTree BlockTree { get; init; }
        public InMemoryReceiptStorage ReceiptStorage { get; init; }
        public ISyncPeerPool PeerPool { get; init; }

        public Context(
            ResponseBuilder ResponseBuilder,
            [KeyFilter(nameof(FastSyncFeed))] SyncFeedComponent<BlocksRequest> FastSyncFeedComponent,
            [KeyFilter(nameof(FullSyncFeed))] SyncFeedComponent<BlocksRequest> FullSyncFeedComponent,
            IForwardSyncController ForwardSyncController,
            IBlockTree BlockTree,
            InMemoryReceiptStorage ReceiptStorage,
            ISyncPeerPool PeerPool
        )
        {
            this.ResponseBuilder = ResponseBuilder;
            this.FastSyncFeedComponent = FastSyncFeedComponent;
            this.FullSyncFeedComponent = FullSyncFeedComponent;
            this.ForwardSyncController = ForwardSyncController;
            this.BlockTree = BlockTree;
            this.ReceiptStorage = ReceiptStorage;
            this.PeerPool = PeerPool;
            BlockTree.NewBestSuggestedBlock += (sender, args) => _wasSuggested[args.Block.Hash!] = true;
        }

        public void ConfigureBestPeer(ISyncPeer syncPeer)
        {
            ConfigureBestPeer(new PeerInfo(syncPeer));
        }

        public void ConfigureBestPeer(PeerInfo peerInfo)
        {
            AutoResetEvent autoResetEvent = new(true);
            SyncPeerAllocation peerAllocation = new(peerInfo, AllocationContexts.Blocks, null);

            PeerPool
                .Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns((c) =>
                {
                    if (!autoResetEvent.WaitOne(100)) return Task.FromResult<SyncPeerAllocation?>(null)!;
                    return Task.FromResult(peerAllocation);
                });

            PeerPool
                .When((p) => p.Free(peerAllocation))
                .Do((c) => autoResetEvent.Set());
        }

        public async Task SyncUntilNoRequest(SyncFeedComponent<BlocksRequest> component, PeerInfo peerInfo)
        {
            using AutoCancelTokenSource cts = AutoCancelTokenSource.ThatCancelAfter(10.Seconds());

            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();

                BlocksRequest? blockRequest = await component.Feed.PrepareRequest(cts.Token);
                if (blockRequest is null) break;
                await component.Downloader.Dispatch(peerInfo, blockRequest, cts.Token);
                component.Feed.HandleResponse(blockRequest, peerInfo);
            }
        }

        public Task FastSyncUntilNoRequest(PeerInfo peerInfo)
        {
            return SyncUntilNoRequest(FastSyncFeedComponent, peerInfo);
        }

        public Task FullSyncUntilNoRequest(PeerInfo peerInfo)
        {
            return SyncUntilNoRequest(FullSyncFeedComponent, peerInfo);
        }

        public void WasSuggested(Hash256 blockHash)
        {
            _wasSuggested.TryGetValue(blockHash, out _).Should().BeTrue();
        }

        public async Task FullDispatcherSync(long untilBestSuggestedHeaderIs, long timeoutMs = 10000)
        {
            using AutoCancelTokenSource cts = AutoCancelTokenSource.ThatCancelAfter(timeoutMs.Milliseconds());

            Task waitTask = Wait.ForEventCondition<BlockEventArgs>(cts.Token,
                e => BlockTree.NewBestSuggestedBlock += e,
                e => BlockTree.NewBestSuggestedBlock -= e,
                (arg) => arg.Block.Number == untilBestSuggestedHeaderIs);

            if (BlockTree.BestSuggestedHeader?.Number == untilBestSuggestedHeaderIs) return;
            Task dLoop = FullSyncFeedComponent.Dispatcher.Start(cts.Token);
            FullSyncFeedComponent.Feed.Activate();

            await waitTask;
            FullSyncFeedComponent.Feed.Finish();
            await dLoop;
        }

        public async Task<SyncResponseHandlingResult> HandleOneRequest(PeerInfo peerInfo)
        {
            BlocksRequest blockRequest = await FullSyncFeedComponent.Feed.PrepareRequest(default);
            try
            {
                await FullSyncFeedComponent.Downloader.Dispatch(peerInfo, blockRequest, default);
            }
            catch (Exception)
            {
            }

            return FullSyncFeedComponent.Feed.HandleResponse(blockRequest, peerInfo);
        }

        public async Task<SyncResponseHandlingResult> HandleFastSyncOneRequest(PeerInfo peerInfo)
        {
            BlocksRequest blockRequest = await FastSyncFeedComponent.Feed.PrepareRequest(default);
            blockRequest.Should().NotBeNull();
            try
            {
                await FastSyncFeedComponent.Downloader.Dispatch(peerInfo, blockRequest, default);
            }
            catch (Exception)
            {
            }

            return FastSyncFeedComponent.Feed.HandleResponse(blockRequest, peerInfo);
        }

        public virtual void ShouldFastSyncedUntil(long blockNumber)
        {
            BlockTree.BestSuggestedHeader!.Number.Should().Be(blockNumber);
        }

        public void Deconstruct(out ResponseBuilder ResponseBuilder, out SyncFeedComponent<BlocksRequest> FastSyncFeedComponent, out SyncFeedComponent<BlocksRequest> FullSyncFeedComponent, out IForwardSyncController ForwardSyncController, out IBlockTree BlockTree, out InMemoryReceiptStorage ReceiptStorage, out ISyncPeerPool PeerPool)
        {
            ResponseBuilder = this.ResponseBuilder;
            FastSyncFeedComponent = this.FastSyncFeedComponent;
            FullSyncFeedComponent = this.FullSyncFeedComponent;
            ForwardSyncController = this.ForwardSyncController;
            BlockTree = this.BlockTree;
            ReceiptStorage = this.ReceiptStorage;
            PeerPool = this.PeerPool;
        }
    }

    private class SyncPeerMock : ISyncPeer
    {
        private readonly bool _withReceipts;
        private readonly bool _withWithdrawals;
        private readonly BlockHeadersMessageSerializer _headersSerializer = new();
        private readonly BlockBodiesMessageSerializer _bodiesSerializer = new();
        private readonly ReceiptsMessageSerializer _receiptsSerializer = new(MainnetSpecProvider.Instance);
        private readonly Response _flags;

        public IBlockTree BlockTree { get; private set; } = null!;
        private IReceiptStorage _receiptStorage = new InMemoryReceiptStorage();

        public string Name => "Mock";
        public DisconnectReason? DisconnectReason { get; private set; }

        public SyncPeerMock(long chainLength, bool withReceipts, Response flags, bool withWithdrawals = false)
        {
            _withReceipts = withReceipts;
            _withWithdrawals = withWithdrawals;
            _flags = flags;
            BuildTree(chainLength, withReceipts);
        }

        public SyncPeerMock(BlockTree blockTree, bool withReceipts, Response flags, UInt256 peerTotalDifficulty, bool withWithdrawals = false, IReceiptStorage? receiptStorage = null)
        {
            _withReceipts = withReceipts;
            _receiptStorage = receiptStorage!;
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

            if (timeoutOnFullBatch && number >= FullBatch)
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
            DisconnectReason = reason;
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

            if (timeoutOnFullBatch && number >= FullBatch)
            {
                throw new TimeoutException();
            }

            BlockHeader startBlock = _blockTree.FindHeader(_testHeaderMapping[startNumber], BlockTreeLookupOptions.None)!;
            if (startBlock is null)
            {
                throw new Exception($"Null start block {startNumber} {_testHeaderMapping[startNumber]}");
            }
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

                    BlockHeader header = blockHeaderBuilder.TestObject;
                    if (consistent && !_bodies.ContainsKey(header.Hash!))
                    {
                        Block block = BuildBlockForHeader(header, i, withTransaction);
                        _bodies[header.Hash!] = block.Body;
                    }
                    headers[i] = header;

                    if (allKnown)
                    {
                        _blockTree.SuggestHeader(header);
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

            if (timeoutOnFullBatch && blockHashes.Count >= FullBatch)
            {
                throw new TimeoutException();
            }

            BlockHeader[] blockHeaders = new BlockHeader[blockHashes.Count];
            BlockBody[] blockBodies = new BlockBody[blockHashes.Count];

            for (int i = 0; i < blockHashes.Count; i++)
            {
                if (consistent && _headers.TryGetValue(blockHashes[i], out var value))
                {
                    blockHeaders[i] = value;
                }
                else
                {
                    blockHeaders[i] = Build.A.BlockHeader.WithNumber(blockHeaders[i - 1].Number + 1).WithHash(blockHashes[i]).TestObject;
                }
                _headers[blockHashes[i]] = blockHeaders[i];
                var header = blockHeaders[i];

                BlockBody body = consistent
                    ? _bodies[blockHashes[i]]
                    : BuildBlockForHeader(header, i, withTransactions).Body;

                _testHeaderMapping[header.Number] = header.Hash!;

                blockBodies[i] = body;
                _bodies[blockHashes[i]] = blockBodies[i];

                if (allKnown)
                {
                    _blockTree.SuggestBlock(new Block(header, body));
                }

                if (justFirst) break;
            }

            using BlockBodiesMessage message = new(blockBodies);
            byte[] messageSerialized = _bodiesSerializer.Serialize(message);
            return await Task.FromResult(_bodiesSerializer.Deserialize(messageSerialized).Bodies!);
        }

        private Block BuildBlockForHeader(BlockHeader header, int txSeed, bool withTransactions)
        {
            BlockBuilder blockBuilder = Build.A.Block.WithHeader(header);

            if (withTransactions && header.TxRoot != Keccak.EmptyTreeHash)
            {
                blockBuilder.WithTransactions(
                    Build.A.Transaction.WithValue(txSeed * 2).SignedAndResolved().TestObject,
                    Build.A.Transaction.WithValue(txSeed * 2 + 1).SignedAndResolved().TestObject);
            }

            Block block = blockBuilder.TestObject;
            return block;
        }

        public async Task<IOwnedReadOnlyList<TxReceipt[]?>> BuildReceiptsResponse(IList<Hash256> blockHashes, Response flags = Response.AllCorrect)
        {
            TxReceipt[][] receipts = new TxReceipt[blockHashes.Count][];
            for (int i = 0; i < receipts.Length; i++)
            {
                BlockBody body = _bodies[blockHashes[i]];
                receipts[i] = body.Transactions
                    .Select(static t => Build.A.Receipt
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

        public BlockHeader? GetHeader(Hash256 hash) =>
            _headers.TryGetValue(hash, out var header) ? header : _blockTree.FindHeader(hash, BlockTreeLookupOptions.None)!;
    }
}
