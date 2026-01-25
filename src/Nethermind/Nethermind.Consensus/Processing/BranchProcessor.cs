// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

public class BranchProcessor(
    IBlockProcessor blockProcessor,
    ISpecProvider specProvider,
    IWorldState stateProvider,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IBlockhashProvider blockhashProvider,
    ILogManager logManager,
    IBlockCachePreWarmer? preWarmer = null)
    : IBranchProcessor
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    protected readonly WorldStateMetricsDecorator _stateProvider = new WorldStateMetricsDecorator(stateProvider);
    private Task _clearTask = Task.CompletedTask;

    private const int MaxUncommittedBlocks = 64;
    private readonly Action<Task> _clearCaches = _ => preWarmer?.ClearCaches();

    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;

    public event EventHandler<BlockEventArgs>? BlockProcessing;

    private void PreCommitBlock(BlockHeader block)
    {
        if (_logger.IsTrace) _logger.Trace($"Committing the branch - {block.ToString(BlockHeader.Format.Short)} state root {block.StateRoot}");
        _stateProvider.CommitTree(block.Number);
    }

    public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions options, IBlockTracer blockTracer, CancellationToken token = default)
    {
        if (suggestedBlocks.Count == 0) return [];

        Block suggestedBlock = suggestedBlocks[0];

        IDisposable? worldStateCloser = null;
        if (stateProvider.IsInScope)
        {
            if (baseBlock is null && suggestedBlock.IsGenesis)
            {
                // Super special ultra mega – I don't want to deal with this right now – special case where genesis is handled
                // externally, where the state is added via `GenesisLoader` but not processed by the block processor
                // even though it still passes through the block tree suggest to blockchain processor event chain.
                // Meaning don't set state when handling genesis.
            }
            else
            {
                throw new InvalidOperationException($"State must not be handled from outside of {nameof(IBranchProcessor)} except for genesis block.");
            }
        }
        else
        {
            worldStateCloser = stateProvider.BeginScope(baseBlock);
        }

        CancellationTokenSource? backgroundCancellation = new();
        Task? preWarmTask = null;

        try
        {
            // Start prewarming as early as possible
            WaitForCacheClear();
            IReleaseSpec spec = specProvider.GetSpec(suggestedBlock.Header);

            if (Out.IsTargetBlock)
                Out.Log($"spec provider={specProvider.GetType().Name} spec={spec.GetType().Name} blockNumber={suggestedBlock.Header.Number} eip3860={spec.IsEip3860Enabled} eip4844={spec.IsEip4844Enabled}");

            preWarmTask = PreWarmTransactions(suggestedBlock, baseBlock!, spec, backgroundCancellation.Token);
            Task? prefetchBlockhash = blockhashProvider.Prefetch(suggestedBlock.Header, backgroundCancellation.Token);

            BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(suggestedBlocks));

            BlockHeader? preBlockBaseBlock = baseBlock;

            bool notReadOnly = !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
            int blocksCount = suggestedBlocks.Count;
            Block[] processedBlocks = new Block[blocksCount];

            for (int i = 0; i < blocksCount; i++)
            {
                WaitForCacheClear();
                suggestedBlock = suggestedBlocks[i];

                Out.Reset();
                Out.CurrentBlockNumber = suggestedBlock.Number;

                if (i > 0)
                {
                    // Refresh spec
                    spec = specProvider.GetSpec(suggestedBlock.Header);
                }
                // If prewarmCancellation is not null it means we are in first iteration of loop
                // and started prewarming at method entry, so don't start it again
                backgroundCancellation ??= new CancellationTokenSource();
                preWarmTask ??= PreWarmTransactions(suggestedBlock, preBlockBaseBlock, spec, backgroundCancellation.Token);
                prefetchBlockhash ??= blockhashProvider.Prefetch(suggestedBlock.Header, backgroundCancellation.Token);

                if (blocksCount > 64 && i % 8 == 0)
                {
                    if (_logger.IsInfo) _logger.Info($"Processing part of a long blocks branch {i}/{blocksCount}. Block: {suggestedBlock}");
                }

                if (notReadOnly)
                {
                    BlockProcessing?.Invoke(this, new BlockEventArgs(suggestedBlock));
                }

                Block processedBlock;
                TxReceipt[] receipts;

                if (preWarmTask is not null)
                {
                    (processedBlock, receipts) = blockProcessor.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
                }
                else
                {
                    // Even though we skip prewarming we still need to ensure the caches are cleared
                    CacheType result = preWarmer?.ClearCaches() ?? default;
                    if (result != default)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Low txs, caches {result} are not empty. Clearing them.");
                    }
                    (processedBlock, receipts) = blockProcessor.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
                }

                // Block is processed, we can cancel background tasks
                CancellationTokenExtensions.CancelDisposeAndClear(ref backgroundCancellation);

                processedBlocks[i] = processedBlock;

                // be cautious here as AuRa depends on processing
                PreCommitBlock(suggestedBlock.Header);
                QueueClearCaches(preWarmTask);

                if (notReadOnly)
                {
                    Metrics.StateMerkleizationTime = _stateProvider.StateMerkleizationTime;
                    BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(processedBlock, receipts));
                }

                // CommitBranch in parts if we have long running branch
                bool isFirstInBatch = i == 0;
                bool isLastInBatch = i == blocksCount - 1;
                bool isNotAtTheEdge = !isFirstInBatch && !isLastInBatch;
                bool isCommitPoint = i % MaxUncommittedBlocks == 0 && isNotAtTheEdge;
                if (isCommitPoint && notReadOnly)
                {
                    if (_logger.IsInfo) _logger.Info($"Commit part of a long blocks branch {i}/{blocksCount}");
                    BlockHeader previousBranchStateRoot = suggestedBlock.Header;

                    worldStateCloser?.Dispose();
                    worldStateCloser = stateProvider.BeginScope(previousBranchStateRoot);
                }

                preBlockBaseBlock = processedBlock.Header;
                // Make sure the prewarm task is finished before we reset the state
                WaitAndClear(ref preWarmTask);
                prefetchBlockhash = null;

                _stateProvider.Reset();

                // Calculate the transaction hashes in the background and release tx sequence memory
                // Hashes will be required for PersistentReceiptStorage in ForkchoiceUpdatedHandler
                // Though we still want to release the memory even if syncing rather than processing live
                TxHashCalculator.CalculateInBackground(suggestedBlock);
            }

            return processedBlocks;
        }
        catch (Exception ex) // try to restore at all cost
        {
            if (_logger.IsWarn) _logger.Warn($"Encountered exception {ex} while processing blocks.");
            CancellationTokenExtensions.CancelDisposeAndClear(ref backgroundCancellation);
            QueueClearCaches(preWarmTask);
            WaitAndClear(ref preWarmTask);
            throw;
        }
        finally
        {
            worldStateCloser?.Dispose();
        }

        static void WaitAndClear(ref Task? task)
        {
            task?.GetAwaiter().GetResult();
            task = null;
        }
    }

    private Task? PreWarmTransactions(Block suggestedBlock, BlockHeader preBlockBaseBlock, IReleaseSpec spec, CancellationToken token) =>
        suggestedBlock.Transactions.Length < 3
            ? null
            : preWarmer?.PreWarmCaches(suggestedBlock,
                preBlockBaseBlock,
                spec,
                token,
                beaconBlockRootHandler);

    private void WaitForCacheClear() => _clearTask.GetAwaiter().GetResult();

    private void QueueClearCaches(Task? preWarmTask)
    {
        if (preWarmTask is not null)
        {
            // Can start clearing caches in background
            _clearTask = preWarmTask.ContinueWith(_clearCaches, TaskContinuationOptions.RunContinuationsAsynchronously);
        }
        else if (preWarmer is not null)
        {
            _clearTask = Task.Run(preWarmer.ClearCaches);
        }
    }

    private class TxHashCalculator(Block suggestedBlock) : IThreadPoolWorkItem
    {
        public static void CalculateInBackground(Block suggestedBlock)
        {
            // Memory has been reserved on the transactions to delay calculate the hashes
            // We calculate the hashes in the background to release that memory
            ThreadPool.UnsafeQueueUserWorkItem(new TxHashCalculator(suggestedBlock), preferLocal: false);
        }

        void IThreadPoolWorkItem.Execute()
        {
            // Hashes will be required for PersistentReceiptStorage in UpdateMainChain ForkchoiceUpdatedHandler
            // Which occurs after the block has been processed; however the block is stored in cache and picked up
            // from there so we can calculate the hashes now for that later use.
            foreach (Transaction tx in suggestedBlock.Transactions)
            {
                // Calculate the hashes to release the memory from the transactionSequence
                tx.CalculateHashInternal();
            }
        }
    }
}
