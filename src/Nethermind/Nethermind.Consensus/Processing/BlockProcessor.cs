// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
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
    IWorldStateProvider? worldStateProvider,
    IReceiptStorage? receiptStorage,
    IBlockhashStore? blockHashStore,
    IBeaconBlockRootHandler? beaconBlockRootHandler,
    ILogManager? logManager,
    IWithdrawalProcessor? withdrawalProcessor = null,
    IReceiptsRootCalculator? receiptsRootCalculator = null,
    IBlockCachePreWarmer? preWarmer = null)
    : IBlockProcessor
{
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    protected readonly IWorldStateProvider _worldStateProvider = worldStateProvider ?? throw new ArgumentNullException(nameof(worldStateProvider));
    private readonly IReceiptStorage _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
    private readonly IReceiptsRootCalculator _receiptsRootCalculator = receiptsRootCalculator ?? ReceiptsRootCalculator.Instance;
    private readonly IWithdrawalProcessor _withdrawalProcessor = withdrawalProcessor ?? new WithdrawalProcessor(logManager);
    private readonly IBeaconBlockRootHandler _beaconBlockRootHandler = beaconBlockRootHandler ?? throw new ArgumentNullException(nameof(beaconBlockRootHandler));
    private readonly IBlockValidator _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
    private readonly IRewardCalculator _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
    private readonly IBlockProcessor.IBlockTransactionsExecutor _blockTransactionsExecutor = blockTransactionsExecutor ?? throw new ArgumentNullException(nameof(blockTransactionsExecutor));
    private readonly IBlockhashStore _blockhashStore = blockHashStore ?? throw new ArgumentNullException(nameof(blockHashStore));
    private const int MaxUncommittedBlocks = 64;
    private readonly Func<Task, Task> _clearCaches = _ => preWarmer.ClearCachesInBackground();

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
        Hash256 previousBranchStateRoot = _worldStateProvider.GetWorldState().StateRoot; // we will store previousBranchStateRoot for both verkle and merkle trees
        InitBranch(newBranchStateRoot, suggestedBlocks[0].Header);
        Hash256 preBlockStateRoot = newBranchStateRoot;

        bool notReadOnly = !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
        int blocksCount = suggestedBlocks.Count;
        var processedBlocks = new Block[blocksCount];

        try
        {
            for (int i = 0; i < blocksCount; i++)
            {
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

                IWorldState? worldStateToUse = _worldStateProvider.GetGlobalWorldState(suggestedBlock.Header);
                _logger.Info($"Found the worldState to use: {worldStateToUse.StateRoot}");
                Task? preWarmTask = null;
                bool skipPrewarming = preWarmer is null || suggestedBlock.Transactions.Length < 3;
                if (!skipPrewarming)
                {
                    using CancellationTokenSource cancellationTokenSource = new();
                    (_, AccessList? accessList) = _beaconBlockRootHandler.BeaconRootsAccessList(suggestedBlock, _specProvider.GetSpec(suggestedBlock.Header));
                    preWarmTask = preWarmer.PreWarmCaches(suggestedBlock, preBlockStateRoot, accessList, cancellationTokenSource.Token);

                    (processedBlock, receipts) = ProcessOne(suggestedBlock, options, blockTracer);
                    // Block is processed, we can cancel the prewarm task
                    preWarmTask = preWarmTask.ContinueWith(_clearCaches).Unwrap();
                    cancellationTokenSource.Cancel();
                }
                else
                {
                    (processedBlock, receipts) = ProcessOne(suggestedBlock, options, blockTracer);
                }

                processedBlocks[i] = processedBlock;

                // be cautious here as AuRa depends on processing
                PreCommitBlock(newBranchStateRoot, suggestedBlock.Number, worldStateToUse);
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
                    previousBranchStateRoot = CreateCheckpoint(worldStateToUse);
                    Hash256? newStateRoot = suggestedBlock.StateRoot;
                    InitBranch(newStateRoot, suggestedBlock.Header, false);
                }

                preBlockStateRoot = processedBlock.StateRoot;
                // Make sure the prewarm task is finished before we reset the state
                preWarmTask?.GetAwaiter().GetResult();
                worldStateToUse.Reset(resizeCollections: true);
            }

            if (options.ContainsFlag(ProcessingOptions.DoNotUpdateHead))
            {
                RestoreBranch(previousBranchStateRoot, _worldStateProvider.GetWorldState()); // we will restore for both merkle and verkle
            }

            return processedBlocks;
        }
        catch (Exception ex) // try to restore at all cost
        {
            _logger.Trace($"Encountered exception {ex} while processing blocks.");
            RestoreBranch(previousBranchStateRoot, _worldStateProvider.GetWorldState());
            throw;
        }
        finally
        {
            preWarmer?.ClearCaches();
        }
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;

    public event EventHandler<BlockEventArgs>? BlockProcessing;

    // TODO: revist this implementation. First, what is mean by already existing TODO to move this to branch processor
    // Second, can we move this inside the WorldStateManager? we can keep track of checkpoints as well in the world state
    // manager and then easily restore those checkpoints as well
    // Third, we can just break the processing branches into two, where we process the merkle branch separately
    // and then process the verkle branch separately
    // TODO: move to branch processor
    private void InitBranch(Hash256? branchStateRoot, BlockHeader blockHeader, bool incrementReorgMetric = true)
    {
        /* Please note that we do not reset the state if branch state root is null.
         That said, I do not remember in what cases we receive null here.*/
        if (branchStateRoot is null) return;

        // here even if this is the transition boundary, we still use the correct one on the basis of which stateProvider
        // should be used to process this block, so we reset the state for that. But this creates an interesting
        // scenario where if we are processing a branch M0 -> M1 -> M2 -> V0 -> V1 -> V3, and now if we reorg to
        // M0 -> M1B -> M2B -> V0B -> V1B -> V2B, this means we need to clear the verkle state as well.
        // so here we will ensure that if the block we are processing is the transition block,
        // we clean the new WorldState - this will be managed by the WorldStateManager
        IWorldState? worldStateToUse = _worldStateProvider.GetGlobalWorldState(blockHeader);
        if (worldStateToUse.StateRoot != branchStateRoot)
        {
            /* Discarding the other branch data - chain reorganization.
               We cannot use cached values any more because they may have been written
               by blocks that are being reorganized out.*/

            if (incrementReorgMetric)
                Metrics.Reorganizations++;
            worldStateToUse.Reset();
            worldStateToUse.StateRoot = branchStateRoot;
        }
    }

    // TODO: move to branch processor
    // now this looks like a stupid function that does not make sense
    private Hash256 CreateCheckpoint(IWorldState worldState)
    {
        return worldState.StateRoot;
    }

    // TODO: move to block processing pipeline
    private void PreCommitBlock(Hash256 newBranchStateRoot, long blockNumber, IWorldState worldState)
    {
        if (_logger.IsTrace) _logger.Trace($"Committing the branch - {newBranchStateRoot}");
        worldState.CommitTree(blockNumber);
    }

    // TODO: how do we handle this in this new scenerio, because we dont know which type of stateProvider does this stateRoot
    // belong to. Should we keep track of that as well.
    // TODO: move to branch processor
    private void RestoreBranch(Hash256 branchingPointStateRoot, IWorldState worldStateToRestore)
    {
        if (_logger.IsTrace) _logger.Trace($"Restoring the branch checkpoint - {branchingPointStateRoot}");
        worldStateToRestore.Reset();
        worldStateToRestore.StateRoot = branchingPointStateRoot;
        if (_logger.IsTrace) _logger.Trace($"Restored the branch checkpoint - {branchingPointStateRoot} | {worldStateToRestore.StateRoot}");
    }

    // TODO: block processor pipeline
    private (Block Block, TxReceipt[] Receipts) ProcessOne(IWorldState worldState, Block suggestedBlock,
        ProcessingOptions options, IBlockTracer blockTracer)
    {
        if (_logger.IsTrace) _logger.Trace($"Processing block {suggestedBlock.ToString(Block.Format.Short)} ({options})");

        ApplyDaoTransition(suggestedBlock, worldState);
        Block block = PrepareBlockForProcessing(suggestedBlock);
        TxReceipt[] receipts = ProcessBlock(worldState, block, blockTracer, options);
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
            if (_logger.IsWarn) _logger.Warn($"Suggested block TD: {suggestedBlock.TotalDifficulty}, Suggested block IsPostMerge {suggestedBlock.IsPostMerge}, Block TD: {block.TotalDifficulty}, Block IsPostMerge {block.IsPostMerge}");
            throw new InvalidBlockException(suggestedBlock, error);
        }

        // Block is valid, copy the account changes as we use the suggested block not the processed one
        suggestedBlock.AccountChanges = block.AccountChanges;
    }

    private bool ShouldComputeStateRoot(BlockHeader header) =>
        !header.IsGenesis || !_specProvider.GenesisStateUnavailable;

    // TODO: block processor pipeline
    protected virtual TxReceipt[] ProcessBlock(IWorldState worldState, Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options)
    {
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        ReceiptsTracer.SetOtherTracer(blockTracer);
        ReceiptsTracer.StartNewBlockTrace(block);

        StoreBeaconRoot(block, spec, worldState);
        _blockhashStore.ApplyBlockhashStateChanges(block.Header, worldState);

        worldState.Commit(spec, commitStorageRoots: false);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(worldState, block, options, ReceiptsTracer, spec);

        if (spec.IsEip4844Enabled)
        {
            block.Header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        block.Header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
        ApplyMinerRewards(block, blockTracer, spec, worldState);
        _withdrawalProcessor.ProcessWithdrawals(block, spec, worldState);
        ReceiptsTracer.EndBlockTrace();

        worldState.Commit(spec, commitStorageRoots: true);

        if (BlockchainProcessor.IsMainProcessingThread)
        {
            // Get the accounts that have been changed
            block.AccountChanges = worldState.GetAccountChanges();
        }

        if (ShouldComputeStateRoot(block.Header))
        {
            worldState.RecalculateStateRoot();
            block.Header.StateRoot = worldState.StateRoot;
        }

        block.Header.Hash = block.Header.CalculateHash();

        return receipts;
    }

    private void StoreBeaconRoot(Block block, IReleaseSpec spec, IWorldState worldState)
    {
        try
        {
            _beaconBlockRootHandler.StoreBeaconRoot(block, spec, worldState);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Storing beacon block root for block {block.ToString(Block.Format.FullHashAndNumber)} failed: {e}");
        }
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
    private void ApplyMinerRewards(Block block, IBlockTracer tracer, IReleaseSpec spec, IWorldState worldState)
    {
        if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
        BlockReward[] rewards = _rewardCalculator.CalculateRewards(block, worldState);
        for (int i = 0; i < rewards.Length; i++)
        {
            BlockReward reward = rewards[i];

            using ITxTracer txTracer = tracer.IsTracingRewards
                ? // we need this tracer to be able to track any potential miner account creation
                tracer.StartNewTxTrace(null)
                : NullTxTracer.Instance;

            ApplyMinerReward(block, reward, spec, worldState);

            if (tracer.IsTracingRewards)
            {
                tracer.EndTxTrace();
                tracer.ReportReward(reward.Address, reward.RewardType.ToLowerString(), reward.Value);
                if (txTracer.IsTracingState)
                {
                    worldState.Commit(spec, txTracer);
                }
            }
        }
    }

    // TODO: block processor pipeline (only where rewards needed)
    private void ApplyMinerReward(Block block, BlockReward reward, IReleaseSpec spec, IWorldState worldState)
    {
        if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)reward.Value / (BigInteger)Unit.Ether:N3}{Unit.EthSymbol} for account at {reward.Address}");

        if (!worldState.AccountExists(reward.Address))
        {
            worldState.CreateAccount(reward.Address, reward.Value);
        }
        else
        {
            worldState.AddToBalance(reward.Address, reward.Value, spec);
        }
    }

    // TODO: block processor pipeline
    private void ApplyDaoTransition(Block block, IWorldState worldState)
    {
        if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == block.Header.Number)
        {
            if (_logger.IsInfo) _logger.Info("Applying the DAO transition");
            Address withdrawAccount = DaoData.DaoWithdrawalAccount;
            if (!worldState.AccountExists(withdrawAccount))
            {
                worldState.CreateAccount(withdrawAccount, 0);
            }

            foreach (Address daoAccount in DaoData.DaoAccounts)
            {
                UInt256 balance = worldState.GetBalance(daoAccount);
                worldState.AddToBalance(withdrawAccount, balance, Dao.Instance);
                worldState.SubtractFromBalance(daoAccount, balance, Dao.Instance);
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
