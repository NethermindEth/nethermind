// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State;
using static Nethermind.Consensus.Processing.IBlockProcessor;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
    : IBlockProcessor
{
    private readonly ILogger _logger;
    private readonly TracedAccessWorldState? _tracedAccessWorldState;
    protected readonly WorldStateMetricsDecorator _stateProvider;
    private readonly IReceiptsRootCalculator _receiptsRootCalculator = ReceiptsRootCalculator.Instance;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockValidator _blockValidator;
    private readonly IRewardCalculator _rewardCalculator;
    private readonly IBlockTransactionsExecutor _blockTransactionsExecutor;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IBeaconBlockRootHandler _beaconBlockRootHandler;
    private readonly IBlockhashStore _blockHashStore;
    private readonly ILogManager _logManager;
    private readonly IWithdrawalProcessor _withdrawalProcessor;
    private readonly IExecutionRequestsProcessor _executionRequestsProcessor;

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected BlockReceiptsTracer ReceiptsTracer { get; set; } = new();

    public BlockProcessor(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockTransactionsExecutor blockTransactionsExecutor,
        IWorldState stateProvider,
        IReceiptStorage receiptStorage,
        IBeaconBlockRootHandler beaconBlockRootHandler,
        IBlockhashStore blockHashStore,
        ILogManager logManager,
        IWithdrawalProcessor withdrawalProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor)
    {
        _specProvider = specProvider;
        _blockValidator = blockValidator;
        _rewardCalculator = rewardCalculator;
        _blockTransactionsExecutor = blockTransactionsExecutor;
        _receiptStorage = receiptStorage;
        _beaconBlockRootHandler = beaconBlockRootHandler;
        _blockHashStore = blockHashStore;
        _logManager = logManager;
        _withdrawalProcessor = withdrawalProcessor;
        _executionRequestsProcessor = executionRequestsProcessor;

        _logger = _logManager.GetClassLogger();
        _tracedAccessWorldState = stateProvider as TracedAccessWorldState;
        _stateProvider = new(stateProvider);
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Processing block {suggestedBlock.ToString(Block.Format.Short)} ({options})");

        ApplyDaoTransition(suggestedBlock);
        Block block = PrepareBlockForProcessing(suggestedBlock);
        TxReceipt[] receipts = ProcessBlock(block, blockTracer, options, spec, token);
        ValidateProcessedBlock(suggestedBlock, options, block, receipts);
        if (options.ContainsFlag(ProcessingOptions.StoreReceipts))
        {
            StoreTxReceipts(block, receipts, spec);
        }

        return (block, receipts);
    }

    private void ValidateProcessedBlock(Block suggestedBlock, ProcessingOptions options, Block block, TxReceipt[] receipts)
    {
        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && !_blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock, out string? error))
        {
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(suggestedBlock, "invalid block after processing"));
            throw new InvalidBlockException(suggestedBlock, error);
        }

        // Block is valid, copy the account changes as we use the suggested block not the processed one
        suggestedBlock.AccountChanges = block.AccountChanges;
        suggestedBlock.ExecutionRequests = block.ExecutionRequests;
    }

    private bool ShouldComputeStateRoot(BlockHeader header) =>
        !header.IsGenesis || !_specProvider.GenesisStateUnavailable;

    protected virtual TxReceipt[] ProcessBlock(
        Block block,
        IBlockTracer blockTracer,
        ProcessingOptions options,
        IReleaseSpec spec,
        CancellationToken token)
    {
        BlockBody body = block.Body;
        BlockHeader header = block.Header;

        ReceiptsTracer.SetOtherTracer(blockTracer);
        ReceiptsTracer.StartNewBlockTrace(block);

        _blockTransactionsExecutor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, spec));

        if (_tracedAccessWorldState is not null)
        {
            _tracedAccessWorldState.Enabled = spec.BlockLevelAccessListsEnabled;
            _tracedAccessWorldState.BlockAccessList.ResetBlockAccessIndex();
        }

        StoreBeaconRoot(block, spec);
        _blockHashStore.ApplyBlockhashStateChanges(header, spec);
        _stateProvider.Commit(spec, commitRoots: false);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);

        _stateProvider.Commit(spec, commitRoots: false);

        CalculateBlooms(receipts);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        header.ReceiptsRoot = _receiptsRootCalculator.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
        ApplyMinerRewards(block, blockTracer, spec);
        _withdrawalProcessor.ProcessWithdrawals(block, spec);

        // We need to do a commit here as in _executionRequestsProcessor while executing system transactions
        // we do WorldState.Commit(SystemTransactionReleaseSpec.Instance). In SystemTransactionReleaseSpec
        // Eip158Enabled=false, so we end up persisting empty accounts created while processing withdrawals.
        _stateProvider.Commit(spec, commitRoots: false);

        _executionRequestsProcessor.ProcessExecutionRequests(block, _stateProvider, receipts, spec);

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

        if (_tracedAccessWorldState is not null && spec.BlockLevelAccessListsEnabled)
        {
            if (block.IsGenesis)
            {
                header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
            }
            else
            {
                body.BlockAccessList = _tracedAccessWorldState.BlockAccessList;
                block.EncodedBlockAccessList = Rlp.Encode(_tracedAccessWorldState.BlockAccessList).Bytes;
                header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
            }
        }

        header.Hash = header.CalculateHash();

        return receipts;
    }

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

    private void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        try
        {
            _beaconBlockRootHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Storing beacon block root for block {block.ToString(Block.Format.FullHashAndNumber)} failed: {e}");
        }
    }

    private void StoreTxReceipts(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
    {
        // Setting canonical is done when the BlockAddedToMain event is fired
        _receiptStorage.Insert(block, txReceipts, spec, false);
    }

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

    private void ApplyMinerReward(Block block, BlockReward reward, IReleaseSpec spec)
    {
        if (_logger.IsTrace) _logger.Trace($"  {(BigInteger)reward.Value / (BigInteger)Unit.Ether:N3}{Unit.EthSymbol} for account at {reward.Address}");

        _stateProvider.AddToBalanceAndCreateIfNotExists(reward.Address, reward.Value, spec);
    }

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
}
