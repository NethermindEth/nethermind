// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization;
using NSubstitute;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Tests for race condition handling in NewPayloadHandler event processing
/// </summary>
[TestFixture]
public class NewPayloadHandlerRaceConditionTests : BaseEngineModuleTests
{
    private static readonly FieldInfo? BlockValidationTasksField =
        typeof(NewPayloadHandler).GetField("_blockValidationTasks", BindingFlags.Instance | BindingFlags.NonPublic);

    [Test]
    public async Task NewPayloadV1_RaceCondition_EventHandling_Should_Not_Throw_When_Multiple_Completions()
    {
        // This test simulates the race condition that was fixed:
        // Multiple threads trying to complete the same TaskCompletionSource
        // and unsubscribe the same event handler multiple times

        using MergeTestBlockchain chain = await CreateBlockchain();

        // Create a block to process that will trigger the event handling mechanism
        Block block = Build.A.Block
            .WithNumber(1)
            .WithParent(chain.BlockTree.Head!)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithExtraData(new byte[32])
            .TestObject;

        block.Header.IsPostMerge = true;

        ExecutionPayload payload = ExecutionPayload.Create(block);

        // Create multiple concurrent calls to simulate race condition
        List<Task<ResultWrapper<PayloadStatusV1>>> tasks = [];
        const int concurrentCalls = 10;

        for (int i = 0; i < concurrentCalls; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    // Each task tries to process the same payload concurrently through the RPC module
                    return await chain.EngineRpcModule.engine_newPayloadV1(payload);
                }
                catch (Exception ex)
                {
                    // Before the fix, this would throw exceptions like:
                    // - InvalidOperationException from TaskCompletionSource.SetResult when already completed
                    // - InvalidOperationException from TaskCompletionSource.SetException when already completed
                    // - Potential ObjectDisposedException from event handler cleanup issues
                    TestContext.Out.WriteLine($"Exception caught: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }));
        }

        // Wait for all tasks to complete
        // Before the fix, some tasks would throw exceptions due to race conditions
        // After the fix, all tasks should complete without throwing
        ResultWrapper<PayloadStatusV1>[] results = await Task.WhenAll(tasks);

        // All tasks should complete successfully without throwing exceptions
        Assert.That(results.Length, Is.EqualTo(concurrentCalls));
        Assert.That(Array.TrueForAll(results, static r => r is not null), Is.True);

        // The results should be consistent (all should have the same status)
        ResultWrapper<PayloadStatusV1> firstResult = results[0];
        Assert.That(Array.TrueForAll(results, r => r.Data.Status == firstResult.Data.Status), Is.True);
    }

    [Test]
    public async Task ValidateBlockAndProcess_cleans_up_completion_when_block_tree_rejects_before_enqueue()
    {
        Block block = Build.A.Block
            .WithParentHash(TestItem.KeccakB)
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .TestObject;
        block.Header.IsPostMerge = true;

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.InvalidBlock,
            wasProcessed: false,
            validateSuggestedBlock: true);

        ResultWrapper<PayloadStatusV1> result = await handler.HandleAsync(ExecutionPayload.Create(block));

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
        Assert.That(GetPendingValidationTaskCount(handler), Is.EqualTo(0),
            "the completion source must be removed even when SuggestBlockAsync rejects the block before enqueue");
    }

    [Test]
    public async Task ValidateBlockAndProcess_cleans_up_completion_when_timeout_happens_before_block_removed()
    {
        Block block = PostMergeBlock();

        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue
            .Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>())
            .Returns(_ => ValueTask.CompletedTask);

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.Added,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 100);

        ResultWrapper<PayloadStatusV1> result = await handler.HandleAsync(ExecutionPayload.Create(block));

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Syncing));
        Assert.That(GetPendingValidationTaskCount(handler), Is.EqualTo(0),
            "timed out requests must not leave stale entries behind when BlockRemoved never arrives");
    }

    /// <summary>
    /// The verdict is final once the block is executed and validated; the commit and the chain update that follow
    /// do not change it, so newPayload answers on <see cref="IBlockProcessingQueue.BlockExecuted"/> and does not
    /// wait for <see cref="IBlockProcessingQueue.BlockRemoved"/>.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_answers_on_the_verdict_without_waiting_for_removal()
    {
        Block block = PostMergeBlock();

        TaskCompletionSource enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue
            .Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>())
            .Returns(_ =>
            {
                enqueued.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.Added,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 5_000);

        Task<ResultWrapper<PayloadStatusV1>> request = handler.HandleAsync(ExecutionPayload.Create(block));
        await enqueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The verdict lands; BlockRemoved never does, as if the commit were still running.
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        ResultWrapper<PayloadStatusV1> result = await request;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(GetPendingValidationTaskCount(handler), Is.EqualTo(0), "an answered request must not leave its completion behind");
        }
    }

    /// <summary>
    /// A payload sent again while its first copy is between verdict and commit is known to the tree but not yet
    /// marked processed. Queued again it would be skipped as not better than head and answered INVALID, so the
    /// handler waits for the first copy instead of enqueueing.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_waits_for_a_known_copy_still_in_the_queue_instead_of_enqueueing_again()
    {
        Block block = PostMergeBlock();

        // The first copy is between verdict and commit: the tree knows the block but has not marked it processed.
        bool committed = false;
        TaskCompletionSource waitRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstCopyRemoved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.WaitUntilRemovedAsync(block.Hash!, true).Returns(_ =>
        {
            waitRequested.TrySetResult();
            return new ValueTask(firstCopyRemoved.Task);
        });

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.AlreadyKnown,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 5_000,
            wasProcessedNow: () => committed);

        Task<ResultWrapper<PayloadStatusV1>> request = handler.HandleAsync(ExecutionPayload.Create(block));
        // Awaited first, so the assertion means the request is on the wait rather than parked at an earlier await.
        await waitRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(request.IsCompleted, Is.False, "the request must wait for the first copy rather than answer");

        committed = true;
        firstCopyRemoved.SetResult();
        ResultWrapper<PayloadStatusV1> result = await request;

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid), "once the first copy is committed the tree's answer is the block's");
        await processingQueue.DidNotReceive().Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>());
    }

    /// <summary>
    /// While a re-sent payload waits for its first copy, that copy's own removal lands on the completion registered
    /// for the hash. It must not become the re-submission's answer: the request re-validates the block with its own
    /// queue attempt and answers on that attempt's verdict.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_does_not_take_the_first_copy_removal_for_a_resubmission_answer()
    {
        Block block = PostMergeBlock();

        TaskCompletionSource waitRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstCopyRemoved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.WaitUntilRemovedAsync(block.Hash!, true).Returns(_ =>
        {
            waitRequested.TrySetResult();
            return new ValueTask(firstCopyRemoved.Task);
        });
        processingQueue
            .Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>())
            .Returns(_ =>
            {
                enqueued.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.AlreadyKnown,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 5_000);

        Task<ResultWrapper<PayloadStatusV1>> request = handler.HandleAsync(ExecutionPayload.Create(block));
        // Awaited first, so the assertion below means the request is on the wait, not at an earlier await.
        await waitRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The first copy's removal, published before the wait it releases completes.
        processingQueue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(block.Hash!, ProcessingResult.Success));
        Assert.That(request.IsCompleted, Is.False, "the first copy's removal is not this request's answer");
        firstCopyRemoved.SetResult();

        await enqueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(request.IsCompleted, Is.False, "the request must wait for its own attempt's verdict");
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        ResultWrapper<PayloadStatusV1> result = await request;

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid), "the answer is the re-submission's own verdict");
        await processingQueue.Received(1).Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>());
    }

    /// <summary>
    /// The first copy is marked processed part way through its removal, so a re-submission can find the flag set while
    /// the copy is still in flight. Its removal must not answer the re-submission - here one with an inclusion list of
    /// its own - the request waits for the copy to be gone and then judges its own list.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_waits_for_a_processed_first_copy_still_in_flight_before_judging_a_new_inclusion_list()
    {
        Block block = PostMergeBlock();

        TaskCompletionSource waitRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstCopyRemoved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.WaitUntilRemovedAsync(block.Hash!, true).Returns(_ =>
        {
            waitRequested.TrySetResult();
            return new ValueTask(firstCopyRemoved.Task);
        });
        processingQueue
            .Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>())
            .Returns(_ =>
            {
                enqueued.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.AlreadyKnown,
            wasProcessed: true,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 5_000);

        ExecutionPayloadV3 payload = ExecutionPayloadV3.Create(block);
        payload.InclusionListTransactions = [Rlp.Encode(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).TestObject).Bytes];
        Task<ResultWrapper<PayloadStatusV1>> request = handler.HandleAsync(payload);
        // Awaited first, so the assertion below means the request is on the wait, not at an earlier await.
        await waitRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

        processingQueue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(block.Hash!, ProcessingResult.Success));
        Assert.That(request.IsCompleted, Is.False, "the first copy's removal is not this request's answer");
        firstCopyRemoved.SetResult();

        await enqueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.InclusionListUnsatisfied));
        ResultWrapper<PayloadStatusV1> result = await request.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.InclusionListUnsatisfied), "the answer judges this request's own inclusion list");
    }

    /// <summary>
    /// The verdict is published before the block is committed, so a commit that then fails races the request's own
    /// cache write. Whichever of the two lands first, the answer must not stay cached: a block that never committed
    /// has to be processed again when the consensus client re-sends it, not answered VALID from the cache while
    /// every forkchoice update says SYNCING.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_does_not_leave_a_verdict_cached_for_a_block_that_never_commits()
    {
        Block block = PostMergeBlock();

        TaskCompletionSource firstEnqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondEnqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue
            .Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>())
            .Returns(_ =>
            {
                if (!firstEnqueued.TrySetResult()) secondEnqueued.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.Added,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 5_000);

        Task<ResultWrapper<PayloadStatusV1>> request = handler.HandleAsync(ExecutionPayload.Create(block));
        await firstEnqueued.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The verdict, and then a commit that throws: the request is answered VALID either way.
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        processingQueue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(block.Hash!, ProcessingResult.Exception, new Exception("commit failed")));

        ResultWrapper<PayloadStatusV1> answer = await request.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(answer.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        Task<ResultWrapper<PayloadStatusV1>> retry = handler.HandleAsync(ExecutionPayload.Create(block));
        await secondEnqueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        await retry.WaitAsync(TimeSpan.FromSeconds(10));

        await processingQueue.Received(2).Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>());
    }

    /// <summary>
    /// A second copy of a block that did commit is skipped as no better than the head the first copy became, and its
    /// removal reports a failure. That must not take the first copy's answer out of the cache: the block is in the
    /// chain, and the next payload for it is entitled to be answered from what the first copy established.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_keeps_the_answer_of_a_committed_block_when_another_copy_of_it_fails()
    {
        Block block = PostMergeBlock();

        TaskCompletionSource enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue
            .Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>())
            .Returns(_ =>
            {
                enqueued.TrySetResult();
                return ValueTask.CompletedTask;
            });

        bool committed = false;
        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.Added,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 5_000,
            wasProcessedNow: () => committed);

        Task<ResultWrapper<PayloadStatusV1>> request = handler.HandleAsync(ExecutionPayload.Create(block));
        await enqueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        await request.WaitAsync(TimeSpan.FromSeconds(10));

        // The block is in the chain now, and a copy of it that was queued earlier is skipped as no better than head.
        committed = true;
        processingQueue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(block.Hash!, ProcessingResult.ProcessingError, "skipped"));

        ResultWrapper<PayloadStatusV1> again = await handler.HandleAsync(ExecutionPayload.Create(block)).WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(again.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(GetPendingValidationTaskCount(handler), Is.EqualTo(0));
        }
        await processingQueue.Received(1).Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>());
    }

    /// <summary>
    /// A payload whose parent was answered VALID a moment ago finds the parent not yet marked processed. It must wait
    /// for the parent to leave the queue rather than be inserted for beacon sync and answered SYNCING.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task HandleAsync_waits_for_a_parent_still_committing_instead_of_answering_syncing()
    {
        Block block = PostMergeBlock();

        bool parentCommitted = false;
        TaskCompletionSource parentWaitRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource parentRemoved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.WaitUntilRemovedAsync(block.ParentHash!, true).Returns(_ =>
        {
            parentWaitRequested.TrySetResult();
            return new ValueTask(parentRemoved.Task);
        });
        processingQueue
            .Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>())
            .Returns(_ =>
            {
                enqueued.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.Added,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 5_000,
            parentProcessedNow: () => parentCommitted);

        Task<ResultWrapper<PayloadStatusV1>> request = handler.HandleAsync(ExecutionPayload.Create(block));

        await parentWaitRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(request.IsCompleted, Is.False, "the request waits for the parent rather than answer from its stale flag");
        await processingQueue.DidNotReceive().Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>());

        parentCommitted = true;
        parentRemoved.SetResult();
        await enqueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        ResultWrapper<PayloadStatusV1> result = await request.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
    }

    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_gives_up_on_a_known_copy_that_never_finishes()
    {
        Block block = PostMergeBlock();

        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.WaitUntilRemovedAsync(block.Hash!, true).Returns(new ValueTask(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task));

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.AlreadyKnown,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 100);

        ResultWrapper<PayloadStatusV1> result = await handler.HandleAsync(ExecutionPayload.Create(block));

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Syncing), "the request times out the way a pending block does");
        await processingQueue.DidNotReceive().Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>());
    }

    private static int GetPendingValidationTaskCount(NewPayloadHandler handler)
    {
        Assert.That(BlockValidationTasksField, Is.Not.Null, "_blockValidationTasks field not found - was it renamed?");

        return (int)BlockValidationTasksField!.FieldType
            .GetProperty("Count", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(BlockValidationTasksField.GetValue(handler)!)!;
    }

    /// <summary>The block every case here drives: post-merge, one past a parent none of them have.</summary>
    private static Block PostMergeBlock()
    {
        Block block = Build.A.Block
            .WithParentHash(TestItem.KeccakC)
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .TestObject;
        block.Header.IsPostMerge = true;
        return block;
    }

    private static NewPayloadHandler CreateHandler(
        Block block,
        AddBlockResult suggestBlockResult,
        bool wasProcessed,
        bool validateSuggestedBlock,
        IBlockProcessingQueue? processingQueue = null,
        int timeoutMs = 50,
        Func<bool>? wasProcessedNow = null,
        Func<bool>? parentProcessedNow = null)
    {
        IPayloadPreparationService payloadPreparationService = Substitute.For<IPayloadPreparationService>();
        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
        IBeaconPivot beaconPivot = Substitute.For<IBeaconPivot>();
        IBlockCacheService blockCacheService = Substitute.For<IBlockCacheService>();
        IBlockProcessingQueue effectiveProcessingQueue = processingQueue ?? Substitute.For<IBlockProcessingQueue>();
        IInvalidChainTracker invalidChainTracker = new NoopInvalidChainTracker();
        IMergeSyncController mergeSyncController = Substitute.For<IMergeSyncController>();
        IStateReader stateReader = Substitute.For<IStateReader>();
        IMergeConfig mergeConfig = new MergeConfig { TerminalTotalDifficulty = "0", NewPayloadBlockProcessingTimeout = timeoutMs };
        IReceiptConfig receiptConfig = new ReceiptConfig();

        BlockHeader parent = Build.A.BlockHeader
            .WithHash(block.ParentHash!)
            .WithNumber(block.Number - 1)
            .WithDifficulty(UInt256.Zero)
            .TestObject;
        parent.TotalDifficulty = UInt256.Zero;

        Block head = Build.A.Block.WithHeader(parent).TestObject;
        // The block builder recomputes its header's hash; the parent keeps the one the block names.
        parent.Hash = block.ParentHash;
        blockTree.Head.Returns(head);
        blockTree.SyncPivot.Returns((0UL, Keccak.Zero));
        blockTree.FindHeader(block.ParentHash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);
        // The tree has the block itself once it is suggested, which is how a removal tells a committed block from one that is not.
        blockTree.FindHeader(block.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(block.Header);
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(false);
        blockTree.GetInfo(parent.Number, parent.GetOrCalculateHash()).Returns(_ => (new BlockInfo(parent.Hash!, UInt256.Zero) { WasProcessed = parentProcessedNow?.Invoke() ?? true, BlockNumber = parent.Number }, null));
        blockTree.SuggestBlockAsync(Arg.Any<Block>(), Arg.Any<BlockTreeSuggestOptions>())
            .Returns(ValueTask.FromResult(suggestBlockResult));
        blockTree.WasProcessed(block.Number, block.Hash!).Returns(_ => wasProcessedNow?.Invoke() ?? wasProcessed);

        blockValidator.ValidateSuggestedBlock(Arg.Any<Block>(), Arg.Any<BlockHeader>(), out Arg.Any<string?>(), false)
            .Returns(callInfo =>
            {
                callInfo[2] = validateSuggestedBlock ? null : "invalid";
                return validateSuggestedBlock;
            });

        poSSwitcher.FinalTotalDifficulty.Returns((UInt256?)UInt256.Zero);
        poSSwitcher.TerminalTotalDifficulty.Returns((UInt256?)UInt256.Zero);
        poSSwitcher.TransitionFinished.Returns(true);
        beaconSyncStrategy.IsBeaconSyncFinished(Arg.Any<BlockHeader?>()).Returns(true);
        stateReader.HasStateForBlock(parent).Returns(true);
        if (processingQueue is null)
        {
            effectiveProcessingQueue.Count.Returns(0);
            effectiveProcessingQueue.Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>()).Returns(_ => ValueTask.CompletedTask);
        }

        return new NewPayloadHandler(
            payloadPreparationService,
            blockValidator,
            blockTree,
            poSSwitcher,
            beaconSyncStrategy,
            beaconPivot,
            blockCacheService,
            effectiveProcessingQueue,
            invalidChainTracker,
            mergeSyncController,
            mergeConfig,
            receiptConfig,
            stateReader,
            new RecoverSignatures(Substitute.For<IEthereumEcdsa>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance),
            Substitute.For<ISpecProvider>(),
            Substitute.For<ITxValidator>(),
            LimboLogs.Instance);
    }
}
