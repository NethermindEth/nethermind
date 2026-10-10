// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
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
using Nethermind.State;
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
    IBlockAccessListManager balManager,
    ILeanProofVerifier leanProofVerifier,
    LeanProofStore? leanProofStore = null)
    : IBlockProcessor
{
    private static readonly ParallelOptions SmallBloomOptions = new() { MaxDegreeOfParallelism = 2 };
    protected readonly ISpecProvider _specProvider = specProvider;
    private readonly ProductionProofCache _productionProofCache = leanProofVerifier as ProductionProofCache
        ?? new(leanProofVerifier ?? throw new ArgumentNullException(nameof(leanProofVerifier)));
    private (ValueHash256 Dependencies, ValueHash256 VerificationKey)? _productionProofKey;
    private byte[]? _productionProof;
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
        new(beaconBlockRootHandler, blockHashStore, withdrawalProcessor, executionRequestsProcessor, stateProvider));
    private ISystemContractHandler _systemContractHandler;

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected BlockReceiptsTracer ReceiptsTracer { get; set; } = new();

    internal sealed class BlockAccessListSequentialRetryException(
        BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException blockAccessListException)
        : InvalidBlockException(blockAccessListException.InvalidBlock, blockAccessListException.Message, blockAccessListException);

    public event Action? TransactionsExecuted;

    public virtual (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Processing block {suggestedBlock.ToString(Block.Format.Short)} ({options})");

        _balManager.PrepareForProcessing(suggestedBlock, spec, options);

        _systemContractHandler = _balManager.Enabled ? _balSystemContractHandler.Value : _standardSystemContractHandler.Value;

        ApplyDaoTransition(suggestedBlock);
        Block block = PrepareBlockForProcessing(suggestedBlock);
        if (spec.IsEip8288Enabled && options.ContainsFlag(ProcessingOptions.ProducingBlock) && block is not BlockToProduce)
        {
            if (_blockTransactionsExecutor is not IBlockProductionTransactionsExecutor)
                throw new ArgumentException("EIP-8288 production requires a block-production transaction executor.", nameof(options));
            block = new BlockToProduce(block.Header, block.Transactions, block.Uncles, block.Withdrawals)
            {
                BlockAccessList = block.BlockAccessList,
                InclusionListTransactions = block.InclusionListTransactions,
                InclusionListProvenDependencies = block.InclusionListProvenDependencies,
                InclusionListRecursiveStark = block.InclusionListRecursiveStark
            };
        }
        TxReceipt[] receipts;
        bool processed = false;
        try
        {
            receipts = ProcessBlock(block, blockTracer, options, spec, token);
            processed = true;
            ValidateProcessedBlock(suggestedBlock, options, block, receipts);
            _blockTransactionsExecutor.PublishTransactionProcessedEvents();
        }
        catch (BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException ex) when (_balManager.ParallelExecutionEnabled)
        {
            throw new BlockAccessListSequentialRetryException(ex);
        }
        catch (BlockAccessListManager.ParallelExecutionException ex) when (
            _balManager.ParallelExecutionEnabled &&
            ex.InnerException is BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException blockAccessListException)
        {
            throw new BlockAccessListSequentialRetryException(blockAccessListException);
        }
        finally
        {
            _blockTransactionsExecutor.ClearTransactionProcessedEvents();
            if (!processed) block.DisposeAccountChanges();
        }

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
        if (!block.IsGenesis && PredeployInstaller.HasActivePredeploys(spec))
        {
            _systemContractHandler.InstallPredeploys(spec);
        }
        CommitState(spec);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);

        // Signal that transactions are done — subscribers can cancel background work (e.g. prewarmer)
        // to free the thread pool for blooms, receipts root, state root parallel work below
        TransactionsExecuted?.Invoke();

        PrepareProductionProof(block, options, spec, token);
        return FinalizeBlock(block, blockTracer, options, spec, receipts);
    }

    protected virtual TxReceipt[] FinalizeBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options,
        IReleaseSpec spec, TxReceipt[] receipts) =>
        FinalizeBlock<OnFlag>(block, blockTracer, spec, receipts);

    private void PrepareProductionProof(Block block, ProcessingOptions options, IReleaseSpec spec, CancellationToken token)
    {
        if (spec.IsEip8288Enabled && options.ContainsFlag(ProcessingOptions.ProducingBlock))
        {
            token.ThrowIfCancellationRequested();
            List<FrameDependency> deps = Eip8288Dependencies.ForBlock(block);
            ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
            (ValueHash256, ValueHash256) key = (depsHash, new ValueHash256(Eip8288Constants.AggregatedVk));
            byte[] proof;
            if (deps.Count == 0)
                proof = [];
            else if (_productionProofKey == key && _productionProof is not null)
                proof = _productionProof;
            else
            {
                byte[]? prepared = null;
                if (leanProofStore?.TryGetRecursiveProof(deps, out prepared) == true
                    && !leanProofVerifier.VerifyRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, prepared!))
                {
                    leanProofStore.RemoveCachedRecursive(deps, prepared!);
                    prepared = null;
                }
                if (prepared is not null) proof = prepared;
                else
                {
                    BlockToProduce? producing = block as BlockToProduce;
                    // A pass with a deadline never waits for native proving, which takes longer than a slot: the statement
                    // is proven off the production path and the body is rebuilt from dependencies whose proofs exist.
                    if (producing is not null && token.CanBeCanceled && leanProofStore is not null)
                    {
                        HashSet<FrameDependency> limit = ChooseProvenLimit(block, leanProofStore.ProvenSubsets(new HashSet<FrameDependency>(deps)));
                        // Only the unrestricted body is worth proving; a restricted rebuild is a subset of it.
                        if (producing.LeanDependencyLimit is null && !_productionProofCache.IsScheduled)
                            ScheduleProductionProof(producing, deps, depsHash, leanProofStore);
                        throw new LeanProofNotReadyException(limit);
                    }
                    AggregationInput input = producing is null ? new() : RecursiveStarkAggregator.Combine(producing.LeanProofInputs, deps);
                    proof = RecursiveStarkAggregator.Prove(input, _productionProofCache, in depsHash, token);
                    leanProofStore?.AddCachedRecursive(deps, proof);
                }
                token.ThrowIfCancellationRequested();
                // One verified result per processor/backend; improvement passes reuse it without retaining old blocks.
                _productionProof = proof;
                _productionProofKey = key;
            }
            token.ThrowIfCancellationRequested();
            // Headers escape the processor; their mutable bytes cannot alias the improvement cache.
            block.Header.RecursiveStark = new RecursiveStark((byte[])proof.Clone(), new Hash256(depsHash));
        }
    }

    private const int MaxExtensionCandidates = 4;

    /// <summary>Schedules this body's statement with the cheapest input.</summary>
    /// <remarks>
    /// The input either folds every witness again or extends a verified overlapping statement, discarding its dependencies
    /// that the body no longer needs: a statement loses transactions to blocks included while it is being proven, and folding
    /// the rest again costs a recursive merge per child where the extension costs one discarding call.
    /// </remarks>
    private void ScheduleProductionProof(BlockToProduce producing, List<FrameDependency> deps, in ValueHash256 depsHash, LeanProofStore store)
    {
        AggregationInput scheduled = RecursiveStarkAggregator.Combine(producing.LeanProofInputs, deps);
        long cost = RecursiveStarkAggregator.EstimatedCost(scheduled);
        foreach (RecursiveProofInput parent in store.ProvenOverlaps(new HashSet<FrameDependency>(deps), MaxExtensionCandidates))
        {
            AggregationInput extended = RecursiveStarkAggregator.Combine(producing.LeanProofInputs, deps, parent);
            long extendedCost = RecursiveStarkAggregator.EstimatedCost(extended);
            if (extendedCost < cost) (scheduled, cost) = (extended, extendedCost);
        }
        _productionProofCache.TrySchedule(deps, depsHash, scheduled, store);
    }

    /// <summary>Picks the proven dependency set that keeps the most of this body's transactions includable.</summary>
    /// <remarks>
    /// A transaction is kept when its dependencies lie in the set and every earlier transaction of its nonce domain is kept,
    /// as a skipped nonce blocks the rest of that sender's sequence. Only a set whose kept transactions need exactly a proven statement
    /// qualifies, so the rebuild reuses that proof unchanged; the empty set always qualifies.
    /// </remarks>
    private static HashSet<FrameDependency> ChooseProvenLimit(Block block, List<FrameDependency[]> proven)
    {
        HashSet<ValueHash256> statements = [];
        foreach (FrameDependency[] subset in proven) statements.Add(Eip8288Dependencies.ComputeDepsHash(subset));
        HashSet<FrameDependency> best = [];
        int bestKept = -1;
        HashSet<(Address?, UInt256)> blocked = [];
        foreach (FrameDependency[] subset in proven)
        {
            HashSet<FrameDependency> limit = [.. subset];
            HashSet<FrameDependency> needed = [];
            int kept = 0;
            blocked.Clear();
            foreach (Transaction transaction in block.Transactions)
            {
                List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(transaction);
                (Address?, UInt256) domain = (transaction.SenderAddress, transaction.NonceKeys is { Length: > 0 } keys ? keys[0] : UInt256.Zero);
                if (blocked.Contains(domain)) continue;
                if (!limit.IsSupersetOf(dependencies))
                {
                    blocked.Add(domain);
                    continue;
                }
                needed.UnionWith(dependencies);
                kept++;
            }
            if (kept <= bestKept || needed.Count != 0 && !statements.Contains(Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.Canonicalize(needed))))
                continue;
            best = needed;
            bestKept = kept;
        }
        return best;
    }

    /// <summary>
    /// Finalizes the block; <typeparamref name="TComputesCommitments"/> selects whether the blooms, the receipts root,
    /// the storage and state roots and the header hash are derived. A replay whose only product is its trace reads none of them.
    /// </summary>
    protected TxReceipt[] FinalizeBlock<TComputesCommitments>(Block block, IBlockTracer blockTracer, IReleaseSpec spec, TxReceipt[] receipts)
        where TComputesCommitments : struct, IFlag
    {
        BlockHeader header = block.Header;

        using ParallelUnbalancedWork.WorkerScope workerScope = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
        (Bloom BlockBloom, Hash256 ReceiptsRoot) receiptResults = default;
        // Receipts are immutable apart from their blooms now; overlap with the first state commit too.
        using ParallelUnbalancedWork.BackgroundWork? bloomWork = TComputesCommitments.IsActive && ShouldCalculateReceiptsInBackground(receipts)
            ? StartBloomComputation(receipts)
            : null;
        using ParallelUnbalancedWork.BackgroundWork? receiptWork = bloomWork?.ContinueWith(() => receiptResults =
            (AccumulateBlockBloom(receipts), CalculateReceiptsRoot(receipts, spec, block)));

        CommitState(spec);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        if (receiptWork is null && TComputesCommitments.IsActive)
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

        ReceiptsTracer.EndBlockTrace(accumulateBlockBloom: receiptWork is null && TComputesCommitments.IsActive);

        if (TComputesCommitments.IsActive)
        {
            CommitStateAndStorageRoots(spec);
        }
        else
        {
            CommitState(spec);
        }

        if (BlockchainProcessor.IsMainProcessingThread)
        {
            SetAccountChanges(block);
        }

        if (TComputesCommitments.IsActive && ShouldComputeStateRoot(header))
        {
            ComputeStateRoot(header);
        }

        if (receiptWork is not null)
        {
            receiptWork.WaitForCompletion();
            (header.Bloom, header.ReceiptsRoot) = receiptResults;
        }

        _balManager.SetBlockAccessList(block);

        if (TComputesCommitments.IsActive)
        {
            header.Hash = header.CalculateHash();
        }

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

    protected virtual Hash256 CalculateReceiptsRoot(TxReceipt[] receipts, IReleaseSpec spec, Block block)
    {
        using MetricsTimer<ReceiptsRootTimeSink> _ = new();
        return ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParallelUnbalancedWork.BackgroundWork StartBloomComputation(TxReceipt[] receipts)
    {
        long started = ExecutionMetricsFlag.IsActive ? Stopwatch.GetTimestamp() : 0;
        ParallelOptions options = receipts.Length <= Environment.ProcessorCount
            ? SmallBloomOptions : ParallelUnbalancedWork.DefaultOptions;
        return ParallelUnbalancedWork.BackgroundFor(0, receipts.Length, options,
            i => receipts[i].CalculateBloom(), () =>
            {
                if (ExecutionMetricsFlag.IsActive)
                    BloomsTimeSink.AddTicks(Stopwatch.GetElapsedTime(started).Ticks);
            });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalculateBlooms(TxReceipt[] receipts)
    {
        using MetricsTimer<BloomsTimeSink> _ = new();

        // Parallel scheduling overhead exceeds the bloom computation cost for small blocks.
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
        // Setting canonical is done when the BlockAddedToMain event is fired.
        // The durable write is deferred off the processing path; visibility is synchronous.
        receiptStorage.InsertDeferred(block, txReceipts, spec);

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

                ApplyMinerReward(reward, spec);

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
                ApplyMinerReward(rewards[i], spec);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ApplyMinerReward(BlockReward reward, IReleaseSpec spec)
    {
        if (_logger.IsTrace) TraceMinerReward(reward);

        _stateProvider.AddToBalanceAndCreateIfNotExists(reward.Address, reward.Value, spec);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceMinerReward(BlockReward reward) => _logger.Trace($"  {(BigInteger)reward.Value / (BigInteger)Unit.Ether:N3}{Unit.EthSymbol} for account at {reward.Address}");

    /// <summary>Applies the DAO irregular state change when this block is the DAO fork block.</summary>
    /// <remarks>
    /// Split by build: the zkEVM guest serves post-merge blocks only, and naming the Dao fork here
    /// would compile its whole ancestor chain into the guest.
    /// </remarks>
    private partial void ApplyDaoTransition(Block block);

}
