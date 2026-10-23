// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Nethermind.Network;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using Autofac;
using Nethermind.Config;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Synchronization.Peers.AllocationStrategies;


namespace Nethermind.Synchronization.Test;

public partial class ForwardHeaderProviderTests
{
    private const int SyncBatchSizeMax = 128;

    [TestCase(1L, 0, 64, 0, 1)]
    [TestCase(1L, 32, 64, null, null)]
    [TestCase(2L, 0, 64, 0, 2)]
    [TestCase(2L, 32, 64, null, null)]
    [TestCase(32L, 0, 64, 0, 32)]
    [TestCase(32L, 32, 64, null, null)]
    [TestCase(32L, 16, 64, 0, 16)]
    [TestCase(32L, 0, 16, 0, 15)]
    [TestCase(3L, 0, 64, 0, 3)]
    [TestCase(3L, 32, 64, null, null)]
    [TestCase(SyncBatchSizeMax * 8, 0, 64, 0, 63)]
    [TestCase(SyncBatchSizeMax * 8, 0, 64, 0, 63)]
    [TestCase(SyncBatchSizeMax * 8, 32, 64, 0, 63)]
    [TestCase(SyncBatchSizeMax * 8, 32, 64, 0, 63)]
    public async Task Happy_path(long headNumber, int skipLastN, int maxHeader, long? expectedStartNumber, long? expectedEndNumber)
    {
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeer = new(chainLength, false, Response.AllCorrect);
        PeerInfo peerInfo = new(syncPeer);

        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        ctx.ConfigureBestPeer(syncPeer);
        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        int maxNHeader = Math.Min(maxHeader, peerInfo!.MaxHeadersPerRequest());

        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders((ulong)skipLastN, (ulong)maxNHeader, CancellationToken.None);
        Assert.That(headers?[0]?.Number, Is.EqualTo(expectedStartNumber));
        Assert.That(headers?[^1]?.Number, Is.EqualTo(expectedEndNumber));
    }

    [Test]
    public async Task Ancestor_lookup_simple()
    {
        IBlockTree instance = CachedBlockTreeBuilder.OfLength(1024);
        await using IContainer node = CreateNode(builder =>
            builder.AddSingleton<IBlockTree>(instance));
        Context ctx = node.Resolve<Context>();
        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        Response blockResponseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2048 + 1, false, blockResponseOptions);
        ctx.ConfigureBestPeer(syncPeer);

        Block block1024 = Build.A.Block.WithParent(ctx.BlockTree.Head!).WithDifficulty(ctx.BlockTree.Head!.Difficulty + 1).TestObject;
        Block block1025 = Build.A.Block.WithParent(block1024).WithDifficulty(block1024.Difficulty + 1).TestObject;
        Block block1026 = Build.A.Block.WithParent(block1025).WithDifficulty(block1025.Difficulty + 1).TestObject;
        ctx.BlockTree.SuggestBlock(block1024);
        ctx.BlockTree.SuggestBlock(block1025);
        ctx.BlockTree.SuggestBlock(block1026);

        for (uint i = 0; i < 1023; i++)
        {
            Assert.That(syncPeer.BlockTree.FindBlock(i, BlockTreeLookupOptions.None)!.Hash, Is.EqualTo(ctx.BlockTree.FindBlock(i, BlockTreeLookupOptions.None)!.Hash), i.ToString());
        }

        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        Assert.That(headers?[0]?.Number, Is.EqualTo(1019));
        Assert.That(headers?[^1]?.Number, Is.EqualTo(1146));
    }

    [Test]
    public async Task Ancestor_lookup_with_sync_pivot()
    {
        SyncPeerMock syncPeer = new(1024, false, Response.AllCorrect);
        uint pivotNumber = 500;
        BlockHeader syncPivot = syncPeer.BlockTree.FindHeader(pivotNumber, BlockTreeLookupOptions.None)!;

        await using IContainer node = CreateNode(builder =>
        {
        }, new ConfigProvider(new SyncConfig()
        {
            PivotNumber = syncPivot.Number,
            PivotHash = syncPivot.Hash!.ToString(),
        }));

        // Simulate fast header adding the pivot.
        Context ctx = node.Resolve<Context>();
        Assert.That(ctx.BlockTree.Insert(syncPivot), Is.EqualTo(AddBlockResult.Added));
        ctx.ConfigureBestPeer(syncPeer);
        syncPeer.HeadNumber = 700;

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);

        Assert.That(headers?[0]?.Number, Is.EqualTo(pivotNumber));
    }

    [Test]
    public async Task Ancestor_failure_blocks()
    {
        await using IContainer node = CreateNode(builder =>
            builder.AddSingleton<IBlockTree>(CachedBlockTreeBuilder.OfLength(2048 + 1)));
        Context ctx = node.Resolve<Context>();
        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        Response responseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2072 + 1, true, responseOptions);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        Assert.That((await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)), Is.Null);
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    [Test]
    public async Task Peer_only_advertise_some_header([Values(33UL, 65UL)] ulong headNumber)
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ctx.ResponseBuilder.BuildHeaderResponse(0, (int)(headNumber + 1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(headNumber);

        ctx.ConfigureBestPeer(peerInfo);

        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        Assert.That(headers?[0]?.Number, Is.EqualTo(0));
        Assert.That(headers?[^1]?.Number, Is.EqualTo(headNumber));
    }

    [Test]
    public async Task Throws_on_inconsistent_batch()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<ulong>(0), ci.ArgAt<int>(1), Response.AllCorrect ^ Response.Consistent));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1024UL);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Assert.That((await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)), Is.Null);
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    // ForgedLastNumber breaks consecutive numbering; ShiftedWindow stays consecutive and hash-linked but is
    // rooted outside the requested window. Each is caught by a different half of the batch-consistency check.
    [TestCase(Response.ForgedLastNumber)]
    [TestCase(Response.ShiftedWindow)]
    public async Task Throws_on_out_of_window_header_numbers_before_validating_seals(Response badNumbers)
    {
        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        await using IContainer node = CreateNode(builder => builder.AddSingleton<ISealValidator>(sealValidator));
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<ulong>(0), ci.ArgAt<int>(1), Response.AllCorrect | badNumbers));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1024UL);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Assert.That((await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)), Is.Null);

        using (Assert.EnterMultipleScope())
        {
            ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
            // No seal may be looked at: the peer-chosen number selects the Ethash epoch, and validating it
            // builds that epoch's cache synchronously before the response could be rejected.
            sealValidator.DidNotReceiveWithAnyArgs().ValidateSeal(null!, default);
        }
    }

    [Test]
    public async Task Throws_on_invalid_seal()
    {
        await using IContainer node = CreateNode(builder => builder.AddSingleton<ISealValidator>(Always.Invalid));
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.IsInitialized.Returns(true);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<ulong>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000UL);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Assert.That((await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)), Is.Null);
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    [Test]
    public async Task Cache_block_headers_unless_peer_changed()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.IsInitialized.Returns(true);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<ulong>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000UL);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Assert.That((await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)), Is.Not.Null);
        Assert.That((await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)), Is.Not.Null);

        await syncPeer.Received(1).GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        ctx.PeerPool.GetPeer(peerInfo.SyncPeer.Node).Returns((PeerInfo?)null);
        ISyncPeer newSyncPeer = Substitute.For<ISyncPeer>();
        newSyncPeer.HeadHash.Returns(TestItem.KeccakB);
        newSyncPeer.HeadNumber.Returns(1000UL);
        newSyncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        newSyncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<ulong>(0), ci.ArgAt<int>(1), Response.AllCorrect));
        ctx.ConfigureBestPeer(new PeerInfo(newSyncPeer));

        Assert.That((await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)), Is.Not.Null);
        await syncPeer.Received(1).GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await newSyncPeer.Received(1).GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cache_block_headers_with_disposal()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.IsInitialized.Returns(true);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<ulong>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000UL);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        using IOwnedReadOnlyList<BlockHeader?>? headers1 = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        Assert.That(headers1, Is.Not.Null);
        using IOwnedReadOnlyList<BlockHeader?>? headers2 = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        Assert.That(headers2, Is.Not.Null);

        await syncPeer.Received(1).GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Does_not_allocate_peer_that_cannot_improve_chain()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        IPeerAllocationStrategy? strategy = null;
        int allocationTimeout = -1;
        ctx.PeerPool
            .Allocate(Arg.Do<IPeerAllocationStrategy>(s => strategy = s), Arg.Any<AllocationContexts>(), Arg.Do<int>(t => allocationTimeout = t), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SyncPeerAllocation(AllocationContexts.None)));

        using IOwnedReadOnlyList<BlockHeader?>? headers = await ctx.ForwardHeaderProvider.GetBlockHeaders(0, 128, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            // No allocatable peer returns promptly (one-shot timeout) instead of parking.
            Assert.That(headers, Is.Null);
            Assert.That(allocationTimeout, Is.EqualTo(0), "forward-header allocation must be one-shot");
            Assert.That(strategy, Is.Not.Null);
        }

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithTotalDifficulty(10_000_000).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithDifficulty(100_000).TestObject);
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();

        PeerInfo equalTdPeer = BuildInitializedPeer(TestItem.PublicKeyA, 10_000_000);
        PeerInfo betterTdPeer = BuildInitializedPeer(TestItem.PublicKeyB, 12_100_000);

        using (Assert.EnterMultipleScope())
        {
            // The provider only proceeds on strict TD improvement (ImprovementRequirementSatisfied),
            // and a uselessly allocated peer is never reported weak, so it stays selectable forever.
            Assert.That(strategy!.Allocate(null, [equalTdPeer], stats, blockTree), Is.Null);
            Assert.That(strategy.Allocate(null, [equalTdPeer, betterTdPeer], stats, blockTree), Is.SameAs(betterTdPeer));
            Assert.That(strategy.Allocate(null, [betterTdPeer], stats, blockTree), Is.SameAs(betterTdPeer));
        }
    }

    [Test]
    public async Task Retains_cached_tail_until_a_block_header_is_consumed()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        ISyncReport syncReport = node.Resolve<ISyncReport>();
        PeerInfo peerInfo = BuildInitializedPeer(TestItem.PublicKeyA, UInt256.MaxValue);
        ISyncPeer syncPeer = peerInfo.SyncPeer;
        syncPeer.HeadHash.Returns(TestItem.KeccakA);
        syncPeer.HeadNumber.Returns(40UL);
        ctx.ConfigureBestPeer(peerInfo);

        int headerRequests = 0;
        TaskCompletionSource<IOwnedReadOnlyList<BlockHeader>?> blockedHeaderResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ulong> blockedHeaderStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                headerRequests++;
                ulong start = callInfo.ArgAt<ulong>(0);
                if (headerRequests > 1)
                {
                    blockedHeaderStart.TrySetResult(start);
                    return blockedHeaderResponse.Task;
                }

                int count = Math.Min(callInfo.ArgAt<int>(1), (int)(41 - start));
                return ctx.ResponseBuilder.BuildHeaderResponse(start, count, Response.AllCorrect);
            });

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        using (IOwnedReadOnlyList<BlockHeader?>? initialHeaders = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None))
        {
            Assert.That(initialHeaders, Has.Count.EqualTo(41));
            syncPeer.HeadHash.Returns(initialHeaders![^1]!.Hash);
            UInt256 genesisDifficulty = ctx.BlockTree.Genesis!.TotalDifficulty.GetValueOrDefault();
            for (int i = 1; i < 40; i++)
            {
                BlockHeader header = initialHeaders![i]!;
                header.TotalDifficulty = genesisDifficulty + (ulong)i;
                Assert.That(ctx.BlockTree.SuggestBlock(new Block(header), BlockTreeSuggestOptions.None), Is.Not.EqualTo(AddBlockResult.UnknownParent));
                forwardHeader.OnSuggestBlock(BlockTreeSuggestOptions.ShouldProcess, new Block(header), AddBlockResult.Added);
            }
        }

        syncPeer.HeadNumber.Returns(42UL);
        syncPeer.HeadHash.Returns(TestItem.KeccakB);

        Task<IOwnedReadOnlyList<BlockHeader?>?> tailRequest = forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        IOwnedReadOnlyList<BlockHeader?>? tail = null;
        try
        {
            tail = await tailRequest.WaitAsync(TimeSpan.FromSeconds(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tail, Has.Count.EqualTo(2));
                Assert.That(headerRequests, Is.EqualTo(1), "the cached tail must be served before starting another header request");
                Assert.That(syncReport.FullSyncBlocksDownloaded.TargetValue, Is.EqualTo(42UL),
                    "cached header responses must track the owner's current advertised head");
            }
        }
        finally
        {
            if (headerRequests > 1)
            {
                ulong start = await blockedHeaderStart.Task.WaitAsync(TimeSpan.FromSeconds(2));
                blockedHeaderResponse.TrySetResult(await ctx.ResponseBuilder.BuildHeaderResponse(start, 1, Response.AllCorrect));
            }

            if (tail is null)
            {
                IOwnedReadOnlyList<BlockHeader?>? cleanup = await tailRequest.WaitAsync(TimeSpan.FromSeconds(2));
                cleanup?.Dispose();
            }
            else
            {
                tail.Dispose();
            }
        }
    }

    [Test]
    public async Task Cached_pin_falls_back_when_owner_is_not_eligible([Values(
        PinSafety.OwnerRemoved,
        PinSafety.OwnerSleeping,
        PinSafety.OwnerUninitialized,
        PinSafety.OwnerNoLongerImproves)] PinSafety safety)
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        using CachedPinSetup setup = await CreateCachedPinSetup(ctx);
        ISyncPeer syncPeer = setup.PeerInfo.SyncPeer;

        PeerInfo replacementPeer = BuildInitializedPeer(TestItem.PublicKeyB, UInt256.MaxValue);
        ISyncPeer replacementSyncPeer = replacementPeer.SyncPeer;
        replacementSyncPeer.HeadHash.Returns(TestItem.KeccakB);
        replacementSyncPeer.HeadNumber.Returns(1000UL);
        ctx.PeerPool.GetPeer(replacementSyncPeer.Node).Returns(replacementPeer);

        int replacementRequests = 0;
        replacementSyncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                replacementRequests++;
                return ctx.ResponseBuilder.BuildHeaderResponse(callInfo.ArgAt<ulong>(0), 4, Response.AllCorrect);
            });

        int allocations = 0;
        ctx.PeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref allocations);
                SyncPeerAllocation allocation = new(AllocationContexts.ForwardHeader, null);
                allocation.AllocatePeer(replacementPeer);
                return Task.FromResult(allocation);
            });

        switch (safety)
        {
            case PinSafety.OwnerRemoved:
                ctx.PeerPool.GetPeer(setup.PeerInfo.SyncPeer.Node).Returns((PeerInfo?)null);
                break;
            case PinSafety.OwnerSleeping:
                setup.PeerInfo.PutToSleep(AllocationContexts.ForwardHeader, DateTime.UtcNow);
                break;
            case PinSafety.OwnerUninitialized:
                syncPeer.IsInitialized.Returns(false);
                break;
            case PinSafety.OwnerNoLongerImproves:
                syncPeer.TotalDifficulty.Returns(UInt256.Zero);
                break;
        }

        using IOwnedReadOnlyList<BlockHeader?>? response = await setup.ForwardHeader.GetBlockHeaders(0, 4, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.Not.Null);
            Assert.That(response, Has.Count.EqualTo(4));
            Assert.That(setup.HeaderRequests + replacementRequests, Is.EqualTo(2), "an ineligible owner must force a fresh header response");
            Assert.That(allocations, Is.EqualTo(1), "the ineligible owner must trigger one replacement allocation");
        }
    }

    [Test]
    public async Task Cached_pin_respects_bounds_and_cancellation([Values(1, 2)] int maxHeaders)
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        using CachedPinSetup setup = await CreateCachedPinSetup(ctx);

        using (IOwnedReadOnlyList<BlockHeader?>? bounded = await setup.ForwardHeader.GetBlockHeaders(0, (ulong)maxHeaders, CancellationToken.None))
        {
            if (maxHeaders == 1)
            {
                Assert.That(bounded, Is.Null);
            }
            else
            {
                Assert.That(bounded, Is.Not.Null);
                Assert.That(bounded, Has.Count.EqualTo(2));
            }
        }

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await setup.ForwardHeader.GetBlockHeaders(0, 4, cancelled.Token));
        Assert.That(setup.HeaderRequests, Is.EqualTo(1), "bounds and cancellation must not issue a network header request");
    }

    [Test]
    public async Task Cached_pin_respects_skip_last_n_and_falls_back_for_different_suggested_height()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        using CachedPinSetup setup = await CreateCachedPinSetup(ctx);

        using (IOwnedReadOnlyList<BlockHeader?>? bounded = await setup.ForwardHeader.GetBlockHeaders(998, 4, CancellationToken.None))
        {
            Assert.That(bounded, Is.Not.Null);
            Assert.That(bounded, Has.Count.EqualTo(2));
            Assert.That(bounded![0]!.Number, Is.EqualTo(setup.Initial[1]!.Number));
            Assert.That(bounded[^1]!.Number, Is.EqualTo(setup.Initial[1]!.Number + 1));
        }

        UInt256 conflictDifficultyWide = setup.Initial[1]!.TotalDifficulty.GetValueOrDefault() + UInt256.One;
        Assert.That(conflictDifficultyWide, Is.LessThan(UInt256.MaxValue));
        BlockHeader differentHeight = Build.A.BlockHeader
            .WithParent(setup.Initial[1]!)
            .WithHash(TestItem.KeccakB)
            .TestObject;
        differentHeight.TotalDifficulty = conflictDifficultyWide;
        Assert.That(ctx.BlockTree.SuggestBlock(new Block(differentHeight), BlockTreeSuggestOptions.None), Is.Not.EqualTo(AddBlockResult.UnknownParent));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(differentHeight.Number));
            Assert.That(ctx.BlockTree.BestSuggestedHeader.Hash, Is.EqualTo(differentHeight.Hash));
        }

        using IOwnedReadOnlyList<BlockHeader?>? fallback = await setup.ForwardHeader.GetBlockHeaders(0, 4, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fallback, Is.Not.Null);
            Assert.That(fallback, Has.Count.EqualTo(3));
            Assert.That(setup.HeaderRequests, Is.EqualTo(1), "a different suggested height must use the original cache flow");
        }
    }

    [Test]
    public async Task Cached_pin_exhaustion_clears_window_before_fresh_allocation()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        using CachedPinSetup setup = await CreateCachedPinSetup(ctx);

        using IOwnedReadOnlyList<BlockHeader?>? cached = await setup.ForwardHeader.GetBlockHeaders(0, 2, CancellationToken.None);
        Assert.That(cached, Is.Not.Null);
        Assert.That(cached, Has.Count.EqualTo(2));
        Assert.That(ctx.BlockTree.SuggestBlock(new Block(setup.Initial[2]!), BlockTreeSuggestOptions.None), Is.Not.EqualTo(AddBlockResult.UnknownParent));
        Assert.That(ctx.BlockTree.SuggestBlock(new Block(setup.Initial[3]!), BlockTreeSuggestOptions.None), Is.Not.EqualTo(AddBlockResult.UnknownParent));
        setup.ForwardHeader.OnSuggestBlock(BlockTreeSuggestOptions.ShouldProcess, new Block(setup.Initial[2]!), AddBlockResult.Added);
        setup.ForwardHeader.OnSuggestBlock(BlockTreeSuggestOptions.ShouldProcess, new Block(setup.Initial[3]!), AddBlockResult.Added);

        using IOwnedReadOnlyList<BlockHeader?>? fresh = await setup.ForwardHeader.GetBlockHeaders(0, 2, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fresh, Is.Not.Null);
            Assert.That(fresh, Has.Count.EqualTo(2));
            Assert.That(setup.HeaderRequests, Is.EqualTo(2), "an exhausted cached window must allocate a fresh header response");
        }
    }

    [Test]
    public async Task Cached_pin_invalidates_conflicting_anchor_or_advertised_head([Values(PinConflict.Anchor, PinConflict.AdvertisedHead)] PinConflict conflict)
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        using CachedPinSetup setup = await CreateCachedPinSetup(ctx);
        ISyncPeer syncPeer = setup.PeerInfo.SyncPeer;

        bool expectNoResponse = conflict == PinConflict.AdvertisedHead;
        if (conflict == PinConflict.Anchor)
        {
            UInt256 conflictDifficultyWide = setup.Initial[1]!.TotalDifficulty.GetValueOrDefault() + UInt256.One;
            Assert.That(conflictDifficultyWide, Is.LessThan(UInt256.MaxValue));
            BlockHeader conflicting = Build.A.BlockHeader
                .WithParent(ctx.BlockTree.Genesis!)
                .WithNumber(setup.Initial[1]!.Number)
                .WithHash(TestItem.KeccakB)
                .TestObject;
            conflicting.TotalDifficulty = conflictDifficultyWide;
            Assert.That(ctx.BlockTree.SuggestBlock(new Block(conflicting), BlockTreeSuggestOptions.None), Is.Not.EqualTo(AddBlockResult.UnknownParent));
            Assert.That(ctx.BlockTree.BestSuggestedHeader!.Hash, Is.EqualTo(conflicting.Hash));
        }
        else
        {
            syncPeer.HeadNumber.Returns(setup.Initial[1]!.Number);
            syncPeer.HeadHash.Returns(TestItem.KeccakB);
        }

        using IOwnedReadOnlyList<BlockHeader?>? response = await setup.ForwardHeader.GetBlockHeaders(0, 4, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response is null, Is.EqualTo(expectNoResponse));
            Assert.That(setup.HeaderRequests, Is.EqualTo(expectNoResponse ? 1 : 2));
        }
    }

    public enum PinSafety
    {
        OwnerRemoved,
        OwnerSleeping,
        OwnerUninitialized,
        OwnerNoLongerImproves,
    }

    public enum PinConflict
    {
        Anchor,
        AdvertisedHead,
    }

    private static async Task<CachedPinSetup> CreateCachedPinSetup(Context ctx)
    {
        PeerInfo peerInfo = BuildInitializedPeer(TestItem.PublicKeyA, UInt256.MaxValue);
        ISyncPeer syncPeer = peerInfo.SyncPeer;
        syncPeer.HeadHash.Returns(TestItem.KeccakA);
        syncPeer.HeadNumber.Returns(1000UL);
        ctx.ConfigureBestPeer(peerInfo);

        int headerRequests = 0;
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                headerRequests++;
                return ctx.ResponseBuilder.BuildHeaderResponse(
                    callInfo.ArgAt<ulong>(0),
                    callInfo.ArgAt<int>(1),
                    Response.AllCorrect);
            });

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        IOwnedReadOnlyList<BlockHeader?> initial = await forwardHeader.GetBlockHeaders(0, 4, CancellationToken.None)
            ?? throw new AssertionException("The initial header response was not prepared.");
        try
        {
            BlockHeader parent = initial[1]!;
            SuggestParent(ctx, parent);
            forwardHeader.OnSuggestBlock(BlockTreeSuggestOptions.ShouldProcess, new Block(parent), AddBlockResult.Added);
            return new CachedPinSetup(peerInfo, forwardHeader, initial, () => headerRequests);
        }
        catch
        {
            initial.Dispose();
            throw;
        }
    }

    private sealed record CachedPinSetup(
        PeerInfo PeerInfo,
        IForwardHeaderProvider ForwardHeader,
        IOwnedReadOnlyList<BlockHeader?> Initial,
        Func<int> HeaderRequestCount) : IDisposable
    {
        public int HeaderRequests => HeaderRequestCount();

        public void Dispose() => Initial.Dispose();
    }

    private static void SuggestParent(Context ctx, BlockHeader parent)
    {
        parent.TotalDifficulty = ctx.BlockTree.Genesis!.TotalDifficulty.GetValueOrDefault() + UInt256.One;
        Assert.That(ctx.BlockTree.SuggestBlock(new Block(parent), BlockTreeSuggestOptions.None), Is.Not.EqualTo(AddBlockResult.UnknownParent));
    }

    private static PeerInfo BuildInitializedPeer(PublicKey publicKey, UInt256 totalDifficulty)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.IsInitialized.Returns(true);
        syncPeer.TotalDifficulty.Returns(totalDifficulty);
        syncPeer.HeadNumber.Returns(10UL);
        syncPeer.Node.Returns(new Node(publicKey, "127.0.0.1", 30303));
        return new PeerInfo(syncPeer);
    }

    // Cancels the request from inside the first validation, so the cancellation lands while seals are being validated:
    // a CancelAfter timer could not fire on a pool whose threads all sleep in this validator.
    private class SlowSealValidator(CancellationTokenSource cancellation) : ISealValidator
    {
        private int _sealsValidated;

        public int SealsValidated => Volatile.Read(ref _sealsValidated);

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
        {
            cancellation.Cancel();
            Thread.Sleep(200);
            return true;
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            Interlocked.Increment(ref _sealsValidated);
            cancellation.Cancel();
            Thread.Sleep(200);
            return true;
        }
    }

    [Test, MaxTime(7000)]
    public async Task Can_cancel_seal_validation()
    {
        using CancellationTokenSource cancellation = new();
        SlowSealValidator sealValidator = new(cancellation);
        await using IContainer node = CreateNode(builder => builder.AddSingleton<ISealValidator>(sealValidator));
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<ulong>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1000UL);

        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Func<Task> headerTask = () => forwardHeader.GetBlockHeaders(0, 128, cancellation.Token);
        Assert.That(async () => await headerTask(), Throws.InstanceOf<OperationCanceledException>());
        // The validation itself must stop early: only the seals already in flight when the token was cancelled run.
        Assert.That(sealValidator.SealsValidated, Is.InRange(1, 127));
    }

    [Test]
    public async Task Validate_always_the_last_seal_and_random_seal_in_the_package()
    {
        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        await using IContainer node = CreateNode(builder => builder.AddSingleton<ISealValidator>(sealValidator));
        Context ctx = node.Resolve<Context>();

        using IOwnedReadOnlyList<BlockHeader>? blockHeaders = await ctx.ResponseBuilder.BuildHeaderResponse(0, 512, Response.AllCorrect);
        BlockHeader[] blockHeadersCopy = blockHeaders?.ToArray() ?? [];
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(blockHeaders);

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(511UL);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        using IOwnedReadOnlyList<BlockHeader?>? _ = await forwardHeader.GetBlockHeaders(0, 128, default);

        sealValidator.Received(2).ValidateSeal(Arg.Any<BlockHeader>(), true);
        sealValidator.Received(510).ValidateSeal(Arg.Any<BlockHeader>(), false);
        sealValidator.Received().ValidateSeal(blockHeadersCopy![^1], true);
    }

    private class ThrowingPeer(ulong number, UInt256? totalDiff, Hash256? headHash = null) : ISyncPeer
    {
        public string Name => "Throwing";
        public string ClientId => "EX peer";
        public Node Node { get; } = null!;
        public string ProtocolCode { get; } = null!;
        public byte ProtocolVersion { get; } = default;
        public Hash256 HeadHash { get; set; } = headHash ?? Keccak.Zero;
        public ulong HeadNumber { get; set; } = number;
        public UInt256? TotalDifficulty { get; set; } = totalDiff ?? UInt256.MaxValue;
        public bool IsInitialized { get; set; }
        public bool IsPriority { get; set; }

        public void Disconnect(DisconnectReason reason, string details) =>
            throw new NotImplementedException();

        public Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 blockHash, int maxBlocks, int skip, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(ulong number, int maxBlocks, int skip, CancellationToken token) =>
            throw new InvalidOperationException();

        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token) =>
            throw new NotImplementedException();

        public void NotifyOfNewBlock(Block block, SendBlockMode mode) =>
            throw new NotImplementedException();

        public PublicKey Id => Node.Id;

        public void SendNewTransactions(ReadOnlySpan<Transaction> txs, bool sendFullTx) =>
            throw new NotImplementedException();

        public Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<IByteArrayList> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token) =>
            throw new NotImplementedException();

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class =>
            throw new NotImplementedException();

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class =>
            throw new NotImplementedException();
    }

    [Test]
    public async Task Faults_on_get_headers_faulting()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = new ThrowingPeer(1000, UInt256.MaxValue);
        ctx.ConfigureBestPeer(syncPeer);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Func<Task> headerTask = () => forwardHeader.GetBlockHeaders(0, 128, default);
        Assert.That(async () => await headerTask(), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Reports_weak_peer_on_timeout_cancellation()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1000UL);
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IOwnedReadOnlyList<BlockHeader>?>>(x => throw new OperationCanceledException());

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Assert.That((await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)), Is.Null);
        ctx.PeerPool.Received().ReportWeakPeer(peerInfo, AllocationContexts.ForwardHeader);
    }

    [Test]
    public async Task Throws_on_sync_cancellation()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        using CancellationTokenSource cts = new();
        cts.Cancel();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1000UL);
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IOwnedReadOnlyList<BlockHeader>?>>(x => throw new OperationCanceledException());

        ctx.ConfigureBestPeer(syncPeer);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Func<Task> act = () => forwardHeader.GetBlockHeaders(0, 128, cts.Token);
        Assert.That(async () => await act(), Throws.InstanceOf<OperationCanceledException>());
    }

    [Flags]
    public enum Response
    {
        Consistent = 1,
        AllCorrect = 7,
        JustFirst = 8,
        AllKnown = 16,
        TimeoutOnFullBatch = 32,
        ForgedLastNumber = 64,
        WithTransactions = 128,
        ShiftedWindow = 256,
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

            .AddSingleton<Dictionary<ulong, Hash256>, IBlockTree>((blockTree) => new Dictionary<ulong, Hash256>()
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

    private record Context(
        ResponseBuilder ResponseBuilder,
        IForwardHeaderProvider ForwardHeaderProvider,
        IBlockTree BlockTree,
        ISyncPeerPool PeerPool
    )
    {
        public void ConfigureBestPeer(ISyncPeer syncPeer) =>
            ConfigureBestPeer(new PeerInfo(syncPeer));

        public void ConfigureBestPeer(PeerInfo peerInfo)
        {
            PeerPool.GetPeer(peerInfo.SyncPeer.Node).Returns(peerInfo);
            PeerPool
                .Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    SyncPeerAllocation peerAllocation = new(ci.ArgAt<AllocationContexts>(1), null);
                    peerAllocation.AllocatePeer(peerInfo);
                    return Task.FromResult(peerAllocation);
                });
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

        public Node Node { get; } = null!;
        public string ClientId { get; } = null!;
        public byte ProtocolVersion { get; } = default;
        public string ProtocolCode { get; } = null!;
        public Hash256 HeadHash { get; set; } = null!;
        public PublicKey Id => Node.Id;
        public ulong HeadNumber { get; set; }
        public UInt256? TotalDifficulty { get; set; }
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

        public async Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(ulong number, int maxBlocks, int skip, CancellationToken token)
        {
            bool justFirst = _flags.HasFlag(Response.JustFirst);
            bool timeoutOnFullBatch = _flags.HasFlag(Response.TimeoutOnFullBatch);

            if (timeoutOnFullBatch && number == SyncBatchSizeMax)
            {
                throw new TimeoutException();
            }

            BlockHeader[] headers = new BlockHeader[maxBlocks];
            for (int i = 0; i < (justFirst ? 1 : maxBlocks); i++)
            {
                headers[i] = BlockTree.FindHeader(number + (ulong)i, BlockTreeLookupOptions.None)!;
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

        public void Disconnect(DisconnectReason reason, string details) =>
            DisconnectReason = reason;

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token) =>
            throw new NotImplementedException();

        public void NotifyOfNewBlock(Block block, SendBlockMode mode) =>
            throw new NotImplementedException();

        public void SendNewTransactions(ReadOnlySpan<Transaction> txs, bool sendFullTx) =>
            throw new NotImplementedException();

        public Task<IByteArrayList> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token) =>
            throw new NotImplementedException();

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class =>
            throw new NotImplementedException();

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class =>
            throw new NotImplementedException();
    }

    private class ResponseBuilder(IBlockTree blockTree, Dictionary<ulong, Hash256> testHeaderMapping)
    {
        private readonly IBlockTree _blockTree = blockTree;
        private readonly Dictionary<ulong, Hash256> _testHeaderMapping = testHeaderMapping;

        public async Task<IOwnedReadOnlyList<BlockHeader>?> BuildHeaderResponse(ulong startNumber, int number, Response flags)
        {
            bool consistent = flags.HasFlag(Response.Consistent);
            bool justFirst = flags.HasFlag(Response.JustFirst);
            bool allKnown = flags.HasFlag(Response.AllKnown);
            bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);
            bool forgedLastNumber = flags.HasFlag(Response.ForgedLastNumber);
            bool shiftedWindow = flags.HasFlag(Response.ShiftedWindow);
            bool withTransaction = flags.HasFlag(Response.WithTransactions);

            if (timeoutOnFullBatch && number == SyncBatchSizeMax)
            {
                throw new TimeoutException();
            }

            BlockHeader startBlock = _blockTree.FindHeader(_testHeaderMapping[startNumber], BlockTreeLookupOptions.None)!;
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

                    _testHeaderMapping[startNumber + (ulong)i] = headers[i].Hash!;
                }
            }

            foreach (BlockHeader header in headers)
            {
                _headers[header.Hash!] = header;
            }

            if (shiftedWindow && number > 1)
            {
                // Consecutive and hash-linked, but rooted outside the requested window: only the
                // requested-start anchor can reject this.
                for (int i = 0; i < headers.Length; i++)
                {
                    BlockHeader shifted = headers[i].Clone();
                    shifted.Number += 1_000_000;
                    if (i > 0)
                    {
                        shifted.ParentHash = headers[i - 1].Hash;
                    }

                    shifted.Hash = shifted.CalculateHash();
                    headers[i] = shifted;
                }
            }

            if (forgedLastNumber && number > 1)
            {
                // The parent hash still links, so only the block-number check can catch this. Epoch
                // 10_000 is what a real Ethash node would build a >1 GiB cache for.
                BlockHeader forged = headers[^1].Clone();
                forged.Number += 300_000_000;
                headers[^1] = forged;
            }

            using BlockHeadersMessage message = new(headers.ToPooledList());
            byte[] messageSerialized = _headersSerializer.Serialize(message);
            return await Task.FromResult(_headersSerializer.Deserialize(messageSerialized).BlockHeaders);
        }

        private readonly BlockHeadersMessageSerializer _headersSerializer = new();
        private readonly BlockBodiesMessageSerializer _bodiesSerializer = new();
        private readonly ReceiptsMessageSerializer _receiptsSerializer = new(MainnetSpecProvider.Instance);
        private readonly Dictionary<Hash256, BlockHeader> _headers = [];
        private readonly Dictionary<Hash256, BlockBody> _bodies = [];

        public async Task<OwnedBlockBodies> BuildBlocksResponse(IList<Hash256> blockHashes, Response flags)
        {
            bool consistent = flags.HasFlag(Response.Consistent);
            bool justFirst = flags.HasFlag(Response.JustFirst);
            bool allKnown = flags.HasFlag(Response.AllKnown);
            bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);
            bool withTransactions = flags.HasFlag(Response.WithTransactions);

            if (timeoutOnFullBatch && blockHashes.Count == SyncBatchSizeMax)
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

            blockBodies[0] = BuildBlockForHeader(startHeader, 0).Body;
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

                    _testHeaderMapping[startHeader.Number + (ulong)i] = blockHeaders[i].Hash!;

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
    }
}
