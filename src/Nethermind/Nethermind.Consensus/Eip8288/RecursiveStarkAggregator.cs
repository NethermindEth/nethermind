// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Eip8288;

/// <summary>The "verify, union and discard" logic shared by mempool re-forwarders, FOCIL creators, and block builders.</summary>
public static class RecursiveStarkAggregator
{
    /// <summary>Conservative block-production budget for serialized witnesses, in bytes.</summary>
    public const long MaxProductionWitnessBytes = 4 * 1024 * 1024;
    private const int MaxRecursiveChildren = 16;
    private const int DirectBatchSize = 512;

    /// <summary>Measures the native aggregation-input encoding, including nested witnesses.</summary>
    public static long InputSize(AggregationInput input)
    {
        long size = 12 + (long)input.Discards.Count * Eip8288Constants.DependencyTripleLength;
        foreach (byte[] witness in input.Witnesses) size += Eip8288Constants.DependencyTripleLength + 4 + witness.Length;
        foreach (RecursiveProofInput proof in input.RecursiveProofs)
            size += 8 + (long)proof.InnerDeps.Count * Eip8288Constants.DependencyTripleLength + proof.Proof.Length;
        return size;
    }

    /// <summary>Combines selected witnesses, pruning recursive dependencies absent from the final transaction set.</summary>
    public static AggregationInput Combine(IReadOnlyList<AggregationInput> inputs, IReadOnlyList<FrameDependency> required)
    {
        List<FrameDependency> direct = [];
        List<byte[]> witnesses = [];
        List<RecursiveProofInput> recursive = [];
        HashSet<FrameDependency> wanted = [.. required];
        HashSet<FrameDependency> discards = [];
        HashSet<FrameDependency> covered = [];
        HashSet<ValueHash256> seenProofs = [];
        foreach (AggregationInput input in inputs)
        {
            foreach (RecursiveProofInput child in input.RecursiveProofs)
            {
                if (!seenProofs.Add(child.ProofHash)) continue;
                recursive.Add(child);
                foreach (FrameDependency dep in child.InnerDeps)
                {
                    covered.Add(dep);
                    if (!wanted.Contains(dep)) discards.Add(dep);
                }
            }
        }
        foreach (AggregationInput input in inputs)
        {
            for (int i = 0; i < input.Deps.Count; i++)
            {
                FrameDependency dep = input.Deps[i];
                if (!wanted.Contains(dep) || !covered.Add(dep)) continue;
                direct.Add(dep);
                witnesses.Add(input.Witnesses[i]);
            }
        }
        return new() { Deps = direct, Witnesses = witnesses, RecursiveProofs = recursive, Discards = [.. discards] };
    }

    /// <summary>Folds inputs in bounded batches before generating the final recursive proof.</summary>
    public static byte[] Prove(AggregationInput input, ILeanProofVerifier verifier, in ValueHash256 depsHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.RecursiveProofs.Count <= MaxRecursiveChildren && input.Deps.Count <= DirectBatchSize)
            return verifier.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, input);

        List<RecursiveProofInput> children = [.. input.RecursiveProofs];
        for (int offset = 0; offset < input.Deps.Count;)
        {
            List<FrameDependency> deps = [];
            List<byte[]> witnesses = [];
            long bytes = 12;
            do
            {
                byte[] witness = input.Witnesses[offset];
                long entrySize = Eip8288Constants.DependencyTripleLength + 4L + witness.Length;
                if (deps.Count > 0 && bytes + entrySize > MaxProductionWitnessBytes) break;
                deps.Add(input.Deps[offset]);
                witnesses.Add(witness);
                bytes += entrySize;
                offset++;
            } while (offset < input.Deps.Count && deps.Count < DirectBatchSize);
            children.Add(ProveChild(new() { Deps = deps, Witnesses = witnesses }, deps));
        }
        while (children.Count > MaxRecursiveChildren)
        {
            List<RecursiveProofInput> next = [];
            for (int offset = 0; offset < children.Count; offset += MaxRecursiveChildren)
            {
                List<RecursiveProofInput> batch = children.GetRange(offset, Math.Min(MaxRecursiveChildren, children.Count - offset));
                List<FrameDependency> deps = [];
                foreach (RecursiveProofInput child in batch) deps.AddRange(child.InnerDeps);
                next.Add(ProveChild(new() { RecursiveProofs = batch }, deps));
            }
            children = next;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return verifier.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk,
            new AggregationInput { RecursiveProofs = children, Discards = input.Discards });

        RecursiveProofInput ProveChild(AggregationInput childInput, IReadOnlyList<FrameDependency> dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(dependencies);
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(canonical);
            byte[] proof = verifier.ProveRecursiveStark(in hash, Eip8288Constants.AggregatedVk, childInput);
            return new(canonical, proof);
        }
    }

    /// <summary>
    /// Runs the EIP-8288 recursive STARK statement: verify direct dependencies against their witnesses
    /// and each nested proof against its claimed deps, then union and remove discards/duplicates,
    /// returning the filtered set and its commitment.
    /// </summary>
    public static bool TryAggregate(AggregationInput input, ILeanProofVerifier verifier, out IReadOnlyList<FrameDependency> filteredDeps, out ValueHash256 depsHash)
    {
        filteredDeps = [];
        depsHash = default;

        if (input.Deps.Count != input.Witnesses.Count) return false;

        List<FrameDependency> allDeps = [];
        for (int i = 0; i < input.Deps.Count; i++)
        {
            FrameDependency dep = input.Deps[i];
            byte[] witness = input.Witnesses[i];
            bool valid = dep.Scheme switch
            {
                Eip8288Constants.LeanSphincsScheme => verifier.VerifyLeanSphincs(dep.DataHash, dep.VerificationKey, witness),
                Eip8288Constants.LeanStarkScheme => verifier.VerifyLeanStark(dep.DataHash, dep.VerificationKey, witness),
                _ => false,
            };
            if (!valid) return false;
            allDeps.Add(dep);
        }

        foreach (RecursiveProofInput recursiveProof in input.RecursiveProofs)
        {
            ValueHash256 innerHash = Eip8288Dependencies.ComputeDepsHash(recursiveProof.InnerDeps);
            if (!verifier.VerifyRecursiveStark(in innerHash, Eip8288Constants.AggregatedVk, recursiveProof.Proof.Span)) return false;
            allDeps.AddRange(recursiveProof.InnerDeps);
        }

        HashSet<FrameDependency> discards = input.Discards.Count == 0 ? [] : [.. input.Discards];

        List<FrameDependency> filtered = [];
        HashSet<FrameDependency> seen = [];
        foreach (FrameDependency dep in allDeps)
        {
            if (!seen.Add(dep)) continue;
            if (discards.Contains(dep)) continue;
            filtered.Add(dep);
        }

        filteredDeps = Eip8288Dependencies.Canonicalize(filtered);
        depsHash = Eip8288Dependencies.ComputeDepsHash(filteredDeps);
        return true;
    }
}
