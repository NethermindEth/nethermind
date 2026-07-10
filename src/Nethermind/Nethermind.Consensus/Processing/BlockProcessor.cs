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
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Metric;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using static Nethermind.Consensus.Processing.IBlockProcessor;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor(
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
    IExecutionRequestsProcessor executionRequestsProcessor,
    IBlockAccessListManager balManager)
    : IBlockProcessor
{
    protected readonly ISpecProvider _specProvider = specProvider;
    protected readonly IWorldState _stateProvider = stateProvider;
    protected readonly IBlockAccessListManager _balManager = balManager;
    protected readonly IBlockTransactionsExecutor _blockTransactionsExecutor = blockTransactionsExecutor;
    protected readonly ILogManager _logManager = logManager;
    private readonly ILogger _logger = logManager.GetClassLogger<BlockProcessor>();
    private readonly Lazy<BlockAccessListSystemContractHandler> _balSystemContractHandler = new(() =>
        new(
            beaconBlockRootHandler,
            blockHashStore,
            balManager
        ));
    private readonly Lazy<SystemContractHandler> _standardSystemContractHandler = new(() =>
        new(beaconBlockRootHandler, blockHashStore, withdrawalProcessor, executionRequestsProcessor));
    private ISystemContractHandler _systemContractHandler;

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected BlockReceiptsTracer ReceiptsTracer { get; set; } = new();

    public event Action? TransactionsExecuted;

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Processing block {suggestedBlock.ToString(Block.Format.Short)} ({options})");

        _balManager.PrepareForProcessing(suggestedBlock, spec, options);

        _systemContractHandler = _balManager.Enabled ? _balSystemContractHandler.Value : _standardSystemContractHandler.Value;

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
        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && !blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock, out string? error))
        {
            block.DisposeAccountChanges();
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(suggestedBlock, "invalid block after processing"));
            throw new InvalidBlockException(suggestedBlock, error);
        }

        PostValidation(suggestedBlock, block, receipts, options);
    }

    protected virtual void PostValidation(Block suggestedBlock, Block processedBlock, TxReceipt[] receipts, ProcessingOptions options)
    {
        // Block is valid, copy the execution artifacts back onto the suggested block.
        // Forward sync suggests blocks without BAL payloads, so the generated BAL needs to
        // follow the suggested block through main-chain updates and persistence.
        suggestedBlock.AccountChanges = processedBlock.AccountChanges;
        suggestedBlock.ExecutionRequests = processedBlock.ExecutionRequests;
        suggestedBlock.GeneratedBlockAccessList = processedBlock.GeneratedBlockAccessList;
        suggestedBlock.EncodedBlockAccessList = processedBlock.EncodedBlockAccessList ?? suggestedBlock.EncodedBlockAccessList;
    }

    protected bool ShouldComputeStateRoot(BlockHeader header) =>
        !header.IsGenesis || !_specProvider.GenesisStateUnavailable;

    protected virtual BlockExecutionContext CreateBlockExecutionContext(BlockHeader header, IReleaseSpec spec) =>
        new(header, spec);

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

        _blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header, spec));

        _balManager.Setup(block);

        _systemContractHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);
        _systemContractHandler.ApplyBlockhashStateChanges(header, spec);
        CommitState(spec);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);

        // Signal that transactions are done — subscribers can cancel background work (e.g. prewarmer)
        // to free the thread pool for blooms, receipts root, state root parallel work below
        TransactionsExecuted?.Invoke();

        CommitState(spec);

        // User-transaction storage is final here. Compute those storage roots while the
        // remaining block stages prepare receipts, blooms, rewards, and requests. The request
        // predeploys are excluded because they are written later in this method.
        HashSet<AddressAsKey> lateStorageWriters = [];
        if (spec.Eip7002ContractAddress is not null) lateStorageWriters.Add(spec.Eip7002ContractAddress);
        if (spec.Eip7251ContractAddress is not null) lateStorageWriters.Add(spec.Eip7251ContractAddress);
        _stateProvider.BeginEarlyStorageRoots(lateStorageWriters);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        Task<(Bloom BlockBloom, Hash256 ReceiptsRoot)>? bloomsAndReceiptsRootTask = null;
        if (ShouldCalculateReceiptsInBackground(receipts))
        {
            bloomsAndReceiptsRootTask = Task.Run(() =>
            {
                CalculateBlooms(receipts);
                return (AccumulateBlockBloom(receipts), CalculateReceiptsRoot(receipts, spec, block));
            });
        }
        else
        {
            CalculateBlooms(receipts);
            header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);
        }

        ApplyMinerRewards(block, blockTracer, spec);
        _systemContractHandler.ProcessWithdrawals(block, spec);

        // We need to do a commit here as in _executionRequestsProcessor while executing system transactions
        // the spec has Eip158Enabled=false, so we end up persisting empty accounts created while processing withdrawals.
        CommitState(spec);

        _systemContractHandler.ProcessExecutionRequests(block, _stateProvider, receipts, spec);

        ReceiptsTracer.EndBlockTrace(accumulateBlockBloom: bloomsAndReceiptsRootTask is null);

        CommitStateAndStorageRoots(spec);

        if (BlockchainProcessor.IsMainProcessingThread)
        {
            SetAccountChanges(block);
        }

        if (ShouldComputeStateRoot(header))
        {
            ComputeStateRoot(header);
        }

        _balManager.SetBlockAccessList(block);

        if (bloomsAndReceiptsRootTask is not null)
        {
            (header.Bloom, header.ReceiptsRoot) = bloomsAndReceiptsRootTask.GetAwaiter().GetResult();
        }

        header.Hash = header.CalculateHash();

        return receipts;
    }

    private void CommitState(IReleaseSpec spec)
    {
        using MetricsTimer<CommitTimeSink> _ = new();
        _stateProvider.Commit(spec, commitRoots: false);
    }

    private void CommitStateAndStorageRoots(IReleaseSpec spec)
    {
        using MetricsTimer<StorageMerkleTimeSink> _ = new();
        _stateProvider.Commit(spec, commitRoots: true);
    }

    private void ComputeStateRoot(BlockHeader header)
    {
        using (MetricsTimer<StateRootTimeSink> _ = new())
        {
            _stateProvider.RecalculateStateRoot();
        }
        header.StateRoot = _stateProvider.StateRoot;
    }

    private static partial bool ShouldCalculateReceiptsInBackground(TxReceipt[] receipts);

    private static int CountLogs(TxReceipt[] receipts)
    {
        int count = 0;
        foreach (TxReceipt? t in receipts)
        {
            count += t.Logs?.Length ?? 0;
        }

        return count;
    }

    private static Bloom AccumulateBlockBloom(TxReceipt[] receipts)
    {
        Bloom blockBloom = new();
        foreach (TxReceipt? t in receipts)
        {
            blockBloom.Accumulate(t.Bloom!);
        }

        return blockBloom;
    }

    private static Hash256 CalculateReceiptsRoot(TxReceipt[] receipts, IReleaseSpec spec, Block block)
    {
        using MetricsTimer<ReceiptsRootTimeSink> _ = new();
        return ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalculateBlooms(TxReceipt[] receipts)
    {
        using MetricsTimer<BloomsTimeSink> _ = new();

        // Avoid parallel scheduling overhead for small receipt counts: ParallelUnbalancedWork queues
        // ProcessorCount-1 ThreadPool items regardless of work size, which adds scheduling jitter and
        // allocation overhead that exceeds the bloom computation cost for small blocks.
        if (receipts.Length <= Environment.ProcessorCount)
        {
            foreach (TxReceipt? t in receipts)
            {
                t.CalculateBloom();
            }

            return;
        }

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

    // Timing sinks — forward elapsed ticks into the appropriate EVM metric counters.
    // CommitStateAndStorageRoots and ComputeStateRoot both feed StateHashTime (sum of the two),
    // so each sink also bumps StateHashTime alongside its specific metric.
    // Each sink wires IsEnabled to ExecutionMetricsFlag.IsActive: when the flag is off, the JIT
    // folds the surrounding MetricsTimer's Stopwatch calls and AddTicks dispatch to nothing.
    private readonly struct CommitTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks) => Evm.Metrics.IncrementCommitTime(ticks);
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    private readonly struct StorageMerkleTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks)
        {
            Evm.Metrics.IncrementStateHashTime(ticks);
            Evm.Metrics.IncrementStorageMerkleTime(ticks);
        }
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    private readonly struct StateRootTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks)
        {
            Evm.Metrics.IncrementStateHashTime(ticks);
            Evm.Metrics.IncrementStateRootTime(ticks);
        }
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    private readonly struct ReceiptsRootTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks) => Evm.Metrics.IncrementReceiptsRootTime(ticks);
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    private readonly struct BloomsTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks) => Evm.Metrics.IncrementBloomsTime(ticks);
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SetAccountChanges(Block block)
        => block.AccountChanges = _stateProvider.GetAccountChanges();

    private void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        try
        {
            beaconBlockRootHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Storing beacon block root for block {block.ToString(Block.Format.FullHashAndNumber)} failed: {e}");
        }
    }

    private void StoreTxReceipts(Block block, TxReceipt[] txReceipts, IReleaseSpec spec) =>
        // Setting canonical is done when the BlockAddedToMain event is fired
        receiptStorage.Insert(block, txReceipts, spec, false);

    protected virtual Block PrepareBlockForProcessing(Block suggestedBlock)
    {
        if (_logger.IsTrace) _logger.Trace($"{suggestedBlock.Header.ToString(BlockHeader.Format.Full)}");
        BlockHeader bh = suggestedBlock.Header;
        BlockHeader headerForProcessing = bh.CloneForProcessing();

        if (!ShouldComputeStateRoot(bh))
        {
            headerForProcessing.StateRoot = bh.StateRoot;
        }

        Block block = suggestedBlock.WithReplacedHeader(headerForProcessing);
        block.BlockAccessList = suggestedBlock.BlockAccessList;

        return block;
    }

    private void ApplyMinerRewards(Block block, IBlockTracer tracer, IReleaseSpec spec)
    {
        if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
        BlockReward[] rewards = rewardCalculator.CalculateRewards(block);
        if (tracer.IsTracingRewards)
        {
            for (int i = 0; i < rewards.Length; i++)
            {
                BlockReward reward = rewards[i];
                // we need this tracer to be able to track any potential miner account creation
                using ITxTracer txTracer = tracer.StartNewTxTrace(null);

                ApplyMinerReward(block, reward, spec);

                tracer.EndTxTrace();
                tracer.ReportReward(reward.Address, reward.RewardType.ToLowerString(), reward.Value);
                if (txTracer.IsTracingState)
                {
                    _stateProvider.Commit(spec, txTracer);
                }
            }
        }
        else
        {
            for (int i = 0; i < rewards.Length; i++)
            {
                ApplyMinerReward(block, rewards[i], spec);
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
        ulong? daoBlockNumber = _specProvider.DaoBlockNumber;
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
