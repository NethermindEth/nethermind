// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Requests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor(
    ISpecProvider? specProvider,
    IBlockValidator? blockValidator,
    IRewardCalculator? rewardCalculator,
    IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
    IWorldState? stateProvider,
    IReceiptStorage? receiptStorage,
    ITransactionProcessor transactionProcessor,
    IBeaconBlockRootHandler? beaconBlockRootHandler,
    IBlockhashStore? blockHashStore,
    ILogManager? logManager,
    IWithdrawalProcessor? withdrawalProcessor = null,
    IReceiptsRootCalculator? receiptsRootCalculator = null,
    IBlockCachePreWarmer? preWarmer = null,
    IConsensusRequestsProcessor? consensusRequestsProcessor = null)
    : IBlockProcessor
{
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    protected readonly IWorldState _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
    private readonly IReceiptStorage _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
    private readonly IReceiptsRootCalculator _receiptsRootCalculator = receiptsRootCalculator ?? ReceiptsRootCalculator.Instance;
    private readonly IWithdrawalProcessor _withdrawalProcessor = withdrawalProcessor ?? new WithdrawalProcessor(stateProvider, logManager);
    private readonly IBeaconBlockRootHandler _beaconBlockRootHandler = beaconBlockRootHandler ?? throw new ArgumentNullException(nameof(beaconBlockRootHandler));
    private readonly IBlockValidator _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
    private readonly IRewardCalculator _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
    private readonly IBlockProcessor.IBlockTransactionsExecutor _blockTransactionsExecutor = blockTransactionsExecutor ?? throw new ArgumentNullException(nameof(blockTransactionsExecutor));
    private readonly IBlockhashStore _blockhashStore = blockHashStore ?? throw new ArgumentNullException(nameof(blockHashStore));

    private readonly IConsensusRequestsProcessor _consensusRequestsProcessor = consensusRequestsProcessor ?? new ConsensusRequestsProcessor(transactionProcessor);
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
        add { _blockTransactionsExecutor.TransactionProcessed += value; }
        remove { _blockTransactionsExecutor.TransactionProcessed -= value; }
    }

    // TODO: move to branch processor
    public Block[] Process(Hash256 newBranchStateRoot, List<Block> suggestedBlocks, ProcessingOptions options, IBlockTracer blockTracer)
    {
        if (suggestedBlocks.Count == 0) return Array.Empty<Block>();

        TxHashCalculator.CalculateInBackground(suggestedBlocks);
        BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(suggestedBlocks));

        /* We need to save the snapshot state root before reorganization in case the new branch has invalid blocks.
           In case of invalid blocks on the new branch we will discard the entire branch and come back to
           the previous head state.*/
        Hash256 previousBranchStateRoot = CreateCheckpoint();
        InitBranch(newBranchStateRoot);
        Hash256 preBlockStateRoot = newBranchStateRoot;

        bool notReadOnly = !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
        int blocksCount = suggestedBlocks.Count;
        Block[] processedBlocks = new Block[blocksCount];

        Task? preWarmTask = null;
        try
        {
            for (int i = 0; i < blocksCount; i++)
            {
                preWarmTask = null;
                WaitForCacheClear();
                Block suggestedBlock = suggestedBlocks[i];
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

                bool skipPrewarming = preWarmer is null || suggestedBlock.Transactions.Length < 3;
                if (!skipPrewarming)
                {
                    using CancellationTokenSource cancellationTokenSource = new();
                    (_, AccessList? accessList) = _beaconBlockRootHandler.BeaconRootsAccessList(suggestedBlock, _specProvider.GetSpec(suggestedBlock.Header));
                    preWarmTask = preWarmer.PreWarmCaches(suggestedBlock, preBlockStateRoot, accessList, cancellationTokenSource.Token);
                    (processedBlock, receipts) = ProcessOne(suggestedBlock, options, blockTracer);
                    // Block is processed, we can cancel the prewarm task
                    cancellationTokenSource.Cancel();
                }
                else
                {
                    if (preWarmer?.ClearCaches() ?? false)
                    {
                        if (_logger.IsWarn) _logger.Warn("Low txs, caches are not empty. Clearing them.");
                    }
                    // Even though we skip prewarming we still need to ensure the caches are cleared
                    (processedBlock, receipts) = ProcessOne(suggestedBlock, options, blockTracer);
                }

                processedBlocks[i] = processedBlock;

                // be cautious here as AuRa depends on processing
                PreCommitBlock(newBranchStateRoot, suggestedBlock.Number);
                QueueClearCaches(preWarmer, preWarmTask);

                if (notReadOnly)
                {
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
                _stateProvider.Reset(resizeCollections: true);
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
            QueueClearCaches(preWarmer, preWarmTask);
            preWarmTask?.GetAwaiter().GetResult();
            RestoreBranch(previousBranchStateRoot);
            throw;
        }
    }

    private void WaitForCacheClear() => _clearTask.GetAwaiter().GetResult();

    private void QueueClearCaches(IBlockCachePreWarmer preWarmer, Task? preWarmTask)
    {
        if (preWarmTask is not null)
        {
            // Can start clearing caches in background
            _clearTask = preWarmTask.ContinueWith(_clearCaches, TaskContinuationOptions.RunContinuationsAsynchronously);
        }
        else if (preWarmer is not null)
        {
            _clearTask = Task.Run(() => preWarmer.ClearCaches());
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
    private (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer)
    {
        if (_logger.IsTrace) _logger.Trace($"Processing block {suggestedBlock.ToString(Block.Format.Short)} ({options})");

        ApplyDaoTransition(suggestedBlock);
        Block block = PrepareBlockForProcessing(suggestedBlock);
        TxReceipt[] receipts = ProcessBlock(block, blockTracer, options);
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
        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && !_blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock, out string? error))
        {
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(suggestedBlock, "invalid block after processing"));
            throw new InvalidBlockException(suggestedBlock, error);
        }

        // Block is valid, copy the account changes as we use the suggested block not the processed one
        suggestedBlock.AccountChanges = block.AccountChanges;
    }

    private bool ShouldComputeStateRoot(BlockHeader header) =>
        !header.IsGenesis || !_specProvider.GenesisStateUnavailable;

    // TODO: block processor pipeline
    protected virtual TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options)
    {
        BlockHeader header = block.Header;
        IReleaseSpec spec = _specProvider.GetSpec(header);

        ReceiptsTracer.SetOtherTracer(blockTracer);
        ReceiptsTracer.StartNewBlockTrace(block);

        StoreBeaconRoot(block, spec);
        _blockhashStore.ApplyBlockhashStateChanges(header);
        _stateProvider.Commit(spec, commitStorageRoots: false);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, spec);
        CalculateBlooms(receipts);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
        ApplyMinerRewards(block, blockTracer, spec);
        _withdrawalProcessor.ProcessWithdrawals(block, spec);
        _consensusRequestsProcessor.ProcessRequests(block, _stateProvider, receipts, spec);

        ReceiptsTracer.EndBlockTrace();

        _stateProvider.Commit(spec, commitStorageRoots: true);

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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalculateBlooms(TxReceipt[] receipts)
    {
        int index = 0;
        Parallel.For(0, receipts.Length, _ =>
        {
            int i = Interlocked.Increment(ref index) - 1;
            receipts[i].CalculateBloom();
        });
    }

    private void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        try
        {
            _beaconBlockRootHandler.StoreBeaconRoot(block, spec);
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
        _receiptStorage.Insert(block, txReceipts, false);
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
            RequestsRoot = bh.RequestsRoot,
            IsPostMerge = bh.IsPostMerge,
            ParentBeaconBlockRoot = bh.ParentBeaconBlockRoot
        };

        if (!ShouldComputeStateRoot(bh))
        {
            headerForProcessing.StateRoot = bh.StateRoot;
        }

        return suggestedBlock.CreateCopy(headerForProcessing);
    }

    // TODO: block processor pipeline
    private void ApplyMinerRewards(Block block, IBlockTracer tracer, IReleaseSpec spec)
    {
        if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
        BlockReward[] rewards = _rewardCalculator.CalculateRewards(block);
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

        if (!_stateProvider.AccountExists(reward.Address))
        {
            _stateProvider.CreateAccount(reward.Address, reward.Value);
        }
        else
        {
            _stateProvider.AddToBalance(reward.Address, reward.Value, spec);
        }
    }

    // TODO: block processor pipeline
    private void ApplyDaoTransition(Block block)
    {
        long? daoBlockNumber = _specProvider.DaoBlockNumber;
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

    private class TxHashCalculator(List<Block> suggestedBlocks) : IThreadPoolWorkItem
    {
        public static void CalculateInBackground(List<Block> suggestedBlocks)
        {
            // Memory has been reserved on the transactions to delay calculate the hashes
            // We calculate the hashes in the background to release that memory
            ThreadPool.UnsafeQueueUserWorkItem(new TxHashCalculator(suggestedBlocks), preferLocal: false);
        }

        void IThreadPoolWorkItem.Execute()
        {
            // Hashes will be required for PersistentReceiptStorage in UpdateMainChain ForkchoiceUpdatedHandler
            // Which occurs after the block has been processed; however the block is stored in cache and picked up
            // from there so we can calculate the hashes now for that later use.
            foreach (Block block in CollectionsMarshal.AsSpan(suggestedBlocks))
            {
                foreach (Transaction tx in block.Transactions)
                {
                    // Calculate the hashes to release the memory from the transactionSequence
                    tx.CalculateHashInternal();
                }
            }
        }
    }
}
