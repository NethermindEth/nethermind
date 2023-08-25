// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
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
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor : IBlockProcessor
{
    private readonly ILogger _logger;
    protected readonly ISpecProvider _specProvider;
    protected readonly IWorldState _stateProvider;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IWitnessCollector _witnessCollector;
    protected readonly IWithdrawalProcessor _withdrawalProcessor;
    private readonly IBlockValidator _blockValidator;
    private readonly IRewardCalculator _rewardCalculator;
    protected readonly IBlockProcessor.IBlockTransactionsExecutor _blockTransactionsExecutor;

    // TODO: will be removed in future
    public IBlockProcessor.IBlockTransactionsExecutor StatelessBlockTransactionsExecutor;
    public bool ShouldVerifyIncomingWitness { get; set; } = false;
    public bool ShouldDoStatelessStuff { get; set; } = false;
    public bool ShouldGenerateWitness { get; set; } = false;

    private const int MaxUncommittedBlocks = 64;

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected readonly BlockExecutionTracer _executionTracer;

    public BlockProcessor(
        ISpecProvider? specProvider,
        IBlockValidator? blockValidator,
        IRewardCalculator? rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
        IWorldState? stateProvider,
        IReceiptStorage? receiptStorage,
        IWitnessCollector? witnessCollector,
        ILogManager? logManager,
        IWithdrawalProcessor? withdrawalProcessor = null)
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
        _executionTracer = new BlockExecutionTracer(true, true);
    }

    public event EventHandler<BlockProcessedEventArgs> BlockProcessed;

    public event EventHandler<TxProcessedEventArgs> TransactionProcessed
    {
        add { _blockTransactionsExecutor.TransactionProcessed += value; }
        remove { _blockTransactionsExecutor.TransactionProcessed -= value; }
    }

    // TODO: move to branch processor
    public Block[] Process(Keccak newBranchStateRoot, List<Block> suggestedBlocks, ProcessingOptions options, IBlockTracer blockTracer)
    {
        if (suggestedBlocks.Count == 0) return Array.Empty<Block>();

        BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs(suggestedBlocks));

        /* We need to save the snapshot state root before reorganization in case the new branch has invalid blocks.
           In case of invalid blocks on the new branch we will discard the entire branch and come back to
           the previous head state.*/
        Keccak previousBranchStateRoot = CreateCheckpoint();
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
                    Keccak? newStateRoot = suggestedBlocks[i].StateRoot;
                    InitBranch(newStateRoot, false);
                }
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
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;

    // TODO: move to branch processor
    protected virtual void InitBranch(Keccak branchStateRoot, bool incrementReorgMetric = true)
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
    private Keccak CreateCheckpoint()
    {
        return _stateProvider.StateRoot;
    }

    // TODO: move to block processing pipeline
    private void PreCommitBlock(Keccak newBranchStateRoot, long blockNumber)
    {
        if (_logger.IsTrace) _logger.Trace($"Committing the branch - {newBranchStateRoot}");
        _stateProvider.CommitTree(blockNumber);
    }

    // TODO: move to branch processor
    private void RestoreBranch(Keccak branchingPointStateRoot)
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
                    VerkleWorldState? incomingWorldState = new (block.ExecutionWitness, stateRoot, LimboLogs.Instance);
                    _logger.Info($"Incoming Witness - VerkleWorldState StateRoot:{incomingWorldState.StateRoot}");
                }
                catch (Exception e)
                {
                    _logger.Error("Verkle proof verification failed for incoming witness.", e);
                }
            }
        }

        _executionTracer.SetOtherTracer(blockTracer);
        _executionTracer.StartNewBlockTrace(block);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, _executionTracer, spec);

        if (spec.IsEip4844Enabled)
        {
            block.Header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        block.Header.ReceiptsRoot = receipts.GetReceiptsRoot(spec, block.ReceiptsRoot);
        ApplyMinerRewards(block, blockTracer, spec);
        _withdrawalProcessor.ProcessWithdrawals(block, spec);
        _executionTracer.EndBlockTrace();

        ExecutionWitness? witness = null;
        if (ShouldGenerateWitness)
        {
            byte[][] witnessKeys = _executionTracer.WitnessKeys.ToArray();
            VerkleWorldState? verkleWorldState = _stateProvider as VerkleWorldState;
            witness = witnessKeys.Length == 0 ? null : verkleWorldState?.GenerateExecutionWitness(witnessKeys, out _);
            // IJsonSerializer ser = new EthereumJsonSerializer();
            // _logger.Info($"BLOCK PROCESSOR WITNESS: {spec.IsVerkleTreeEipEnabled} {!block.IsGenesis} {options} {ser.Serialize(witness)}");
            if (options.ContainsFlag(ProcessingOptions.ProducingBlock) && spec.IsVerkleTreeEipEnabled &&
                !block.IsGenesis) block.Body.ExecutionWitness = witness;
        }

        _stateProvider.Commit(spec);
        _stateProvider.RecalculateStateRoot();

        block.Header.StateRoot = _stateProvider.StateRoot;
        block.Header.Hash = block.Header.CalculateHash();

        if (ShouldGenerateWitness && ShouldDoStatelessStuff)
        {
            try
            {
                (IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor, IWorldState worldState) =
                    GetOrCreateExecutorAndState(block, witness!);
                _executionTracer.StartNewBlockTrace(block);
                TxReceipt[] receiptsSl = blockTransactionsExecutor.ProcessTransactions(block, options, _executionTracer, spec);

                block.Header.ReceiptsRoot = receipts.GetReceiptsRoot(spec, block.ReceiptsRoot);
                ApplyMinerRewards(block, blockTracer, spec);
                _withdrawalProcessor.ProcessWithdrawals(block, spec);
                _executionTracer.EndBlockTrace();

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
        // Setting canonical is done by ReceiptCanonicalityMonitor on block move to main
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
        };
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

            ITxTracer txTracer = NullTxTracer.Instance;
            if (tracer.IsTracingRewards)
            {
                // we need this tracer to be able to track any potential miner account creation
                txTracer = tracer.StartNewTxTrace(null);
            }

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
}
