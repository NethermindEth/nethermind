// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockHashInState;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.BeaconBlockRoot;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Verkle;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Utils;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor : IBlockProcessor
{
    private readonly ILogger _logger;
    protected readonly ISpecProvider _specProvider;
    protected readonly IWorldState _stateProvider;
    private readonly IReceiptStorage _receiptStorage;
    protected readonly IReceiptsRootCalculator _receiptsRootCalculator;
    private readonly IWitnessCollector _witnessCollector;
    protected readonly IWithdrawalProcessor _withdrawalProcessor;
    private readonly IBeaconBlockRootHandler _beaconBlockRootHandler;
    private readonly IBlockHashInStateHandler _blockHashInStateHandlerHandler;
    private readonly IBlockValidator _blockValidator;
    private readonly IRewardCalculator _rewardCalculator;
    protected readonly IBlockProcessor.IBlockTransactionsExecutor _blockTransactionsExecutor;
    private readonly IBlockTree _blockTree;

    // TODO: will be removed in future
    public IBlockProcessor.IBlockTransactionsExecutor StatelessBlockTransactionsExecutor;
    public bool ShouldVerifyIncomingWitness { get; set; } = false;
    public bool ShouldDoStatelessStuff { get; set; } = false;
    public bool ShouldGenerateWitness { get; set; } = true;

    private const int MaxUncommittedBlocks = 64;

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected BlockExecutionTracer ExecutionTracer { get; set; }

    public BlockProcessor(
        ISpecProvider? specProvider,
        IBlockValidator? blockValidator,
        IRewardCalculator? rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
        IWorldState? stateProvider,
        IReceiptStorage? receiptStorage,
        IWitnessCollector? witnessCollector,
        IBlockTree? blockTree,
        ILogManager? logManager,
        IWithdrawalProcessor? withdrawalProcessor = null,
        IReceiptsRootCalculator? receiptsRootCalculator = null)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _witnessCollector = witnessCollector ?? throw new ArgumentNullException(nameof(witnessCollector));
        _withdrawalProcessor = withdrawalProcessor ?? new WithdrawalProcessor(stateProvider, logManager);
        _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
        _blockTransactionsExecutor = blockTransactionsExecutor ?? throw new ArgumentNullException(nameof(blockTransactionsExecutor));
        _receiptsRootCalculator = receiptsRootCalculator ?? ReceiptsRootCalculator.Instance;
        _beaconBlockRootHandler = new BeaconBlockRootHandler();
        _blockHashInStateHandlerHandler = new BlockHashInStateHandler();
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

        ExecutionTracer = new BlockExecutionTracer(true, true);
    }

    public event EventHandler<BlockProcessedEventArgs> BlockProcessed;

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

        bool notReadOnly = !options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
        int blocksCount = suggestedBlocks.Count;
        Block[] processedBlocks = new Block[blocksCount];
        using IDisposable tracker = _witnessCollector.TrackOnThisThread();
        try
        {
            for (int i = 0; i < blocksCount; i++)
            {
                if (blocksCount > 64 && i % 8 == 0)
                {
                    if (_logger.IsInfo) _logger.Info($"Processing part of a long blocks branch {i}/{blocksCount}. Block: {suggestedBlocks[i]}");
                }

                _witnessCollector.Reset();
                (Block processedBlock, TxReceipt[] receipts) = ProcessOne(suggestedBlocks[i], options, blockTracer);
                processedBlocks[i] = processedBlock;

                // be cautious here as AuRa depends on processing
                PreCommitBlock(newBranchStateRoot, suggestedBlocks[i].Number);
                if (notReadOnly)
                {
                    _witnessCollector.Persist(processedBlock.Hash!);
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
                    Hash256? newStateRoot = suggestedBlocks[i].StateRoot;
                    InitBranch(newStateRoot, false);
                }
            }

            // TODO: temporary fix for verkle block processing
            if (options.ContainsFlag(ProcessingOptions.ReadOnlyChain))
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
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;

    // TODO: move to branch processor
    protected virtual void InitBranch(Hash256 branchStateRoot, bool incrementReorgMetric = true)
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

        // TODO: here we assume (and its a good assumption) that DAO is before stateless execution starts
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
        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && !_blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock))
        {
            if (_logger.IsError) _logger.Error($"Processed block is not valid {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}");
            if (_logger.IsError) _logger.Error($"Suggested block TD: {suggestedBlock.TotalDifficulty}, Suggested block IsPostMerge {suggestedBlock.IsPostMerge}, Block TD: {block.TotalDifficulty}, Block IsPostMerge {block.IsPostMerge}");
            throw new InvalidBlockException(suggestedBlock);
        }
    }

    private bool ShouldComputeStateRoot(BlockHeader header) =>
        !header.IsGenesis || !_specProvider.GenesisStateUnavailable;

    // TODO: remove
    private (IBlockProcessor.IBlockTransactionsExecutor, IWorldState) GetOrCreateExecutorAndState(Block block, ExecutionWitness witness)
    {
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor;
        IWorldState worldState;
        if (block.IsGenesis)
        {
            blockTransactionsExecutor = StatelessBlockTransactionsExecutor;
            worldState = _stateProvider;

        }
        else
        {
            block.Header.MaybeParent!.TryGetTarget(out BlockHeader maybeParent);
            Banderwagon stateRoot = Banderwagon.FromBytes(maybeParent!.StateRoot!.Bytes.ToArray())!.Value;
            worldState = new VerkleWorldState(witness, stateRoot, LimboLogs.Instance);
            blockTransactionsExecutor = StatelessBlockTransactionsExecutor.WithNewStateProvider(worldState);
        }

        return (blockTransactionsExecutor, worldState);
    }
    // TODO: remove

    private void InitEip2935History(BlockHeader currentBlock, IReleaseSpec spec, IWorldState stateProvider)
    {
        long current = currentBlock.Number;
        BlockHeader header = currentBlock;
        for (var i = 0; i < Math.Min(256, current - 1); i++)
        {
            _blockHashInStateHandlerHandler.AddParentBlockHashToState(header, spec, stateProvider);
             header = _blockTree.FindParentHeader(currentBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
             if (header is null)
             {
                 throw new InvalidDataException("Parent header cannot be found when executing BLOCKHASH operation");
             }
        }
    }

    // TODO: block processor pipeline
    protected virtual TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options)
    {
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        if (ShouldVerifyIncomingWitness)
        {
            if (!block.IsGenesis)
            {
                block.Header.MaybeParent!.TryGetTarget(out BlockHeader maybeParent);
                Banderwagon stateRoot = Banderwagon.FromBytes(maybeParent!.StateRoot!.Bytes.ToArray())!.Value;
                try
                {
                    VerkleWorldState? incomingWorldState = new(block.ExecutionWitness, stateRoot, LimboLogs.Instance);
                    _logger.Info($"Incoming Witness - VerkleWorldState StateRoot:{incomingWorldState.StateRoot}");
                }
                catch (Exception e)
                {
                    _logger.Error("Verkle proof verification failed for incoming witness.", e);
                }
            }
        }

        ExecutionTracer.SetOtherTracer(blockTracer);
        ExecutionTracer.StartNewBlockTrace(block);

        _beaconBlockRootHandler.ApplyContractStateChanges(block, spec, _stateProvider);

        if (spec.IsEip2935Enabled)
        {
            // TODO: find a better way to handle this - no need to have this check everytime
            //      this would just be true on the fork block
            BlockHeader parentHeader = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
            if (parentHeader is not null && parentHeader!.Timestamp < spec.Eip2935TransitionTimeStamp)
            {
                InitEip2935History(block.Header, spec, _stateProvider);
            }
            else
            {
                _blockHashInStateHandlerHandler.AddParentBlockHashToState(block.Header, spec, _stateProvider);
            }
        }
        _stateProvider.Commit(spec);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ExecutionTracer, spec);

        if (spec.IsEip4844Enabled)
        {
            block.Header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        block.Header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);

        if (!block.IsGenesis && block.Transactions.Length != 0)
        {
            VerkleWitness? gasWitness = null;
            if (blockTracer.IsTracingAccessWitness) gasWitness = new VerkleWitness();
            gasWitness?.AccessForGasBeneficiary(block.Header.GasBeneficiary);
            // TODO: possibly rename this function to just ReportWitness - can be used for both withdrawal and gasBeneficiary
            if (blockTracer.IsTracingAccessWitness) blockTracer.ReportAccessWitness(gasWitness);
        }


        ApplyMinerRewards(block, blockTracer, spec);
        _withdrawalProcessor.ProcessWithdrawals(block, ExecutionTracer, spec);
        ExecutionTracer.EndBlockTrace();

        ExecutionWitness? witness = null;
        if (options.ContainsFlag(ProcessingOptions.ProducingBlock) && spec.IsVerkleTreeEipEnabled &&
            !block.IsGenesis)
        {
            byte[][] witnessKeys = ExecutionTracer.WitnessKeys.ToArray();
            var verkleWorldState = _stateProvider as VerkleWorldState;
            witness = witnessKeys.Length == 0 ? new ExecutionWitness() : verkleWorldState?.GenerateExecutionWitness(witnessKeys, out _);
            block.Body.ExecutionWitness = witness;
        }

        _stateProvider.Commit(spec);

        if (ShouldComputeStateRoot(block.Header))
        {
            _stateProvider.RecalculateStateRoot();
            block.Header.StateRoot = _stateProvider.StateRoot;
        }

        block.Header.Hash = block.Header.CalculateHash();

        if (ShouldGenerateWitness && ShouldDoStatelessStuff)
        {
            try
            {
                (IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor, IWorldState worldState) =
                    GetOrCreateExecutorAndState(block, witness!);
                ExecutionTracer.StartNewBlockTrace(block);
                TxReceipt[] receiptsSl = blockTransactionsExecutor.ProcessTransactions(block, options, ExecutionTracer, spec);

                block.Header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
                ApplyMinerRewards(block, blockTracer, spec);
                _withdrawalProcessor.ProcessWithdrawals(block, ExecutionTracer, spec);
                ExecutionTracer.EndBlockTrace();

                worldState.Commit(spec);
                worldState.RecalculateStateRoot();
            }
            catch (Exception e)
            {
                _logger.Error($"Failed while doing stateless stuff", e);
                return receipts;
            }
        }


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

        if (bh.MaybeParent is not null)
        {
            bh.MaybeParent.TryGetTarget(out BlockHeader maybeParent);
            headerForProcessing.MaybeParent = new WeakReference<BlockHeader>(maybeParent);
        }


        return suggestedBlock.CreateCopy(headerForProcessing);
    }

    // TODO: block processor pipeline
    protected void ApplyMinerRewards(Block block, IBlockTracer tracer, IReleaseSpec spec)
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
