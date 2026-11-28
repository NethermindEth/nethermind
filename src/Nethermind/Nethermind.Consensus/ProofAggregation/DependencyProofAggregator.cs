// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>The EIP-8288 "verify, union and discard" aggregation a block builder runs over its block's leanSPHINCS witnesses.</summary>
public static class DependencyProofAggregator
{
    /// <summary>Target serialized-witness size for each direct proving batch, in bytes.</summary>
    public const long MaxProductionWitnessBytes = 4 * 1024 * 1024;

    /// <summary>Recursive inputs folded per step: the pinned guest folds two at a time, so wider sets fold over a binary tree.</summary>
    private const int MaxRecursiveChildren = 2;

    /// <summary>Signatures folded into one direct leaf, so one native call.</summary>
    /// <remarks>A recursive merge costs about nine signatures, so fewer, wider leaves cost less than more, narrower ones; on the
    /// pinned backend sixteen signatures take about as long as one merge, which keeps the longest single native call, and so
    /// the longest wait for the proving gate, at about one merge.</remarks>
    internal const int DirectBatchSize = 16;

    // Relative native proving costs that rank alternative inputs: on the pinned backend a merge of two proofs takes about as
    // long as a leaf of sixteen signatures, so a merge costs well over nine signatures.
    internal const int SignatureCost = 1;
    internal const int MergeCost = 9;

    /// <summary>Estimates the relative native proving cost of an input from its leaves and recursive merges.</summary>
    /// <remarks>Only ranks alternative inputs for the same statement.</remarks>
    public static long EstimatedCost(AggregationInput input)
    {
        int signatures = input.Deps.Count;
        long nodes = (signatures + DirectBatchSize - 1) / DirectBatchSize + input.RecursiveProofs.Count;
        long calls = Math.Max(0, nodes - 1);
        if (input.Discards.Count != 0) calls = Math.Max(calls, 1);
        return (long)signatures * SignatureCost + calls * MergeCost;
    }

    /// <summary>Measures the native aggregation-input encoding, including nested witnesses.</summary>
    public static long InputSize(AggregationInput input)
    {
        long size = 12 + (long)input.Discards.Count * Eip8288Constants.DependencyTripleLength;
        foreach (ReadOnlyMemory<byte> witness in input.Witnesses) size += Eip8288Constants.DependencyTripleLength + 4 + witness.Length;
        foreach (RecursiveProofInput proof in input.RecursiveProofs)
        {
            size += 8 + (long)proof.InnerDeps.Count * Eip8288Constants.DependencyTripleLength + proof.Proof.Length;
        }

        return size;
    }

    /// <summary>Combines raw witnesses with already proven statements, discarding what the statements prove beyond
    /// <paramref name="required"/>, so a changed dependency set is proven by extending earlier ones.</summary>
    /// <param name="deps">The dependencies with raw witnesses, index for index with <paramref name="witnesses"/>.</param>
    /// <param name="witnesses">The raw witnesses, each the public key followed by the signature.</param>
    /// <param name="required">The final dependency set.</param>
    /// <param name="parents">The proven statements to extend, if any.</param>
    /// <exception cref="ArgumentException">A required dependency has neither a witness nor a parent's proof.</exception>
    public static AggregationInput Combine(IReadOnlyList<AggregatedDependency> deps, IReadOnlyList<ReadOnlyMemory<byte>> witnesses,
        IReadOnlyList<AggregatedDependency> required, params ReadOnlySpan<RecursiveProofInput> parents)
    {
        HashSet<AggregatedDependency> wanted = [.. required];
        HashSet<AggregatedDependency> covered = [];
        List<RecursiveProofInput> recursive = [];
        foreach (RecursiveProofInput proven in parents)
        {
            recursive.Add(proven);
            covered.UnionWith(proven.InnerDeps);
        }

        List<AggregatedDependency> direct = [];
        List<ReadOnlyMemory<byte>> directWitnesses = [];
        for (int i = 0; i < deps.Count; i++)
        {
            if (!wanted.Contains(deps[i]) || !covered.Add(deps[i])) continue;
            direct.Add(deps[i]);
            directWitnesses.Add(witnesses[i]);
        }

        if (!wanted.IsSubsetOf(covered))
            throw new ArgumentException("Required dependencies are not covered by the aggregation inputs", nameof(required));

        return new()
        {
            Deps = direct,
            Witnesses = directWitnesses,
            RecursiveProofs = recursive,
            Discards = Eip8288Dependencies.DiscardDependencies(covered, wanted)
        };
    }

    /// <summary>Folds <paramref name="input"/> in bounded native calls into one recursive proof of <paramref name="depsHash"/>.</summary>
    public static byte[] Prove(AggregationInput input, ILeanProofVerifier verifier, in ValueHash256 depsHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Deps.Count != input.Witnesses.Count) throw new ArgumentException("Witness count mismatch", nameof(input));

        if (input.RecursiveProofs.Count <= MaxRecursiveChildren && input.Deps.Count <= DirectBatchSize
            && input.Discards.Count <= Eip8288Constants.MaxProofDependencies
            && (input.Deps.Count <= 1 || DirectInputSize(input.Witnesses) <= MaxProductionWitnessBytes)
            && InputSize(input) <= Eip8288Constants.MaxAggregationInputBytes)
        {
            return verifier.ProveDependencyProof(in depsHash, Eip8288Constants.AggregatedVk, input);
        }

        HashSet<AggregatedDependency> discarded = [.. input.Discards];
        List<RecursiveProofInput> children = [];
        foreach (RecursiveProofInput child in input.RecursiveProofs)
        {
            HashSet<AggregatedDependency> removed = [];
            foreach (AggregatedDependency dependency in child.InnerDeps)
            {
                if (discarded.Contains(dependency)) removed.Add(dependency);
            }

            if (removed.Count == 0) children.Add(child);
            // With every dependency discarded a child proves nothing, and the empty set has no proof to fold.
            else if (!removed.IsSupersetOf(child.InnerDeps)) children.Add(ProveChild(new() { RecursiveProofs = [child] }, child.InnerDeps));
        }

        for (int offset = 0; offset < input.Deps.Count;)
        {
            List<AggregatedDependency> deps = [];
            List<ReadOnlyMemory<byte>> witnesses = [];
            long bytes = 12;
            do
            {
                ReadOnlyMemory<byte> witness = input.Witnesses[offset];
                long entrySize = Eip8288Constants.DependencyTripleLength + 4L + witness.Length;
                if (deps.Count > 0 && (bytes + entrySize > MaxProductionWitnessBytes || deps.Count >= DirectBatchSize)) break;
                deps.Add(input.Deps[offset]);
                witnesses.Add(witness);
                bytes += entrySize;
                offset++;
            } while (offset < input.Deps.Count);

            children.Add(ProveChild(new() { Deps = deps, Witnesses = witnesses }, deps));
        }

        while (children.Count > MaxRecursiveChildren || InputSize(new() { RecursiveProofs = children }) > Eip8288Constants.MaxAggregationInputBytes)
        {
            List<RecursiveProofInput> next = [];
            for (int offset = 0; offset < children.Count;)
            {
                List<RecursiveProofInput> batch = [];
                List<AggregatedDependency> deps = [];
                long bytes = 12;
                do
                {
                    RecursiveProofInput child = children[offset];
                    long entrySize = 8L + (long)child.InnerDeps.Count * Eip8288Constants.DependencyTripleLength + child.Proof.Length;
                    if (batch.Count > 0 && bytes + entrySize > Eip8288Constants.MaxAggregationInputBytes) break;
                    batch.Add(child);
                    deps.AddRange(child.InnerDeps);
                    bytes += entrySize;
                    offset++;
                } while (offset < children.Count && batch.Count < MaxRecursiveChildren);

                next.Add(batch.Count == 1 ? batch[0] : ProveChild(new() { RecursiveProofs = batch }, deps));
            }

            if (next.Count >= children.Count) throw new InvalidOperationException("Recursive inputs cannot fit the native aggregation bound.");
            children = next;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return verifier.ProveDependencyProof(in depsHash, Eip8288Constants.AggregatedVk, new AggregationInput { RecursiveProofs = children });

        RecursiveProofInput ProveChild(AggregationInput childInput, IReadOnlyList<AggregatedDependency> dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<AggregatedDependency> retained = [];
            List<AggregatedDependency> removed = [];
            foreach (AggregatedDependency dependency in dependencies)
            {
                if (discarded.Contains(dependency)) removed.Add(dependency);
                else retained.Add(dependency);
            }

            List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize(retained);
            childInput = new()
            {
                Deps = childInput.Deps,
                Witnesses = childInput.Witnesses,
                RecursiveProofs = childInput.RecursiveProofs,
                Discards = Eip8288Dependencies.Canonicalize(removed)
            };
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(canonical);
            return new(canonical, verifier.ProveDependencyProof(in hash, Eip8288Constants.AggregatedVk, childInput));
        }
    }

    private static long DirectInputSize(IReadOnlyList<ReadOnlyMemory<byte>> witnesses)
    {
        long size = 12;
        foreach (ReadOnlyMemory<byte> witness in witnesses) size += Eip8288Constants.DependencyTripleLength + 4L + witness.Length;
        return size;
    }
}
