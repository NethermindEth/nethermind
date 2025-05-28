// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IBlockhashStore blockHashStore,
    ILogManager logManager,
    IWithdrawalProcessor withdrawalProcessor,
    IExecutionRequestsProcessor executionRequestsProcessor,
    IBlockCachePreWarmer? preWarmer = null)
    : IBlockProcessor
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    protected readonly WorldStateMetricsDecorator _stateProvider = new WorldStateMetricsDecorator(stateProvider);
    private readonly IReceiptsRootCalculator _receiptsRootCalculator = ReceiptsRootCalculator.Instance;
    private Task _clearTask = Task.CompletedTask;

    private const int MaxUncommittedBlocks = 64;
    private readonly Action<Task> _clearCaches = _ => preWarmer?.ClearCaches();

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected BlockReceiptsTracer ReceiptsTracer { get; set; } = new();

    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;

    public event EventHandler<TxProcessedEventArgs> TransactionProcessed
    {
        add { blockTransactionsExecutor.TransactionProcessed += value; }
        remove { blockTransactionsExecutor.TransactionProcessed -= value; }
    }

    // TODO: move to branch processor
    public Block[] Process(Hash256 newBranchStateRoot, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions options, IBlockTracer blockTracer, CancellationToken token = default)
    {
        if (suggestedBlocks.Count == 0) return [];

        /* We need to save the snapshot state root before reorganization in case the new branch has invalid blocks.
           In case of invalid blocks on the new branch we will discard the entire branch and come back to
           the previous head state.*/
        Hash256 previousBranchStateRoot = CreateCheckpoint();
        InitBranch(newBranchStateRoot);

        Block suggestedBlock = suggestedBlocks[0];
        // Start prewarming as early as possible
        WaitForCacheClear();
        (CancellationTokenSource? prewarmCancellation, Task? preWarmTask)
            = PreWarmTransactions(suggestedBlock, newBranchStateRoot);

        BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(suggestedBlocks));

        Hash256 preBlockStateRoot = newBranchStateRoot;

        bool notReadOnly = !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
        int blocksCount = suggestedBlocks.Count;
        Block[] processedBlocks = new Block[blocksCount];
        try
        {
            for (int i = 0; i < blocksCount; i++)
            {
                WaitForCacheClear();
                suggestedBlock = suggestedBlocks[i];
                // If prewarmCancellation is not null it means we are in first iteration of loop
                // and started prewarming at method entry, so don't start it again
                if (prewarmCancellation is null)
                {
                    (prewarmCancellation, preWarmTask) = PreWarmTransactions(suggestedBlock, preBlockStateRoot);
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
                    (processedBlock, receipts) = ProcessOne(suggestedBlock, options, blockTracer, token);
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
                    (processedBlock, receipts) = ProcessOne(suggestedBlock, options, blockTracer, token);
                }

                processedBlocks[i] = processedBlock;

                // be cautious here as AuRa depends on processing
                PreCommitBlock(newBranchStateRoot, suggestedBlock.Number);
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
                    previousBranchStateRoot = CreateCheckpoint();
                    Hash256? newStateRoot = suggestedBlock.StateRoot;
                    InitBranch(newStateRoot, false);
                }

                preBlockStateRoot = processedBlock.StateRoot;
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

    private (CancellationTokenSource prewarmCancellation, Task preWarmTask) PreWarmTransactions(Block suggestedBlock, Hash256 preBlockStateRoot)
    {
        if (preWarmer is null || suggestedBlock.Transactions.Length < 3) return (null, null);

        CancellationTokenSource prewarmCancellation = new();
        Task preWarmTask = preWarmer.PreWarmCaches(suggestedBlock,
            preBlockStateRoot,
            specProvider.GetSpec(suggestedBlock.Header),
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

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;

    public event EventHandler<BlockEventArgs>? BlockProcessing;

    // TODO: move to branch processor
    private void InitBranch(Hash256 branchStateRoot, bool incrementReorgMetric = true)
    {
        /* Please note that we do not reset the state if branch state root is null.
           That said, I do not remember in what cases we receive null here.*/
        if (branchStateRoot is not null && _stateProvider.StateRoot != branchStateRoot)
        {
            /* Discarding the other branch data - chain reorganization.
               We cannot use cached values any more because they may have been written
               by blocks that are being reorganized out.*/

            if (incrementReorgMetric)
                Metrics.Reorganizations++;
            _stateProvider.Reset();
            _stateProvider.StateRoot = branchStateRoot;
        }
    }

    // TODO: move to branch processor
    private Hash256 CreateCheckpoint()
    {
        return _stateProvider.StateRoot;
    }

    // TODO: move to block processing pipeline
    private void PreCommitBlock(Hash256 newBranchStateRoot, long blockNumber)
    {
        if (_logger.IsTrace) _logger.Trace($"Committing the branch - {newBranchStateRoot}");
        _stateProvider.CommitTree(blockNumber);
    }

    // TODO: move to branch processor
    private void RestoreBranch(Hash256 branchingPointStateRoot)
    {
        if (_logger.IsTrace) _logger.Trace($"Restoring the branch checkpoint - {branchingPointStateRoot}");
        _stateProvider.Reset();
        _stateProvider.StateRoot = branchingPointStateRoot;
        if (_logger.IsTrace) _logger.Trace($"Restored the branch checkpoint - {branchingPointStateRoot} | {_stateProvider.StateRoot}");
    }

    // TODO: block processor pipeline
    private (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Processing block {suggestedBlock.ToString(Block.Format.Short)} ({options})");

        ApplyDaoTransition(suggestedBlock);
        Block block = PrepareBlockForProcessing(suggestedBlock);
        TxReceipt[] receipts = ProcessBlock(block, blockTracer, options, token);
        ValidateProcessedBlock(suggestedBlock, options, block, receipts);
        if (options.ContainsFlag(ProcessingOptions.StoreReceipts))
        {
            StoreTxReceipts(block, receipts);
        }

        return (block, receipts);
    }

    // TODO: block processor pipeline
    private void ValidateProcessedBlock(Block suggestedBlock, ProcessingOptions options, Block block, TxReceipt[] receipts)
    {
        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && !blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock, out string? error))
        {
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(suggestedBlock, "invalid block after processing"));
            throw new InvalidBlockException(suggestedBlock, error);
        }

        // Block is valid, copy the account changes as we use the suggested block not the processed one
        suggestedBlock.AccountChanges = block.AccountChanges;
        suggestedBlock.ExecutionRequests = block.ExecutionRequests;
    }

    private bool ShouldComputeStateRoot(BlockHeader header) =>
        !header.IsGenesis || !specProvider.GenesisStateUnavailable;

    // TODO: block processor pipeline
    protected virtual TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options,
        CancellationToken token)
    {
        BlockHeader header = block.Header;
        IReleaseSpec spec = specProvider.GetSpec(header);

        ReceiptsTracer.SetOtherTracer(blockTracer);
        ReceiptsTracer.StartNewBlockTrace(block);

        var blkCtx = new BlockExecutionContext(block.Header, spec);

        StoreBeaconRoot(block, in blkCtx, spec);
        blockHashStore.ApplyBlockhashStateChanges(header);
        _stateProvider.Commit(spec, commitRoots: false);

        TxReceipt[] receipts = blockTransactionsExecutor.ProcessTransactions(block, in blkCtx, options, ReceiptsTracer, spec, token);

        _stateProvider.Commit(spec, commitRoots: false);

        CalculateBlooms(receipts);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
        ApplyMinerRewards(block, blockTracer, spec);
        withdrawalProcessor.ProcessWithdrawals(block, spec);

        // We need to do a commit here as in _executionRequestsProcessor while executing system transactions
        // we do WorldState.Commit(SystemTransactionReleaseSpec.Instance). In SystemTransactionReleaseSpec
        // Eip158Enabled=false, so we end up persisting empty accounts created while processing withdrawals.
        _stateProvider.Commit(spec, commitRoots: false);

        executionRequestsProcessor.ProcessExecutionRequests(block, _stateProvider, in blkCtx, receipts, spec);

        ReceiptsTracer.EndBlockTrace();

        _stateProvider.Commit(spec, commitRoots: true);

        if (BlockchainProcessor.IsMainProcessingThread)
        {
            // Get the accounts that have been changed
            block.AccountChanges = _stateProvider.GetAccountChanges();
        }

        if (ShouldComputeStateRoot(header))
        {
            _stateProvider.RecalculateStateRoot();
            header.StateRoot = _stateProvider.StateRoot;
        }

        header.Hash = header.CalculateHash();

        return receipts;
    }

    /// <summary>
    /// Builds the block context ouf the block and the release spec.
    /// </summary>
    protected virtual BlockExecutionContext BuildBlockContext(Block block, IReleaseSpec spec) => new(block.Header, spec);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalculateBlooms(TxReceipt[] receipts)
    {
        ParallelUnbalancedWork.For(
            0,
            receipts.Length,
            ParallelUnbalancedWork.DefaultOptions,
            receipts,
            static (i, receipts) =>
            {
                receipts[i].CalculateBloom();
                return receipts;
            });
    }

    private void StoreBeaconRoot(Block block, in BlockExecutionContext blkCtx, IReleaseSpec spec)
    {
        try
        {
            beaconBlockRootHandler.StoreBeaconRoot(block, in blkCtx, spec, NullTxTracer.Instance);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Storing beacon block root for block {block.ToString(Block.Format.FullHashAndNumber)} failed: {e}");
        }
    }

    // TODO: block processor pipeline
    private void StoreTxReceipts(Block block, TxReceipt[] txReceipts)
    {
        // Setting canonical is done when the BlockAddedToMain event is fired
        receiptStorage.Insert(block, txReceipts, false);
    }

    // TODO: block processor pipeline
    private Block PrepareBlockForProcessing(Block suggestedBlock)
    {
        if (_logger.IsTrace) _logger.Trace($"{suggestedBlock.Header.ToString(BlockHeader.Format.Full)}");
        BlockHeader bh = suggestedBlock.Header;
        BlockHeader headerForProcessing = new(
            bh.ParentHash,
            bh.UnclesHash,
            bh.Beneficiary,
            bh.Difficulty,
            bh.Number,
            bh.GasLimit,
            bh.Timestamp,
            bh.ExtraData,
            bh.BlobGasUsed,
            bh.ExcessBlobGas)
        {
            Bloom = Bloom.Empty,
            Author = bh.Author,
            Hash = bh.Hash,
            MixHash = bh.MixHash,
            Nonce = bh.Nonce,
            TxRoot = bh.TxRoot,
            TotalDifficulty = bh.TotalDifficulty,
            AuRaStep = bh.AuRaStep,
            AuRaSignature = bh.AuRaSignature,
            ReceiptsRoot = bh.ReceiptsRoot,
            BaseFeePerGas = bh.BaseFeePerGas,
            WithdrawalsRoot = bh.WithdrawalsRoot,
            RequestsHash = bh.RequestsHash,
            IsPostMerge = bh.IsPostMerge,
            ParentBeaconBlockRoot = bh.ParentBeaconBlockRoot
        };

        if (!ShouldComputeStateRoot(bh))
        {
            headerForProcessing.StateRoot = bh.StateRoot;
        }

        return suggestedBlock.WithReplacedHeader(headerForProcessing);
    }

    // TODO: block processor pipeline
    private void ApplyMinerRewards(Block block, IBlockTracer tracer, IReleaseSpec spec)
    {
        if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
        BlockReward[] rewards = rewardCalculator.CalculateRewards(block);
        for (int i = 0; i < rewards.Length; i++)
        {
            BlockReward reward = rewards[i];

            using ITxTracer txTracer = tracer.IsTracingRewards
                ? // we need this tracer to be able to track any potential miner account creation
                tracer.StartNewTxTrace(null)
                : NullTxTracer.Instance;

            ApplyMinerReward(block, reward, spec);

            if (tracer.IsTracingRewards)
            {
                tracer.EndTxTrace();
                tracer.ReportReward(reward.Address, reward.RewardType.ToLowerString(), reward.Value);
                if (txTracer.IsTracingState)
                {
                    _stateProvider.Commit(spec, txTracer);
                }
            }
        }
    }

    // TODO: block processor pipeline (only where rewards needed)
    private void ApplyMinerReward(Block block, BlockReward reward, IReleaseSpec spec)
    {
        if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)reward.Value / (BigInteger)Unit.Ether:N3}{Unit.EthSymbol} for account at {reward.Address}");

        _stateProvider.AddToBalanceAndCreateIfNotExists(reward.Address, reward.Value, spec);
    }

    // TODO: block processor pipeline
    private void ApplyDaoTransition(Block block)
    {
        long? daoBlockNumber = specProvider.DaoBlockNumber;
        if (daoBlockNumber.HasValue && daoBlockNumber.Value == block.Header.Number)
        {
            ApplyTransition();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ApplyTransition()
        {
            if (_logger.IsInfo) _logger.Info("Applying the DAO transition");
            Address withdrawAccount = DaoData.DaoWithdrawalAccount;
            if (!_stateProvider.AccountExists(withdrawAccount))
            {
                _stateProvider.CreateAccount(withdrawAccount, 0);
            }

            foreach (Address daoAccount in DaoData.DaoAccounts)
            {
                UInt256 balance = _stateProvider.GetBalance(daoAccount);
                _stateProvider.AddToBalance(withdrawAccount, balance, Dao.Instance);
                _stateProvider.SubtractFromBalance(daoAccount, balance, Dao.Instance);
            }
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
