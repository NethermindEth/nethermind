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
/// under <see cref="DependencyProofAggregation.BuilderOnly"/>.</summary>
/// <remarks>
/// Runs on <see cref="IBlockProcessor.TransactionsExecuted"/>, after the raw proof is built and before finalization, so the
/// header hash covers the replacement. A build under a deadline never waits for the prover: it schedules the block's
/// statement and keeps the raw witnesses, and a later build of a block with the same leanSPHINCS dependencies uses the proof.
/// A build without a deadline proves in place. The aggregated proof is used only when smaller than the raw one: a recursive
/// STARK outweighs a few signatures.
/// </remarks>
public sealed class AggregatingBlockProcessor(
    IBlockProcessor inner,
    IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
    DependencyProofCache proofCache,
    IBlocksConfig blocksConfig,
    ILogManager logManager) : IBlockProcessor
{
    private readonly ILogger _logger = logManager.GetClassLogger<AggregatingBlockProcessor>();

    public event Action<Block>? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer,
        IReleaseSpec spec, CancellationToken token = default)
    {
        if (!spec.IsEip8288Enabled || blocksConfig.DependencyProofAggregation != DependencyProofAggregation.BuilderOnly
            || blockTransactionsExecutor is not BlockProcessor.IBlockProductionTransactionsExecutor)
        {
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }

        void Aggregate(Block block) => TryAggregate(block, spec, token);
        inner.TransactionsExecuted += Aggregate;
        try
        {
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }
        finally
        {
            inner.TransactionsExecuted -= Aggregate;
        }
    }

    private void TryAggregate(Block block, IReleaseSpec spec, CancellationToken token)
    {
        if (block.DependencyProof is not { Length: > 0 } rawProof || !FrameTxDependencyProof.TryDecode(rawProof, out byte[][] witnesses)) return;

        FrameTxDependencyProof.Dependency[] dependencies = FrameTxDependencyProof.BlockDependencies(block);
        List<AggregatedDependency> aggregated = [];
        List<ReadOnlyMemory<byte>> aggregatedWitnesses = [];
        List<byte[]> raw = [];
        for (int j = 0; j < dependencies.Length; j++)
        {
            if (!AggregatedDependencyProof.IsAggregated(dependencies[j]))
            {
                raw.Add(witnesses[j]);
                continue;
            }

            aggregated.Add(Eip8288Dependencies.Map(dependencies[j].Key, dependencies[j].Msg));
            byte[] witness = new byte[Eip8288Constants.LeanSphincsWitnessBytes];
            dependencies[j].Key.Bytes.CopyTo(witness);
            witnesses[j].CopyTo(witness.AsSpan(ValueHash256.MemorySize));
            aggregatedWitnesses.Add(witness);
        }

        if (aggregated.Count == 0) return;

        // The block's dependencies are sorted by key before message, the guest's by message: reorder both together.
        List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize(aggregated);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(canonical);
        if (!proofCache.TryGetProof(depsHash, out byte[] stark))
        {
            AggregationInput input = Plan(aggregated, aggregatedWitnesses, canonical);
            if (token.CanBeCanceled)
            {
                proofCache.Schedule(canonical, depsHash, input);
                Interlocked.Increment(ref Metrics.LeanRawFallbacks);
                return;
            }

            stark = DependencyProofAggregator.Prove(input, proofCache, depsHash, token);
            proofCache.Remember(canonical, depsHash, stark);
        }

        byte[] proof = AggregatedDependencyProof.Encode(stark, raw);
        if (proof.Length >= rawProof.Length)
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

    /// <summary>Folds every witness, or extends the proven statement sharing the most dependencies, whichever is estimated cheaper.</summary>
    private AggregationInput Plan(List<AggregatedDependency> deps, List<ReadOnlyMemory<byte>> witnesses, List<AggregatedDependency> required)
    {
        AggregationInput best = DependencyProofAggregator.Combine(deps, witnesses, required);
        if (proofCache.BestOverlap(new HashSet<AggregatedDependency>(required)) is { } parent)
        {
            AggregationInput extended = DependencyProofAggregator.Combine(deps, witnesses, required, new RecursiveProofInput(parent.Deps, parent.Proof));
            if (DependencyProofAggregator.EstimatedCost(extended) < DependencyProofAggregator.EstimatedCost(best)) best = extended;
        }

        return best;
    }
}
