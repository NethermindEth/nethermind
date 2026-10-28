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
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Messages;
using Nethermind.Core.Metric;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
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
    private readonly Lazy<BlockAccessListSystemContractHandler> _balSystemContractHandler = new(() => new(balManager));
    private readonly Lazy<SystemContractHandler> _standardSystemContractHandler = new(() =>
        new(beaconBlockRootHandler, blockHashStore, withdrawalProcessor, executionRequestsProcessor));
    private ISystemContractHandler _systemContractHandler;

    /// <summary>
    /// We use a single receipt tracer for all blocks. Internally receipt tracer forwards most of the calls
    /// to any block-specific tracers.
    /// </summary>
    protected BlockReceiptsTracer ReceiptsTracer { get; set; } = new();

    /// <summary>Requests a fresh parent-state scope and ordinary sequential execution.</summary>
    public sealed class BlockAccessListSequentialRetryException(BlockHeader block, string message, Exception? innerException = null)
        : InvalidBlockException(block, message, innerException);

    public event Action<Block>? TransactionsExecuted;

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
                InclusionLists = block.InclusionLists
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
        catch (InvalidBlockException ex) when (IsBlockAccessListFailure(ex))
        {
            if (_balManager.ParallelExecutionEnabled)
            {
                // Some checks run only on the parallel path, so a false positive on a valid block must stand out from
                // ordinary invalid blocks, even when a sequential retry later accepts it.
                Interlocked.Increment(ref Blockchain.Metrics.ParallelBlockAccessListRejections);
                if (_logger.IsWarn) _logger.Warn($"Parallel validation rejected block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} for its block access list: {ex.Message}");
                if (HasFrameTransaction(suggestedBlock)) throw new BlockAccessListSequentialRetryException(suggestedBlock.Header, ex.Message, ex);
            }

            KeepGeneratedBlockAccessList(suggestedBlock, block.GeneratedBlockAccessList);
            throw;
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

    private static bool HasFrameTransaction(Block block)
    {
        foreach (Transaction tx in block.Transactions)
        {
            if (tx.Type == TxType.FrameTx) return true;
        }

        return false;
    }

    // The manager resets its generated access list in place for the next block, so the bad-block store gets a copy.
    private static void KeepGeneratedBlockAccessList(Block suggestedBlock, GeneratedBlockAccessList? generated) =>
        suggestedBlock.EncodedGeneratedBlockAccessList = generated is null ? null : BlockAccessListDecoder.EncodeToBytes(generated);

    private static bool IsBlockAccessListFailure(InvalidBlockException e) =>
        e is BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException
            or BlockAccessListManager.ParallelExecutionException { InnerException: BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException };

    private void ValidateProcessedBlock(Block suggestedBlock, ProcessingOptions options, Block block, TxReceipt[] receipts)
    {
        ThrowIfProducedBlockExceedsItemLimit(block);

        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && !blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock, out string? error))
        {
            block.DisposeAccountChanges();
            KeepGeneratedBlockAccessList(suggestedBlock, suggestedBlock.GeneratedBlockAccessList);
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(suggestedBlock, "invalid block after processing"));
            throw new InvalidBlockException(suggestedBlock, error);
        }

        PostValidation(suggestedBlock, block, receipts, options);
    }

    /// <summary>
    /// EIP-7928: a producer's transaction selection leaves room for the end-of-block steps, so a built block over
    /// <c>block_gas_limit / ITEM_COST</c> means that reserve missed some end-of-block work. Such a block is invalid
    /// everywhere, so it is rejected here instead of being returned as a payload; the payload keeps its previous best
    /// build and the executor reserves the excess in later builds on the same parent.
    /// </summary>
    /// <remarks>
    /// A block without transactions is rejected too, but reserves nothing: transactions never remove items, so its
    /// items are the least any block on this parent with these withdrawals can have, and no build of this payload is
    /// valid.
    /// </remarks>
    private void ThrowIfProducedBlockExceedsItemLimit(Block block)
    {
        if (_blockTransactionsExecutor is not IBlockProductionTransactionsExecutor producer
            || block.GeneratedBlockAccessList is not { } blockAccessList)
        {
            return;
        }

        int itemCount = blockAccessList.ItemCount;
        if (!Eip7928Constants.ExceedsItemLimit(itemCount, block.Header.GasLimit)) return;

        ulong maxItems = block.Header.GasLimit / Eip7928Constants.ItemCost;
        string error = BlockErrorMessages.BlockAccessListGasLimitExceeded((ulong)itemCount, maxItems);
        if (block.Transactions.Length == 0)
        {
            if (_logger.IsError) _logger.Error($"Block {block.Number} cannot be built within the block access list item limit, even without transactions: {error}");
        }
        else
        {
            producer.ReportItemLimitExceeded(itemCount - (long)maxItems);
        }

        block.DisposeAccountChanges();
        throw new InvalidBlockException(block, error);
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

        // EIP-7668: set before tracing so receipts are built with the zero-length bloom instead of computing one.
        if (spec.IsEip7668Enabled) header.Bloom = Bloom.ZeroLength;

        ReceiptsTracer.SetOtherTracer(blockTracer);
        ReceiptsTracer.StartNewBlockTrace(block);

        _blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header, spec));

        _balManager.Setup(block);

        // EIP-8253: the fork-block nonce bump precedes every pre-execution system call.
        _balManager.ApplyZeroNonceStorageAccountsTransition(header, spec);
        _systemContractHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);
        _systemContractHandler.ApplyBlockhashStateChanges(header, spec);
        CommitState(spec);

        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);

        // Signal that transactions are done — subscribers can cancel background work (e.g. prewarmer)
        // to free the thread pool for blooms, receipts root, state root parallel work below
        TransactionsExecuted?.Invoke(block);

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
            bool latest = _productionProofCache.ObserveProduction(block.Timestamp, block.Number);
            List<FrameDependency> deps = Eip8288Dependencies.ForBlock(block, FrameTxSigHash.ComputeValue);
            ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps);
            Eip8288Verifier verifier = Eip8288Verifier.For(spec);
            (ValueHash256, ValueHash256) key = (depsHash, verifier.AggregatedVk);
            byte[] proof;
            if (deps.Count == 0)
                proof = [];
            else if (_productionProofKey == key && _productionProof is not null)
                proof = _productionProof;
            else
            {
                byte[]? prepared = null;
                if (leanProofStore?.TryGetRecursiveProof(deps, out prepared) == true
                    && !leanProofVerifier.VerifyRecursiveStark(in depsHash, verifier.AggregatedVk.Bytes, prepared!))
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
                        OfferInclusionListProofs(producing, leanProofStore);
                        HashSet<FrameDependency> attempted = [.. deps];
                        IncludableTransactions includable = new(block);
                        (HashSet<FrameDependency> limit, int kept) = includable.ChooseProvenLimit(leanProofStore.ProvenSubsets(attempted));
                        // Only the latest payload's unrestricted body is worth proving; a restricted rebuild is a subset of it.
                        if (latest && producing.LeanDependencyLimit is null && !_productionProofCache.IsScheduled)
                            ScheduleProductionProof(producing, includable, attempted, deps, depsHash, limit, kept, leanProofStore, verifier);
                        throw new LeanProofNotReadyException(limit);
                    }
                    AggregationInput input = producing is null ? new() : RecursiveStarkAggregator.Combine(producing.LeanProofInputs, deps);
                    proof = RecursiveStarkAggregator.Prove(input, _productionProofCache, verifier, in depsHash, token);
                    leanProofStore?.AddCachedRecursive(deps, proof);
                }
                token.ThrowIfCancellationRequested();
                // One verified result per processor/backend; improvement passes reuse it without retaining old blocks.
                _productionProof = proof;
                _productionProofKey = key;
            }
            token.ThrowIfCancellationRequested();
            // Produced blocks must not alias the mutable improvement cache.
            block.StarkProof = (byte[])proof.Clone();
            block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(proof), new Hash256(depsHash));
        }
    }

    private const int MaxExtensionCandidates = 4;

    /// <summary>Estimated cost (<see cref="RecursiveStarkAggregator.EstimatedCost"/>) above which a scheduled statement is cut to
    /// a nonce-ordered prefix of the body: two recursive merges and a leaf, about two slots on a two-thread prover.</summary>
    internal const long MaxScheduledStatementCost = 2 * RecursiveStarkAggregator.MergeCost + 4 * RecursiveStarkAggregator.SignatureCost;

    /// <summary>Offers the verified recursive proofs of the inclusion lists as reusable statements.</summary>
    /// <remarks>
    /// A rebuild under a deadline keeps only transactions whose dependencies form a proven statement. A verified list proof is one,
    /// so the rebuild can keep the list's transactions, which attesters still obligate, and reuse that proof unchanged.
    /// </remarks>
    private static void OfferInclusionListProofs(BlockToProduce producing, LeanProofStore store)
    {
        if (producing.InclusionListProofInput is not { } inclusionList) return;
        foreach (RecursiveProofInput child in inclusionList.RecursiveProofs)
        {
            if (child.InnerDeps is not { Count: > 0 } dependencies) continue;
            try { store.AddCachedRecursive(dependencies, child.Proof.ToArray()); }
            catch (ArgumentException) { }
        }
    }

    /// <summary>Schedules the cheapest statement that makes more of this body's transactions includable.</summary>
    /// <remarks>
    /// A verified statement for an earlier body is narrowed first, with one discarding call, to the transactions it still
    /// makes includable: its other transactions were included while it was being proven. Otherwise the statement covers what
    /// the body needs once the transactions of the proven set go into this block; a statement that also covered them would
    /// need another discarding call after their inclusion. It is proven either by folding every witness again or by
    /// extending a verified overlapping statement and discarding what the body no longer needs.
    /// </remarks>
    private void ScheduleProductionProof(BlockToProduce producing, IncludableTransactions includable, HashSet<FrameDependency> attempted,
        List<FrameDependency> deps, in ValueHash256 depsHash, HashSet<FrameDependency> provenLimit, int provenKept, LeanProofStore store,
        Eip8288Verifier verifier)
    {
        RecursiveProofInput narrowed = default;
        HashSet<FrameDependency>? narrowedNeeded = null;
        int narrowedKept = provenKept;
        foreach (RecursiveProofInput parent in store.ProvenOverlaps(attempted, MaxExtensionCandidates))
        {
            HashSet<FrameDependency> within = [.. parent.InnerDeps];
            within.IntersectWith(attempted);
            HashSet<FrameDependency> needed = [];
            int kept = includable.Keep(within, needed);
            if (kept <= narrowedKept || needed.Count == 0 || needed.Count == parent.InnerDeps.Count) continue;
            (narrowed, narrowedNeeded, narrowedKept) = (parent, needed, kept);
        }
        if (narrowedNeeded is not null)
        {
            List<FrameDependency> narrowedTarget = Eip8288Dependencies.Canonicalize(narrowedNeeded);
            List<FrameDependency> discards = [];
            foreach (FrameDependency dependency in narrowed.InnerDeps)
                if (!narrowedNeeded.Contains(dependency)) discards.Add(dependency);
            _productionProofCache.TrySchedule(narrowedTarget, Eip8288Dependencies.ComputeDepsHash(narrowedTarget),
                new AggregationInput { RecursiveProofs = [narrowed], Discards = discards }, verifier, store);
            return;
        }
        HashSet<FrameDependency> pending = includable.NeededAfter(provenLimit);
        if (pending.Count == 0) return;
        List<RecursiveProofInput> overlaps = store.ProvenOverlaps(pending, MaxExtensionCandidates);
        (AggregationInput scheduled, long cost) = Cheapest(pending);
        if (cost > MaxScheduledStatementCost)
        {
            // A statement over the whole backlog can take many slots while more arrives; prove a nonce-ordered prefix that fits
            // the budget instead, and let later passes take the rest.
            pending = includable.BoundedNeed(provenLimit, pending, set => Cheapest(set).Cost, MaxScheduledStatementCost);
            if (pending.Count == 0) return;
            (scheduled, cost) = Cheapest(pending);
        }
        List<FrameDependency> target = pending.Count == deps.Count ? deps : Eip8288Dependencies.Canonicalize(pending);
        if (store.TryGetRecursiveProof(target, out _)) return;
        ValueHash256 targetHash = pending.Count == deps.Count ? depsHash : Eip8288Dependencies.ComputeDepsHash(target);
        _productionProofCache.TrySchedule(target, targetHash, scheduled, verifier, store);

        // Folding every witness again, or extending a verified overlapping statement, whichever is estimated cheaper.
        (AggregationInput Input, long Cost) Cheapest(HashSet<FrameDependency> set)
        {
            List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(set);
            AggregationInput best = RecursiveStarkAggregator.Combine(producing.LeanProofInputs, canonical);
            long bestCost = RecursiveStarkAggregator.EstimatedCost(best);
            foreach (RecursiveProofInput parent in overlaps)
            {
                if (!Overlaps(parent, set)) continue;
                AggregationInput extended = RecursiveStarkAggregator.Combine(producing.LeanProofInputs, canonical, parent);
                long extendedCost = RecursiveStarkAggregator.EstimatedCost(extended);
                if (extendedCost < bestCost) (best, bestCost) = (extended, extendedCost);
            }
            return (best, bestCost);
        }
    }

    private static bool Overlaps(RecursiveProofInput parent, HashSet<FrameDependency> set)
    {
        foreach (FrameDependency dependency in parent.InnerDeps)
            if (set.Contains(dependency)) return true;
        return false;
    }

    /// <summary>The transactions of a body with their dependencies and nonce domains, to simulate restricting it.</summary>
    private sealed class IncludableTransactions
    {
        private readonly List<FrameDependency>[] _dependencies;
        private readonly (Address?, UInt256)[] _domains;
        private readonly bool[] _obligated;
        private readonly HashSet<(Address?, UInt256)> _blocked = [];

        public IncludableTransactions(Block block)
        {
            Transaction[] transactions = block.Transactions;
            _dependencies = new List<FrameDependency>[transactions.Length];
            _domains = new (Address?, UInt256)[transactions.Length];
            _obligated = new bool[transactions.Length];
            HashSet<Hash256> listed = [];
            foreach (Transaction listedTransaction in block.InclusionListTransactions ?? [])
                if (listedTransaction?.Hash is { } hash) listed.Add(hash);
            for (int i = 0; i < transactions.Length; i++)
            {
                _obligated[i] = transactions[i].Hash is { } hash && listed.Contains(hash);
                _dependencies[i] = Eip8288Dependencies.ForTransaction(transactions[i], FrameTxSigHash.ComputeValue);
                _domains[i] = (transactions[i].SenderAddress, transactions[i].NonceKeys is { Length: > 0 } keys ? keys[0] : UInt256.Zero);
            }
        }

        /// <summary>Counts the transactions a body restricted to <paramref name="limit"/> keeps and collects their dependencies.</summary>
        /// <remarks>
        /// A transaction is kept when its dependencies lie in the set and every earlier transaction of its nonce domain is kept,
        /// as a skipped nonce blocks the rest of that sender's sequence.
        /// </remarks>
        public int Keep(IReadOnlySet<FrameDependency> limit, HashSet<FrameDependency> needed, bool[]? kept = null)
        {
            int count = 0;
            _blocked.Clear();
            for (int i = 0; i < _dependencies.Length; i++)
            {
                if (_blocked.Contains(_domains[i])) continue;
                if (!limit.IsSupersetOf(_dependencies[i]))
                {
                    _blocked.Add(_domains[i]);
                    continue;
                }
                needed.UnionWith(_dependencies[i]);
                if (kept is not null) kept[i] = true;
                count++;
            }
            return count;
        }

        /// <summary>Picks the proven dependency set that keeps the most transactions includable.</summary>
        /// <remarks>
        /// Only a set whose kept transactions need exactly a proven statement qualifies, so the rebuild reuses that proof
        /// unchanged; the empty set always qualifies.
        /// </remarks>
        public (HashSet<FrameDependency> Limit, int Kept) ChooseProvenLimit(List<FrameDependency[]> proven)
        {
            HashSet<ValueHash256> statements = [];
            foreach (FrameDependency[] subset in proven) statements.Add(Eip8288Dependencies.ComputeDepsHash(subset));
            HashSet<FrameDependency> best = [];
            bool[] kept = new bool[_dependencies.Length];
            int bestKept = Keep(best, [], kept);
            int bestListed = Listed(kept);
            foreach (FrameDependency[] subset in proven)
            {
                HashSet<FrameDependency> needed = [];
                Array.Clear(kept);
                int count = Keep(new HashSet<FrameDependency>(subset), needed, kept);
                int listed = Listed(kept);
                // Obligated inclusion-list transactions come first: omitting one fails the list for an honest proposer.
                if (listed < bestListed || listed == bestListed && count <= bestKept
                    || needed.Count != 0 && !statements.Contains(Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.Canonicalize(needed))))
                    continue;
                best = needed;
                bestKept = count;
                bestListed = listed;
            }
            return (best, bestKept);
        }

        private int Listed(bool[] kept)
        {
            int count = 0;
            for (int i = 0; i < kept.Length; i++)
                if (kept[i] && _obligated[i]) count++;
            return count;
        }

        /// <summary>The dependencies, within <paramref name="needed"/>, of the longest nonce-ordered run of transactions not kept
        /// under <paramref name="limit"/> whose proving cost stays within <paramref name="budget"/>; the first such transaction is
        /// always taken.</summary>
        public HashSet<FrameDependency> BoundedNeed(IReadOnlySet<FrameDependency> limit, IReadOnlySet<FrameDependency> needed,
            Func<HashSet<FrameDependency>, long> cost, long budget)
        {
            bool[] kept = new bool[_dependencies.Length];
            Keep(limit, [], kept);
            HashSet<(Address?, UInt256)> blocked = [];
            HashSet<FrameDependency> selected = [];
            for (int i = 0; i < _dependencies.Length; i++)
            {
                if (kept[i] || blocked.Contains(_domains[i])) continue;
                HashSet<FrameDependency> candidate = [.. selected];
                foreach (FrameDependency dependency in _dependencies[i])
                    if (needed.Contains(dependency)) candidate.Add(dependency);
                if (candidate.Count == selected.Count) continue;
                if (selected.Count != 0 && cost(candidate) > budget)
                {
                    blocked.Add(_domains[i]);
                    continue;
                }
                selected = candidate;
            }
            return selected;
        }

        /// <summary>The dependencies the body still needs once the transactions kept under <paramref name="limit"/> are included.</summary>
        /// <remarks>
        /// A dependency only kept transactions use leaves with them. One that several transactions share stays: later
        /// transactions keep reusing it, and a statement without it would lose them to every statement that has it.
        /// </remarks>
        public HashSet<FrameDependency> NeededAfter(IReadOnlySet<FrameDependency> limit)
        {
            Dictionary<FrameDependency, int> uses = [];
            foreach (List<FrameDependency> dependencies in _dependencies)
                foreach (FrameDependency dependency in dependencies)
                    uses[dependency] = uses.GetValueOrDefault(dependency) + 1;
            bool[] kept = new bool[_dependencies.Length];
            Keep(limit, [], kept);
            HashSet<FrameDependency> needed = [];
            for (int i = 0; i < _dependencies.Length; i++)
                foreach (FrameDependency dependency in _dependencies[i])
                    if (!kept[i] || uses[dependency] > 1) needed.Add(dependency);
            return needed;
        }
    }

    /// <summary>
    /// Finalizes the block; <typeparamref name="TComputesCommitments"/> selects whether the blooms, the receipts root,
    /// the storage and state roots and the header hash are derived. A replay whose only product is its trace reads none of them.
    /// </summary>
    protected TxReceipt[] FinalizeBlock<TComputesCommitments>(Block block, IBlockTracer blockTracer, IReleaseSpec spec, TxReceipt[] receipts)
        where TComputesCommitments : struct, IFlag
    {
        // EIP-8116: execution relies on running totals, so convert to per-transaction gas once before the receipts root.
        if (spec.IsEip8116Enabled)
        {
            receipts.SetEip8116GasUsed();
        }

        BlockHeader header = block.Header;

        using ParallelUnbalancedWork.WorkerScope workerScope = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
        (Bloom BlockBloom, Hash256 ReceiptsRoot) receiptResults = default;
        // EIP-7668: ProcessBlock set the zero-length header bloom and the receipts were built with it, so no blooms are computed.
        bool bloomsRemoved = spec.IsEip7668Enabled;
        bool inBackground = TComputesCommitments.IsActive && ShouldCalculateReceiptsInBackground(receipts);
        // Receipts are immutable apart from their blooms now; overlap with the first state commit too.
        using ParallelUnbalancedWork.BackgroundWork? bloomWork = inBackground && !bloomsRemoved ? StartBloomComputation(receipts) : null;
        using ParallelUnbalancedWork.BackgroundWork? receiptWork = bloomWork is not null
            ? bloomWork.ContinueWith(() => receiptResults = (AccumulateBlockBloom(receipts), CalculateReceiptsRoot(receipts, spec, block)))
            : inBackground
                ? ParallelUnbalancedWork.BackgroundFor(0, 1, SmallBloomOptions, _ => receiptResults = (Bloom.ZeroLength, CalculateReceiptsRoot(receipts, spec, block)))
                : null;

        CommitState(spec);

        if (spec.IsEip4844Enabled)
        {
            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);
        }

        if (receiptWork is null && TComputesCommitments.IsActive)
        {
            if (!bloomsRemoved)
            {
                CalculateBlooms(receipts);
            }

            header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);
        }

        ApplyMinerRewards(block, blockTracer, spec);
        _systemContractHandler.ProcessWithdrawals(block, spec);

        // We need to do a commit here as in _executionRequestsProcessor while executing system transactions
        // the spec has Eip158Enabled=false, so we end up persisting empty accounts created while processing withdrawals.
        CommitState(spec);

        _systemContractHandler.ProcessExecutionRequests(block, _stateProvider, receipts, spec);

        ReceiptsTracer.EndBlockTrace(accumulateBlockBloom: receiptWork is null && TComputesCommitments.IsActive && !bloomsRemoved);

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

        if (BlockchainProcessor.IsMainProcessingThread)
        {
            Evm.Metrics.PublishBlockGasPrices(block.Transactions, spec.IsEip1559Enabled, header.BaseFeePerGas);
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
    {
        ArrayPoolList<AddressAsKey>? worldStateChanges = _stateProvider.GetAccountChanges();
        // Already set from the BAL when its changes went straight to the scope: the world state adds only what it did not cover.
        if (block.AccountChanges is not { } balChanges || block.BlockAccessList is not { } bal)
        {
            block.AccountChanges = worldStateChanges;
            return;
        }

        if (worldStateChanges is null) return;

        using (worldStateChanges)
        {
            foreach (AddressAsKey address in worldStateChanges.AsSpan())
            {
                if (bal.GetAccountChanges(address) is not { HasStateChanges: true }) balChanges.Add(address);
            }
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
