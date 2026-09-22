// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class ForkchoiceUpdatedHandlerTests
{
    /// <summary>
    /// newPayload answers on the verdict, before the block is committed and marked processed, and the CL's forkchoice
    /// update follows at once. The handler must wait for the block to leave the processing queue and read its info
    /// again, rather than answer SYNCING from the not-yet-processed flag.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task Handle_waits_for_the_head_block_to_leave_the_queue_before_reading_its_processed_flag()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        Block newHead = Build.A.Block.WithNumber(2).WithParentHash(parent.Hash!).WithDifficulty(0).WithNonce(0).TestObject;
        Hash256 newHeadHash = newHead.Hash!;

        bool committed = false;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(newHeadHash, Arg.Any<BlockTreeLookupOptions>()).Returns(newHead.Header);
        blockTree.FindHeader(parent.Hash!, Arg.Any<BlockTreeLookupOptions>()).Returns(parent);
        blockTree.GetInfo(newHead.Number, newHeadHash).Returns(_ => (new BlockInfo(newHeadHash, UInt256.Zero) { WasProcessed = committed, BlockNumber = newHead.Number }, null));
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);
        // Committed by the time the wait returns, and already the head: the update itself has nothing left to move.
        blockTree.Head.Returns(newHead);

        TaskCompletionSource waitRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.Count.Returns(1);
        processingQueue.WaitUntilRemovedAsync(newHeadHash, true).Returns(_ =>
        {
            waitRequested.TrySetResult();
            return new ValueTask(removed.Task);
        });

        ForkchoiceUpdatedHandler handler = CreateHandler(blockTree, processingQueue);

        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> request = handler.Handle(new ForkchoiceStateV1(newHeadHash, parent.Hash!, parent.Hash!), null, 1);

        await waitRequested.Task;
        Assert.That(request.IsCompleted, Is.False, "the handler waits for the block rather than answer from the stale flag");

        committed = true;
        removed.SetResult();
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await request;

        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    /// <summary>With copies of other blocks queued ahead, the head block is not a moment away; the handler does not wait.</summary>
    [Test, MaxTime(10_000)]
    public async Task Handle_does_not_wait_behind_a_backlog()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).TestObject;
        Block newHead = Build.A.Block.WithNumber(2).WithParentHash(parent.Hash!).WithDifficulty(0).WithNonce(0).TestObject;
        Hash256 newHeadHash = newHead.Hash!;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(newHeadHash, Arg.Any<BlockTreeLookupOptions>()).Returns(newHead.Header);
        blockTree.FindHeader(parent.Hash!, Arg.Any<BlockTreeLookupOptions>()).Returns(parent);
        blockTree.GetInfo(newHead.Number, newHeadHash).Returns((new BlockInfo(newHeadHash, UInt256.Zero) { WasProcessed = false, BlockNumber = newHead.Number }, null));
        blockTree.Head.Returns(Build.A.Block.WithHeader(parent).TestObject);

        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.Count.Returns(2);

        ForkchoiceUpdatedHandler handler = CreateHandler(blockTree, processingQueue);

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await handler.Handle(new ForkchoiceStateV1(newHeadHash, parent.Hash!, parent.Hash!), null, 1);

        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Syncing));
        await processingQueue.DidNotReceive().WaitUntilRemovedAsync(Arg.Any<Hash256>(), Arg.Any<bool>());
    }

    /// <summary>The handler with everything but the tree and the queue stubbed away, which are what these cases drive.</summary>
    private static ForkchoiceUpdatedHandler CreateHandler(IBlockTree blockTree, IBlockProcessingQueue processingQueue) =>
        new(blockTree,
            Substitute.For<IPoSSwitcher>(),
            Substitute.For<IPayloadPreparationService>(),
            processingQueue,
            Substitute.For<IBlockCacheService>(),
            Substitute.For<IInvalidChainTracker>(),
            Substitute.For<IMergeSyncController>(),
            Substitute.For<IBeaconPivot>(),
            Substitute.For<IPeerRefresher>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<ISyncPeerPool>(),
            new MergeConfig { NewPayloadBlockProcessingTimeout = 5_000 },
            LimboLogs.Instance);
}
