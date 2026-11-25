// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Attaches the EIP-8288 <c>stark_proof</c> and its header commitment to blocks being produced.</summary>
/// <remarks>Prepared on <see cref="IBlockProcessor.TransactionsExecuted"/>, before finalization, so the header hash covers the
/// commitment; an inner processor that never raises it fails the pass rather than return a block without a proof.</remarks>
internal sealed class DependencyProofBlockProcessor(
    IBlockProcessor inner,
    IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
    ILeanProofVerifier leanProofVerifier,
    LeanProofStore? leanProofStore = null) : IBlockProcessor
{
    private readonly DependencyProofCache _dependencyProofCache = leanProofVerifier as DependencyProofCache
        ?? new(leanProofVerifier ?? throw new ArgumentNullException(nameof(leanProofVerifier)));
    private (ValueHash256 Dependencies, ValueHash256 VerificationKey)? _productionProofKey;
    private byte[]? _productionProof;

    public event Action<Block>? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer,
        IReleaseSpec spec, CancellationToken token = default)
    {
        if (!spec.IsEip8288Enabled || !options.ContainsFlag(ProcessingOptions.ProducingBlock))
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

        if (suggestedBlock is not BlockToProduce)
        {
            if (blockTransactionsExecutor is not BlockProcessor.IBlockProductionTransactionsExecutor)
                throw new ArgumentException("EIP-8288 production requires a block-production transaction executor.", nameof(options));
            suggestedBlock = new BlockToProduce(suggestedBlock.Header, suggestedBlock.Transactions, suggestedBlock.Uncles, suggestedBlock.Withdrawals)
            {
                BlockAccessList = suggestedBlock.BlockAccessList,
                InclusionListTransactions = suggestedBlock.InclusionListTransactions,
                InclusionLists = suggestedBlock.InclusionLists
            };
        }

        bool prepared = false;
        Action<Block> prepare = block =>
        {
            PrepareProductionProof(block, spec, token);
            prepared = true;
        };
        (Block Block, TxReceipt[] Receipts) result;
        inner.TransactionsExecuted += prepare;
        try
        {
            result = inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }
        finally
        {
            inner.TransactionsExecuted -= prepare;
        }

        return prepared
            ? result
            : throw new InvalidOperationException($"The block processor did not raise {nameof(IBlockProcessor.TransactionsExecuted)}, so no EIP-8288 proof was attached.");
    }

    private void PrepareProductionProof(Block block, IReleaseSpec spec, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool latest = _dependencyProofCache.ObserveProduction(block.Timestamp, block.Number);
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
                && !leanProofVerifier.VerifyDependencyProof(in depsHash, verifier.AggregatedVk.Bytes, prepared!))
            {
                leanProofStore.RemoveCachedRecursive(deps, prepared!);
                prepared = null;
            }
            if (prepared is not null) proof = prepared;
            else
            {
                BlockToProduce? producing = block as BlockToProduce;
                // A pass with a deadline waits for no native proving but a rebuild's bounded merge of held proofs: the statement
                // is proven off the production path, and the body is rebuilt from a held statement per bucket merged within
                // MaxDeadlineMergeCost, or, when that merge does not fit, from one proven statement.
                if (producing is not null && token.CanBeCanceled && leanProofStore is not null
                    && !DependencyProofPlanner.MergesBeforeDeadline(producing, deps))
                {
                    DependencyProofPlanner.OfferInclusionListProofs(producing, leanProofStore);
                    HashSet<FrameDependency> attempted = [.. deps];
                    DependencyProofPlanner.IncludableTransactions includable = new(block);
                    List<FrameDependency[]> proven = leanProofStore.ProvenSubsets(attempted);
                    (HashSet<FrameDependency> limit, int kept) = producing.LeanProduction.DependencyLimit is null
                        ? includable.ChooseBucketLimit(proven)
                        : includable.ChooseProvenLimit(proven);
                    // Only the latest payload's unrestricted body is worth proving, and only when the rebuild leaves some of it out.
                    if (latest && producing.LeanProduction.DependencyLimit is null && kept < block.Transactions.Length
                        && !_dependencyProofCache.IsScheduledWithin(attempted))
                        DependencyProofPlanner.ScheduleProductionProof(producing, includable, attempted, deps, depsHash, limit, kept,
                            leanProofStore, verifier, _dependencyProofCache);
                    throw new LeanProofNotReadyException(limit);
                }
                AggregationInput input = producing is null ? new() : DependencyProofAggregator.Combine(producing.LeanProduction.ProofInputs, deps);
                try
                {
                    proof = DependencyProofAggregator.Prove(input, _dependencyProofCache, verifier, in depsHash, token);
                }
                // A prover that cannot take the statement now leaves the body to what is already proven, as a deadline would.
                catch (LeanProverBusyException) when (producing is not null && leanProofStore is not null)
                {
                    DependencyProofPlanner.OfferInclusionListProofs(producing, leanProofStore);
                    throw new LeanProofNotReadyException(new DependencyProofPlanner.IncludableTransactions(block)
                        .ChooseProvenLimit(leanProofStore.ProvenSubsets(new HashSet<FrameDependency>(deps))).Limit);
                }
                leanProofStore?.AddCachedRecursive(deps, proof);
            }
            token.ThrowIfCancellationRequested();
            // One verified result per processor/backend; improvement passes reuse it without retaining old blocks.
            _productionProof = proof;
            _productionProofKey = key;
        }
        token.ThrowIfCancellationRequested();
        // Produced blocks must not alias the mutable improvement cache.
        block.DependencyProof = (byte[])proof.Clone();
        block.Header.DependencyProofCommitment = new DependencyProofCommitment(Keccak.Compute(proof), new Hash256(depsHash));
    }
}
