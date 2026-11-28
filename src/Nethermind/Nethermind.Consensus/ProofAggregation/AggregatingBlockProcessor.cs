// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Replaces the raw leanSPHINCS witnesses of a block being produced with an EIP-8288 aggregated dependency proof
/// under <see cref="DependencyProofAggregation.BuilderOnly"/> and <see cref="DependencyProofAggregation.Mempool"/>.</summary>
/// <remarks>
/// Runs on <see cref="IBlockProcessor.TransactionsExecuted"/>, after the raw proof is built and before finalization, so the
/// header hash covers the replacement. A build under a deadline never waits for the prover: it schedules the block's
/// statement and keeps the raw witnesses, and a later build of a block with the same leanSPHINCS dependencies uses the proof.
/// A build without a deadline proves in place. The aggregated proof is used only when smaller than the raw one: a recursive
/// STARK outweighs a few signatures.
/// <para>A transaction known only from an EIP-8518 proof wrapper has no raw witness to fall back on, so a block holding one
/// must carry the aggregated proof, extending the proofs that cover it. When that proof is not ready under a deadline, the
/// build fails with <see cref="AggregatedProofNotReadyException"/> and is rebuilt without such transactions.</para>
/// </remarks>
/// <param name="blockTransactionsExecutor">The scope's executor; only a production executor aggregates, and a scope that
/// registers none, such as one supplying its own block processor, never does.</param>
/// <param name="production">The scope's state shared with <see cref="AggregatedTxPicker"/>.</param>
public sealed class AggregatingBlockProcessor(
    IBlockProcessor inner,
    DependencyProofCache proofCache,
    IBlocksConfig blocksConfig,
    ILogManager logManager,
    IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor = null,
    AggregatedTxProduction? production = null) : IBlockProcessor
{
    private readonly ILogger _logger = logManager.GetClassLogger<AggregatingBlockProcessor>();
    private readonly AggregatedTxProduction _production = production ?? new();

    public event Action<Block>? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer,
        IReleaseSpec spec, CancellationToken token = default)
    {
        if (!spec.IsEip8288Enabled || blocksConfig.DependencyProofAggregation is not (DependencyProofAggregation.BuilderOnly or DependencyProofAggregation.Mempool)
            || blockTransactionsExecutor is not BlockProcessor.IBlockProductionTransactionsExecutor)
        {
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }

        bool rebuild = _production.Excludes(suggestedBlock.Header);
        void Aggregate(Block block) => TryAggregate(block, spec, token);
        inner.TransactionsExecuted += Aggregate;
        try
        {
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }
        finally
        {
            inner.TransactionsExecuted -= Aggregate;
            if (rebuild) _production.ExcludeAggregatedFor = null;
        }
    }

    private void TryAggregate(Block block, IReleaseSpec spec, CancellationToken token)
    {
        if (block.DependencyProof is not { Length: > 0 } rawProof || !FrameTxDependencyProof.TryDecode(rawProof, out byte[][] witnesses)) return;

        FrameTxDependencyProof.Dependency[] dependencies = FrameTxDependencyProof.BlockDependencies(block);
        List<AggregatedDependency> aggregated = [];
        List<ReadOnlyMemory<byte>> aggregatedWitnesses = [];
        List<AggregatedDependency> unwitnessed = [];
        List<byte[]> raw = [];
        for (int j = 0; j < dependencies.Length; j++)
        {
            if (!AggregatedDependencyProof.IsAggregated(dependencies[j]))
            {
                raw.Add(witnesses[j]);
                continue;
            }

            AggregatedDependency dependency = Eip8288Dependencies.Map(dependencies[j].Key, dependencies[j].Msg);
            // Only a transaction known from a proof wrapper leaves a leanSPHINCS witness empty: a proof covers it instead.
            if (witnesses[j].Length == 0)
            {
                unwitnessed.Add(dependency);
                continue;
            }

            aggregated.Add(dependency);
            byte[] witness = new byte[Eip8288Constants.LeanSphincsWitnessBytes];
            dependencies[j].Key.Bytes.CopyTo(witness);
            witnesses[j].CopyTo(witness.AsSpan(ValueHash256.MemorySize));
            aggregatedWitnesses.Add(witness);
        }

        if (aggregated.Count == 0 && unwitnessed.Count == 0) return;

        bool mustAggregate = unwitnessed.Count != 0;
        // The block's dependencies are sorted by key before message, the guest's by message: reorder both together.
        List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize([.. aggregated, .. unwitnessed]);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(canonical);
        if (!proofCache.TryGetProof(depsHash, out byte[] stark))
        {
            AggregationInput? input = Plan(aggregated, aggregatedWitnesses, unwitnessed, canonical);
            if (input is null || token.CanBeCanceled)
            {
                if (input is not null) proofCache.Schedule(canonical, depsHash, input);
                Interlocked.Increment(ref Metrics.LeanRawFallbacks);
                if (mustAggregate) NotReady(block);
                return;
            }

            try
            {
                stark = DependencyProofAggregator.Prove(input, proofCache, depsHash, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The raw proof is already valid, so a prover failure costs only the aggregation, not the block.
                if (_logger.IsWarn) _logger.Warn($"EIP-8288 aggregation of block {block.ToString(Block.Format.Short)} failed, keeping raw witnesses: {exception.Message}");
                Interlocked.Increment(ref Metrics.LeanRawFallbacks);
                if (mustAggregate) NotReady(block);
                return;
            }

            proofCache.Remember(canonical, depsHash, stark);
        }

        byte[] proof = AggregatedDependencyProof.Encode(stark, raw);
        if (proof.Length >= rawProof.Length && !mustAggregate)
        {
            Interlocked.Increment(ref Metrics.LeanRawFallbacks);
            return;
        }

        token.ThrowIfCancellationRequested();
        block.DependencyProof = proof;
        block.Header.DependencyProofHash = Keccak.Compute(proof);
        Interlocked.Increment(ref Metrics.LeanAggregatedBlocks);
        if (_logger.IsDebug) _logger.Debug($"Aggregated {aggregated.Count} leanSPHINCS witnesses of block {block.ToString(Block.Format.Short)}: {rawProof.Length} -> {proof.Length} bytes");
    }

    /// <summary>Folds every witness, or extends the proven statement sharing the most dependencies, whichever is estimated
    /// cheaper, together with the proven statements covering <paramref name="unwitnessed"/>.</summary>
    /// <returns>The input, or <see langword="null"/> when no statement held covers <paramref name="unwitnessed"/> any more.</returns>
    private AggregationInput? Plan(List<AggregatedDependency> deps, List<ReadOnlyMemory<byte>> witnesses, List<AggregatedDependency> unwitnessed,
        List<AggregatedDependency> required)
    {
        List<RecursiveProofInput> covering = [];
        if (unwitnessed.Count != 0)
        {
            if (proofCache.Covering(unwitnessed) is not { } statements) return null;
            foreach (DependencyProofCache.Statement statement in statements) covering.Add(new RecursiveProofInput(statement.Deps, statement.Proof));
        }

        AggregationInput best = DependencyProofAggregator.Combine(deps, witnesses, required, [.. covering]);
        if (proofCache.BestOverlap(new HashSet<AggregatedDependency>(required)) is { } parent)
        {
            AggregationInput extended = DependencyProofAggregator.Combine(deps, witnesses, required, [.. covering, new RecursiveProofInput(parent.Deps, parent.Proof)]);
            if (DependencyProofAggregator.EstimatedCost(extended) < DependencyProofAggregator.EstimatedCost(best)) best = extended;
        }

        return best;
    }

    private void NotReady(Block block)
    {
        _production.ExcludeAggregatedFor = (block.Header.ParentHash!, block.Header.Timestamp);
        throw new AggregatedProofNotReadyException();
    }
}
