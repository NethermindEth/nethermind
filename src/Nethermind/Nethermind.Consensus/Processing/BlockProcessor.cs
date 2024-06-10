// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.BeaconBlockRoot;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor : IBlockProcessor
{
    private readonly ILogger _logger;
    private readonly ISpecProvider _specProvider;
    protected readonly IWorldState _stateProvider;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IReceiptsRootCalculator _receiptsRootCalculator;
    private readonly IWithdrawalProcessor _withdrawalProcessor;
    private readonly IBeaconBlockRootHandler _beaconBlockRootHandler;
    private readonly IBlockValidator _blockValidator;
    private readonly IRewardCalculator _rewardCalculator;
    private readonly IBlockProcessor.IBlockTransactionsExecutor _blockTransactionsExecutor;
    private readonly IBlockhashStore _blockhashStore;
    private readonly IBlockCachePreWarmer? _preWarmer;
    private const int MaxUncommittedBlocks = 64;

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected BlockReceiptsTracer ReceiptsTracer { get; set; }

    public BlockProcessor(
        ISpecProvider? specProvider,
        IBlockValidator? blockValidator,
        IRewardCalculator? rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
        IWorldState? stateProvider,
        IReceiptStorage? receiptStorage,
        IBlockhashStore? blockHashStore,
        ILogManager? logManager,
        IWithdrawalProcessor? withdrawalProcessor = null,
        IReceiptsRootCalculator? receiptsRootCalculator = null,
        IBlockCachePreWarmer? preWarmer = null)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _withdrawalProcessor = withdrawalProcessor ?? new WithdrawalProcessor(stateProvider, logManager);
        _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
        _blockTransactionsExecutor = blockTransactionsExecutor ?? throw new ArgumentNullException(nameof(blockTransactionsExecutor));
        _receiptsRootCalculator = receiptsRootCalculator ?? ReceiptsRootCalculator.Instance;
        _blockhashStore = blockHashStore ?? throw new ArgumentNullException(nameof(blockHashStore));
        _preWarmer = preWarmer;
        _beaconBlockRootHandler = new BeaconBlockRootHandler();
        ReceiptsTracer = new BlockReceiptsTracer();
    }

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
        try
        {
            for (int i = 0; i < blocksCount; i++)
            {
                Block suggestedBlock = suggestedBlocks[i];
                if (blocksCount > 64 && i % 8 == 0)
                {
                    if (_logger.IsInfo) _logger.Info($"Processing part of a long blocks branch {i}/{blocksCount}. Block: {suggestedBlock}");
                }

                using CancellationTokenSource cancellationTokenSource = new();
                Task? preWarmTask = suggestedBlock.Transactions.Length < 3
                    ? null
                    : _preWarmer?.PreWarmCaches(suggestedBlock, preBlockStateRoot!, cancellationTokenSource.Token);
                (Block processedBlock, TxReceipt[] receipts) = ProcessOne(suggestedBlock, options, blockTracer);
                cancellationTokenSource.Cancel();
                preWarmTask?.GetAwaiter().GetResult();
                processedBlocks[i] = processedBlock;

                // be cautious here as AuRa depends on processing
                PreCommitBlock(newBranchStateRoot, suggestedBlock.Number);
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
            _logger.Trace($"Encountered exception {ex} while processing blocks.");
            RestoreBranch(previousBranchStateRoot);
            throw;
        }
        finally
        {
            _preWarmer?.ClearCaches();
        }
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;

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
            if (_logger.IsWarn) _logger.Warn($"Processed block is not valid {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} - {error}");
            if (_logger.IsWarn) _logger.Warn($"Suggested block TD: {suggestedBlock.TotalDifficulty}, Suggested block IsPostMerge {suggestedBlock.IsPostMerge}, Block TD: {block.TotalDifficulty}, Block IsPostMerge {block.IsPostMerge}");
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
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        ReceiptsTracer.SetOtherTracer(blockTracer);
        ReceiptsTracer.StartNewBlockTrace(block);

        _beaconBlockRootHandler.ApplyContractStateChanges(block, spec, _stateProvider);
        _blockhashStore.ApplyHistoryBlockHashes(block.Header);

        _stateProvider.Commit(spec, commitStorageRoots: false);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, spec);

        if (spec.IsEip4844Enabled)
        {
            block.Header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        block.Header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
        ApplyMinerRewards(block, blockTracer, spec);
        _withdrawalProcessor.ProcessWithdrawals(block, spec);
        ReceiptsTracer.EndBlockTrace();

        _stateProvider.Commit(spec, commitStorageRoots: true);

        if (BlockchainProcessor.IsMainProcessingThread)
        {
            // Get the accounts that have been changed
            block.AccountChanges = _stateProvider.GetAccountChanges();
        }

        if (ShouldComputeStateRoot(block.Header))
        {
            _stateProvider.RecalculateStateRoot();
            block.Header.StateRoot = _stateProvider.StateRoot;
        }

        block.Header.Hash = block.Header.CalculateHash();

        return receipts;
    }

    // TODO: block processor pipeline
    private void StoreTxReceipts(Block block, TxReceipt[] txReceipts)
    {
        // Setting canonical is done when the BlockAddedToMain event is firec
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
            IsPostMerge = bh.IsPostMerge,
            ParentBeaconBlockRoot = bh.ParentBeaconBlockRoot,
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
        if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == block.Header.Number)
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
