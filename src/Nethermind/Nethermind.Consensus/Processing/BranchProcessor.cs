// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

public class BranchProcessor(
    IBlockProcessor blockProcessor,
    ISpecProvider specProvider,
    IWorldState stateProvider,
    IBeaconBlockRootHandler beaconBlockRootHandler,
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

    public event EventHandler<TxProcessedEventArgs> TransactionProcessed
    {
        add => blockProcessor.TransactionProcessed += value;
        remove => blockProcessor.TransactionProcessed -= value;
    }

    private void InitBranch(BlockHeader? baseBlock, bool incrementReorgMetric = true)
    {
        /* Please note that we do not reset the state if branch state root is null.
           That said, I do not remember in what cases we receive null here.*/
        if (baseBlock is not null && _stateProvider.StateRoot != baseBlock.StateRoot)
        {
            /* Discarding the other branch data - chain reorganization.
               We cannot use cached values any more because they may have been written
               by blocks that are being reorganized out.*/

            if (incrementReorgMetric)
                Metrics.Reorganizations++;
            _stateProvider.Reset();
            _stateProvider.SetBaseBlock(baseBlock);
        }
    }

    private void PreCommitBlock(BlockHeader block)
    {
        if (_logger.IsTrace) _logger.Trace($"Committing the branch - {block.ToString(BlockHeader.Format.Short)} state root {block.StateRoot}");
        _stateProvider.CommitTree(block.Number);
    }

    private void RestoreBranch(BlockHeader? branchingPointHeader)
    {
        if (_logger.IsTrace) _logger.Trace($"Restoring the branch checkpoint - {branchingPointHeader?.ToString(BlockHeader.Format.Short)}");
        _stateProvider.Reset();
        _stateProvider.SetBaseBlock(branchingPointHeader);
        if (_logger.IsTrace) _logger.Trace($"Restored the branch checkpoint - {branchingPointHeader?.ToString(BlockHeader.Format.Short)} | {_stateProvider.StateRoot}");
    }

    public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions options, IBlockTracer blockTracer, CancellationToken token = default, string? forkName = null)
    {
        if (suggestedBlocks.Count == 0) return [];

        BlockHeader? previousBranchStateRoot = baseBlock;
        InitBranch(baseBlock);

        Block suggestedBlock = suggestedBlocks[0];
        // Start prewarming as early as possible
        WaitForCacheClear();

        IReleaseSpec spec;

        if (forkName is not null)
        {
            options |= ProcessingOptions.TracingMode;
            _logger.Warn("Using Tracing Mode");

            if (forkName.Equals("fusaka"))
            {
                spec = Osaka.Instance;
                _logger.Warn($"Using Osaka spec");
            }
            else
            {
                spec = specProvider.GetSpec(suggestedBlock.Header);
            }
        }
        else
        {
            spec = specProvider.GetSpec(suggestedBlock.Header);
        }

        (CancellationTokenSource? prewarmCancellation, Task? preWarmTask)
            = PreWarmTransactions(suggestedBlock, baseBlock, spec);

        BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(suggestedBlocks));

        BlockHeader? preBlockBaseBlock = baseBlock;

        bool notReadOnly = !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
        int blocksCount = suggestedBlocks.Count;
        Block[] processedBlocks = new Block[blocksCount];
        try
        {
            for (int i = 0; i < blocksCount; i++)
            {
                WaitForCacheClear();
                suggestedBlock = suggestedBlocks[i];
                if (i > 0)
                {
                    // Refresh spec
                    if (forkName is not null && forkName.Equals("fusaka"))
                    {
                        spec = Osaka.Instance;
                        _logger.Warn($"Using Osaka spec for block {suggestedBlock.Number} hash: {suggestedBlock.Hash}");
                    }
                    else
                    {
                        spec = specProvider.GetSpec(suggestedBlock.Header);
                    }
                }
                // If prewarmCancellation is not null it means we are in first iteration of loop
                // and started prewarming at method entry, so don't start it again
                if (prewarmCancellation is null)
                {
                    (prewarmCancellation, preWarmTask) = PreWarmTransactions(suggestedBlock, preBlockBaseBlock, spec);
                }

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

                if (prewarmCancellation is not null)
                {
                    (processedBlock, receipts) = blockProcessor.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
                    // Block is processed, we can cancel the prewarm task
                    CancellationTokenExtensions.CancelDisposeAndClear(ref prewarmCancellation);
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
                    previousBranchStateRoot = suggestedBlock.Header;
                    InitBranch(suggestedBlock.Header, false);
                }

                preBlockBaseBlock = processedBlock.Header;
                // Make sure the prewarm task is finished before we reset the state
                preWarmTask?.GetAwaiter().GetResult();
                preWarmTask = null;
                _stateProvider.Reset();

                // Calculate the transaction hashes in the background and release tx sequence memory
                // Hashes will be required for PersistentReceiptStorage in ForkchoiceUpdatedHandler
                // Though we still want to release the memory even if syncing rather than processing live
                TxHashCalculator.CalculateInBackground(suggestedBlock);
            }

            if (options.ContainsFlag(ProcessingOptions.DoNotUpdateHead))
            {
                RestoreBranch(previousBranchStateRoot);
            }

            return processedBlocks;
        }
        catch (Exception ex) // try to restore at all cost
        {
            if (_logger.IsWarn) _logger.Warn($"Encountered exception {ex} while processing blocks.");
            CancellationTokenExtensions.CancelDisposeAndClear(ref prewarmCancellation);
            QueueClearCaches(preWarmTask);
            preWarmTask?.GetAwaiter().GetResult();
            RestoreBranch(previousBranchStateRoot);
            throw;
        }
    }

    private (CancellationTokenSource prewarmCancellation, Task preWarmTask) PreWarmTransactions(Block suggestedBlock, BlockHeader preBlockBaseBlock, IReleaseSpec spec)
    {
        if (preWarmer is null || suggestedBlock.Transactions.Length < 3) return (null, null);

        CancellationTokenSource prewarmCancellation = new();
        Task preWarmTask = preWarmer.PreWarmCaches(suggestedBlock,
            preBlockBaseBlock,
            spec,
            prewarmCancellation.Token,
            beaconBlockRootHandler);

        return (prewarmCancellation, preWarmTask);
    }

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
