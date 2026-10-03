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
    /// <summary>Target serialized-witness size for each direct proving batch, in bytes.</summary>
    public const long MaxProductionWitnessBytes = 4 * 1024 * 1024;
    private const int MaxRecursiveChildren = 2;
    private const int DirectBatchSize = 4;

    /// <summary>Measures the native aggregation-input encoding, including nested witnesses.</summary>
    public static long InputSize(AggregationInput input)
    {
        long size = 12 + (long)input.Discards.Count * Eip8288Constants.DependencyTripleLength;
        foreach (ReadOnlyMemory<byte> witness in input.Witnesses) size += Eip8288Constants.DependencyTripleLength + 4 + witness.Length;
        foreach (RecursiveProofInput proof in input.RecursiveProofs)
        {
            if (proof.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(input));
            size += 8 + (long)proof.InnerDeps.Count * Eip8288Constants.DependencyTripleLength + proof.Proof.Length;
        }
        return size;
    }

    /// <summary>Combines selected witnesses, pruning recursive dependencies absent from the final transaction set.</summary>
    public static AggregationInput Combine(IReadOnlyList<AggregationInput> inputs, IReadOnlyList<FrameDependency> required)
    {
        List<FrameDependency> direct = [];
        List<ReadOnlyMemory<byte>> witnesses = [];
        List<RecursiveProofInput> recursive = [];
        HashSet<FrameDependency> wanted = [.. required];
        HashSet<FrameDependency> discards = [];
        HashSet<FrameDependency> covered = [];
        HashSet<ValueHash256> seenProofs = [];
        foreach (AggregationInput input in inputs)
        {
            foreach (RecursiveProofInput child in input.RecursiveProofs)
            {
                if (child.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(inputs));
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
        if (input.Deps.Count != input.Witnesses.Count) throw new ArgumentException("Witness count mismatch", nameof(input));
        foreach (RecursiveProofInput child in input.RecursiveProofs)
            if (child.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(input));
        if (input.RecursiveProofs.Count <= MaxRecursiveChildren && input.Deps.Count <= DirectBatchSize
            && input.Discards.Count <= Eip8288Constants.MaxProofDependencies
            && InputSize(input) <= Eip8288Constants.MaxAggregationInputBytes)
        {
            byte[] result = verifier.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, input);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        HashSet<FrameDependency> discarded = [.. input.Discards];
        Dictionary<FrameDependency, (int Source, int Bytes)> genericSources = [];
        for (int i = 0; i < input.Deps.Count; i++)
            if (input.Deps[i].Scheme == Eip8288Constants.LeanStarkScheme)
                Consider(input.Deps[i], input.RecursiveProofs.Count + i, input.Witnesses[i].Length);
        for (int i = 0; i < input.RecursiveProofs.Count; i++)
        {
            RecursiveProofInput child = input.RecursiveProofs[i];
            bool generic = false;
            foreach (FrameDependency dependency in child.InnerDeps)
                if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) { generic = true; break; }
            if (!generic) continue;
            Dictionary<FrameDependency, int> lengths = [];
            if (!LeanProofCapacity.TryReadGenericWitnessLengths(child.Proof.Span, lengths))
                throw new ArgumentException("Invalid verified proof envelope", nameof(input));
            foreach ((FrameDependency dependency, int bytes) in lengths) Consider(dependency, i, bytes);
        }
        List<RecursiveProofInput> children = [];
        for (int i = 0; i < input.RecursiveProofs.Count; i++)
        {
            RecursiveProofInput child = input.RecursiveProofs[i];
            HashSet<FrameDependency> removed = [];
            foreach (FrameDependency dependency in child.InnerDeps)
                if (discarded.Contains(dependency)
                    || genericSources.TryGetValue(dependency, out (int Source, int Bytes) source) && source.Source != i)
                    removed.Add(dependency);
            // Verify and prune the original parent before any merge can carry a longer duplicate.
            children.Add(removed.Count != 0
                ? ProveChild(new() { RecursiveProofs = [child] }, child.InnerDeps, removed) : child);
        }
        for (int offset = 0; offset < input.Deps.Count;)
        {
            List<FrameDependency> deps = [];
            List<ReadOnlyMemory<byte>> witnesses = [];
            long bytes = 12;
            do
            {
                FrameDependency dependency = input.Deps[offset];
                if (genericSources.TryGetValue(dependency, out (int Source, int Bytes) source)
                    && source.Source != input.RecursiveProofs.Count + offset)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!verifier.VerifyLeanStark(dependency.DataHash, dependency.VerificationKey, input.Witnesses[offset].Span))
                        throw new InvalidOperationException("Invalid generic STARK dependency witness.");
                    cancellationToken.ThrowIfCancellationRequested();
                    offset++;
                    continue;
                }
                ReadOnlyMemory<byte> witness = input.Witnesses[offset];
                long entrySize = Eip8288Constants.DependencyTripleLength + 4L + witness.Length;
                if (deps.Count > 0 && bytes + entrySize > MaxProductionWitnessBytes) break;
                deps.Add(input.Deps[offset]);
                witnesses.Add(witness);
                bytes += entrySize;
                offset++;
            } while (offset < input.Deps.Count && deps.Count < DirectBatchSize);
            if (deps.Count != 0) children.Add(ProveChild(new() { Deps = deps, Witnesses = witnesses }, deps));
        }
        while (children.Count > MaxRecursiveChildren
            || InputSize(new() { RecursiveProofs = children }) > Eip8288Constants.MaxAggregationInputBytes)
        {
            List<RecursiveProofInput> next = [];
            for (int offset = 0; offset < children.Count;)
            {
                List<RecursiveProofInput> batch = [];
                List<FrameDependency> deps = [];
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
        byte[] proof = verifier.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk,
            new AggregationInput { RecursiveProofs = children });
        cancellationToken.ThrowIfCancellationRequested();
        return proof;

        void Consider(FrameDependency dependency, int source, int bytes)
        {
            if (!genericSources.TryGetValue(dependency, out (int Source, int Bytes) previous) || bytes < previous.Bytes
                || bytes == previous.Bytes && source < input.RecursiveProofs.Count && previous.Source >= input.RecursiveProofs.Count)
                genericSources[dependency] = (source, bytes);
        }

        RecursiveProofInput ProveChild(AggregationInput childInput, IReadOnlyList<FrameDependency> dependencies,
            IReadOnlySet<FrameDependency>? localDiscards = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<FrameDependency> retained = [];
            HashSet<FrameDependency> removed = [];
            foreach (FrameDependency dependency in dependencies)
                if (discarded.Contains(dependency) || localDiscards?.Contains(dependency) == true) removed.Add(dependency);
                else retained.Add(dependency);
            List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(retained);
            childInput = new()
            {
                Deps = childInput.Deps,
                Witnesses = childInput.Witnesses,
                RecursiveProofs = childInput.RecursiveProofs,
                Discards = [.. removed]
            };
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(canonical);
            byte[] childProof = verifier.ProveRecursiveStark(in hash, Eip8288Constants.AggregatedVk, childInput);
            cancellationToken.ThrowIfCancellationRequested();
            return new(canonical, childProof);
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
            ReadOnlyMemory<byte> witness = input.Witnesses[i];
            bool valid = dep.Scheme switch
            {
                Eip8288Constants.LeanSphincsScheme => verifier.VerifyLeanSphincs(dep.DataHash, dep.VerificationKey, witness.Span),
                Eip8288Constants.LeanStarkScheme => verifier.VerifyLeanStark(dep.DataHash, dep.VerificationKey, witness.Span),
                _ => false,
            };
            if (!valid) return false;
            allDeps.Add(dep);
        }

        foreach (RecursiveProofInput recursiveProof in input.RecursiveProofs)
        {
            if (recursiveProof.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(input));
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
