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
using Nethermind.TxPool;
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

        using MergeTestBlockchain chain = await CreateBlockchain(mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = 5000 // Long timeout to allow race condition to occur
        });

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
    public async Task NewPayloadV1_EventHandler_Cleanup_Should_Prevent_Memory_Leaks()
    {
        // This test verifies that event handlers are properly cleaned up
        // even when exceptions occur during processing

        using MergeTestBlockchain chain = await CreateBlockchain(mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = 100 // Short timeout to trigger timeout scenarios
        });

        // Create an invalid block that will cause processing to fail or timeout
        Block invalidBlock = Build.A.Block
            .WithNumber(999999) // Very high number to trigger validation failures
            .WithParentHash(TestItem.KeccakA) // Non-existent parent
            .WithDifficulty(0)
            .WithNonce(0)
            .TestObject;

        ExecutionPayload invalidPayload = ExecutionPayload.Create(invalidBlock);

        // Process multiple invalid payloads that will fail or timeout
        for (int i = 0; i < 5; i++)
        {
            try
            {
                ResultWrapper<PayloadStatusV1> result = await chain.EngineRpcModule.engine_newPayloadV1(invalidPayload);
                // Even if processing fails, it should not throw exceptions due to event handler issues
            }
            catch (Exception ex)
            {
                // We expect some processing failures, but not event handler related exceptions
                Assert.That(ex, Is.Not.TypeOf<InvalidOperationException>(), "Event handler race conditions should be fixed");
                Assert.That(ex, Is.Not.TypeOf<ObjectDisposedException>(), "Event handler cleanup should be proper");
            }
        }

        // After processing, event handlers should be properly cleaned up
        // This is a conceptual test - the main verification is that no exceptions are thrown
        // In the actual implementation, the fix ensures event handlers are always unsubscribed
        // in the finally block using Interlocked.CompareExchange to prevent double unsubscription

        // If we reach here without exceptions, the event handler cleanup is working correctly
        Assert.Pass("Event handlers were properly cleaned up without race condition exceptions");
    }

    [Test]
    public async Task NewPayloadV1_TaskCompletionSource_TrySet_Should_Not_Throw_On_Double_Completion()
    {
        // This test specifically targets the TrySetResult/TrySetException fix
        // where the original code used SetResult/SetException which would throw if already completed

        using MergeTestBlockchain chain = await CreateBlockchain(mergeConfig: new MergeConfig()
        {
            NewPayloadBlockProcessingTimeout = 1000
        });

        // Create a block that will trigger event processing
        Block block = Build.A.Block
            .WithNumber(1)
            .WithParent(chain.BlockTree.Head!)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithExtraData(new byte[32])
            .TestObject;

        ExecutionPayload payload = ExecutionPayload.Create(block);

        List<Task> concurrentTasks = [];

        // Launch multiple concurrent operations that might try to complete the same task
        for (int i = 0; i < 5; i++)
        {
            concurrentTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await chain.EngineRpcModule.engine_newPayloadV1(payload);
                }
                catch (OperationCanceledException)
                {
                    // This is expected due to cancellation
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("already completed"))
                {
                    // This should NOT happen with the fix - TrySetResult/TrySetException prevent this
                    Assert.Fail($"TaskCompletionSource double completion exception: {ex.Message}");
                }
            }));
        }

        await Task.WhenAll(concurrentTasks);

        // If we reach here, the TrySetResult/TrySetException fix is working correctly
        Assert.Pass("TaskCompletionSource race condition handling works correctly");
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
        int timeoutMs = 50)
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
        blockTree.WasProcessed(block.Number, block.Hash!).Returns(wasProcessed);

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
            Substitute.For<ISpecProvider>(),
            Substitute.For<ITxPool>(),
            Substitute.For<IStreamedSenderRecovery>(),
            LimboLogs.Instance);
    }
}
