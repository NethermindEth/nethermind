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
        Block block = Build.A.Block
            .WithParentHash(TestItem.KeccakC)
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .TestObject;
        block.Header.IsPostMerge = true;

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
        Block block = Build.A.Block
            .WithParentHash(TestItem.KeccakC)
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .TestObject;
        block.Header.IsPostMerge = true;

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
        await enqueued.Task;
        // The verdict lands; BlockRemoved never does, as if the commit were still running.
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        ResultWrapper<PayloadStatusV1> result = await request;

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(GetPendingValidationTaskCount(handler), Is.EqualTo(0), "an answered request must not leave its completion behind");
    }

    /// <summary>
    /// A payload sent again while its first copy is between verdict and commit is known to the tree but not yet
    /// marked processed. Queued again it would be skipped as not better than head and answered INVALID, so the
    /// handler waits for the first copy instead of enqueueing.
    /// </summary>
    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_waits_for_a_known_copy_still_in_the_queue_instead_of_enqueueing_again()
    {
        Block block = Build.A.Block
            .WithParentHash(TestItem.KeccakC)
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .TestObject;
        block.Header.IsPostMerge = true;

        // The first copy is between verdict and commit: the tree knows the block but has not marked it processed.
        bool committed = false;
        TaskCompletionSource firstCopyRemoved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.WaitUntilRemovedAsync(block.Hash!).Returns(new ValueTask(firstCopyRemoved.Task));

        using NewPayloadHandler handler = CreateHandler(
            block,
            suggestBlockResult: AddBlockResult.AlreadyKnown,
            wasProcessed: false,
            validateSuggestedBlock: true,
            processingQueue: processingQueue,
            timeoutMs: 5_000,
            wasProcessedNow: () => committed);

        Task<ResultWrapper<PayloadStatusV1>> request = handler.HandleAsync(ExecutionPayload.Create(block));
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
    public async Task ValidateBlockAndProcess_does_not_take_the_first_copys_removal_for_a_resubmissions_answer()
    {
        Block block = Build.A.Block
            .WithParentHash(TestItem.KeccakC)
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .TestObject;
        block.Header.IsPostMerge = true;

        TaskCompletionSource firstCopyRemoved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.WaitUntilRemovedAsync(block.Hash!).Returns(new ValueTask(firstCopyRemoved.Task));
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

        // The first copy's removal, published before the wait it releases completes.
        processingQueue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(block.Hash!, ProcessingResult.Success));
        Assert.That(request.IsCompleted, Is.False, "the first copy's removal is not this request's answer");
        firstCopyRemoved.SetResult();

        await enqueued.Task;
        Assert.That(request.IsCompleted, Is.False, "the request must wait for its own attempt's verdict");
        processingQueue.BlockExecuted += Raise.EventWith(new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        ResultWrapper<PayloadStatusV1> result = await request;

        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid), "the answer is the re-submission's own verdict");
        await processingQueue.Received(1).Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>());
    }

    [Test, MaxTime(10_000)]
    public async Task ValidateBlockAndProcess_gives_up_on_a_known_copy_that_never_finishes()
    {
        Block block = Build.A.Block
            .WithParentHash(TestItem.KeccakC)
            .WithNumber(1)
            .WithDifficulty(0)
            .WithNonce(0)
            .TestObject;
        block.Header.IsPostMerge = true;

        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.WaitUntilRemovedAsync(block.Hash!).Returns(new ValueTask(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task));

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

    private static NewPayloadHandler CreateHandler(
        Block block,
        AddBlockResult suggestBlockResult,
        bool wasProcessed,
        bool validateSuggestedBlock,
        IBlockProcessingQueue? processingQueue = null,
        int timeoutMs = 50,
        Func<bool>? wasProcessedNow = null)
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
        blockTree.Head.Returns(head);
        blockTree.SyncPivot.Returns((0UL, Keccak.Zero));
        blockTree.FindHeader(block.ParentHash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(false);
        blockTree.GetInfo(parent.Number, parent.GetOrCalculateHash()).Returns((new BlockInfo(parent.Hash!, UInt256.Zero) { WasProcessed = true, BlockNumber = parent.Number }, null));
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
        effectiveProcessingQueue.Count.Returns(0);
        effectiveProcessingQueue.Enqueue(Arg.Any<Block>(), Arg.Any<ProcessingOptions>()).Returns(_ => ValueTask.CompletedTask);

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
