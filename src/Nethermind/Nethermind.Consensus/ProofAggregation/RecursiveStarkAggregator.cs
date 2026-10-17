// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>The "verify, union and discard" logic shared by mempool re-forwarders, FOCIL creators, and block builders.</summary>
public static class RecursiveStarkAggregator
{
    /// <summary>Target serialized-witness size for each direct proving batch, in bytes.</summary>
    public const long MaxProductionWitnessBytes = 4 * 1024 * 1024;
    // The pinned prototype guest folds two recursive inputs per step and discards at most MaxProofDependencies per step, while
    // EIP-8288 bounds discards by the sum of a step's input dependency lists. Wider sets, such as the running wrapper aggregate
    // with every inclusion-list proof, fold over bounded steps of a binary tree, pruning each child separately; only the slot
    // deadline bounds the rounds.
    private const int MaxRecursiveChildren = 2;
    private const int DirectBatchSize = 4;

    /// <summary>Runs the STARK check of a block proof against its dependency list.</summary>
    /// <remarks>An empty dependency list carries an empty <c>stark_proof</c> and invokes no verifier; any other list needs
    /// a proof within <see cref="Eip8288Constants.MaxAggregateProofBytes"/>, checked before the verifier runs, that verifies
    /// against <paramref name="depsHash"/> and the block fork's <c>AGGREGATED_VK</c>. Wrappers and inclusion lists never use the
    /// empty form: their dependency sets are nonempty.</remarks>
    public static bool VerifyStarkCheck(ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, int dependencyCount, in ValueHash256 depsHash,
        ReadOnlySpan<byte> proof)
        => dependencyCount == 0
            ? proof.IsEmpty
            : proof.Length is > 0 and <= Eip8288Constants.MaxAggregateProofBytes
                && verifier.VerifyRecursiveStark(in depsHash, forkVerifier.AggregatedVk.Bytes, proof);

    // Relative native proving costs measured on a two-core devnet node: folding one leanSPHINCS signature into a leaf
    // costs about 2.2 s and each recursive merge of two proofs about 20 s, so a merge costs about nine signatures.
    private const int SignatureCost = 1;
    private const int GenericLeafCost = 2;
    private const int MergeCost = 9;

    /// <summary>Estimates the relative native proving cost of an input from its leaves and recursive merges.</summary>
    /// <remarks>Signatures fold four to a leaf, each generic proof is its own leaf, and every node beyond the first
    /// needs one recursive merge; a recursive child losing dependencies needs a call of its own unless the whole input
    /// fits one. The estimate only ranks alternative inputs for the same statement.</remarks>
    public static long EstimatedCost(AggregationInput input)
    {
        int signatures = 0, generics = 0;
        foreach (FrameDependency dependency in input.Deps)
            if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) generics++;
            else signatures++;
        long nodes = (signatures + DirectBatchSize - 1) / DirectBatchSize + generics + input.RecursiveProofs.Count;
        long calls = Math.Max(0, nodes - 1);
        if (input.Discards.Count != 0)
        {
            if (input.RecursiveProofs.Count <= MaxRecursiveChildren && input.Deps.Count <= DirectBatchSize) calls = Math.Max(calls, 1);
            else
            {
                HashSet<FrameDependency> discards = [.. input.Discards];
                foreach (RecursiveProofInput child in input.RecursiveProofs)
                    foreach (FrameDependency dependency in child.InnerDeps)
                        if (discards.Contains(dependency))
                        {
                            calls++;
                            break;
                        }
            }
        }
        return (long)signatures * SignatureCost + (long)generics * GenericLeafCost + calls * MergeCost;
    }

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

    private static long DirectInputSize(IReadOnlyList<ReadOnlyMemory<byte>> witnesses)
    {
        long size = 12;
        foreach (ReadOnlyMemory<byte> witness in witnesses)
            size += Eip8288Constants.DependencyTripleLength + 4L + witness.Length;
        return size;
    }

    /// <summary>Combines selected witnesses, pruning recursive dependencies absent from the final transaction set.</summary>
    /// <param name="inputs">The selected witnesses and recursive proofs.</param>
    /// <param name="required">The final dependency set.</param>
    /// <param name="parent">
    /// An already proven statement; selected inputs it fully covers are left out and its dependencies outside
    /// <paramref name="required"/> are discarded, so a changing set is proven by extending a previous statement instead of
    /// re-folding every witness.
    /// </param>
    public static AggregationInput Combine(IReadOnlyList<AggregationInput> inputs, IReadOnlyList<FrameDependency> required,
        RecursiveProofInput? parent = null)
    {
        List<FrameDependency> direct = [];
        List<ReadOnlyMemory<byte>> witnesses = [];
        List<RecursiveProofInput> recursive = [];
        HashSet<FrameDependency> wanted = [.. required];
        HashSet<FrameDependency> covered = [];
        HashSet<ValueHash256> seenProofs = [];
        if (parent is { } proven)
        {
            if (proven.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(parent));
            seenProofs.Add(proven.ProofHash);
            recursive.Add(proven);
            covered.UnionWith(proven.InnerDeps);
        }
        foreach (AggregationInput input in inputs)
        {
            foreach (RecursiveProofInput child in input.RecursiveProofs)
            {
                if (child.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(inputs));
                if (parent is not null && IsCovered(child.InnerDeps, covered) || !Overlaps(child.InnerDeps, wanted)) continue;
                if (!seenProofs.Add(child.ProofHash)) continue;
                recursive.Add(child);
                covered.UnionWith(child.InnerDeps);
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
        // A required dependency no input covers is left to the statement check of the proof built from this input.
        wanted.IntersectWith(covered);
        return new()
        {
            Deps = direct,
            Witnesses = witnesses,
            RecursiveProofs = recursive,
            Discards = Eip8288Dependencies.DiscardDependencies(covered, wanted)
        };

        static bool IsCovered(IReadOnlyList<FrameDependency> dependencies, HashSet<FrameDependency> covered)
        {
            foreach (FrameDependency dependency in dependencies)
                if (!covered.Contains(dependency)) return false;
            return true;
        }

        // A child proving nothing the statement needs would only be discarded whole.
        static bool Overlaps(IReadOnlyList<FrameDependency> dependencies, HashSet<FrameDependency> wanted)
        {
            foreach (FrameDependency dependency in dependencies)
                if (wanted.Contains(dependency)) return true;
            return false;
        }
    }

    /// <summary>Folds inputs in bounded batches before generating the final recursive proof.</summary>
    /// <remarks>Every step proves and verifies under <paramref name="forkVerifier"/>'s <c>AGGREGATED_VK</c>.</remarks>
    public static byte[] Prove(AggregationInput input, ILeanProofVerifier verifier, Eip8288Verifier forkVerifier, in ValueHash256 depsHash,
        CancellationToken cancellationToken = default)
    {
        byte[] aggregatedVk = forkVerifier.AggregatedVk.ToByteArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Deps.Count != input.Witnesses.Count) throw new ArgumentException("Witness count mismatch", nameof(input));
        foreach (RecursiveProofInput child in input.RecursiveProofs)
            if (child.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(input));
        if (input.Deps.Count == 0 && input.RecursiveProofs.Count == 1 && input.Discards.Count == 0)
        {
            RecursiveProofInput parent = input.RecursiveProofs[0];
            if (parent.InnerDeps.Count <= Eip8288Constants.MaxProofDependencies
                && Eip8288Dependencies.ComputeDepsHash(parent.InnerDeps) == depsHash)
            {
                // The same verified statement needs no new recursive guest execution.
                cancellationToken.ThrowIfCancellationRequested();
                if (parent.Proof.Length is 0 or > Eip8288Constants.MaxAggregateProofBytes
                    || !verifier.VerifyRecursiveStark(in depsHash, aggregatedVk, parent.Proof.Span))
                    throw new InvalidOperationException("Invalid recursive dependency proof.");
                cancellationToken.ThrowIfCancellationRequested();
                return parent.Proof.ToArray();
            }
        }
        if (input.RecursiveProofs.Count <= MaxRecursiveChildren && input.Deps.Count <= DirectBatchSize
            && input.Discards.Count <= Eip8288Constants.MaxProofDependencies
            && (input.Deps.Count <= 1 || DirectInputSize(input.Witnesses) <= MaxProductionWitnessBytes)
            && InputSize(input) <= Eip8288Constants.MaxAggregationInputBytes)
        {
            byte[] result = ProductionProofCache.Prove(verifier, in depsHash, aggregatedVk, input, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        HashSet<FrameDependency> discarded = [.. input.Discards];
        HashSet<FrameDependency> recursiveCoverage = [];
        foreach (RecursiveProofInput child in input.RecursiveProofs) recursiveCoverage.UnionWith(child.InnerDeps);
        List<RecursiveProofInput> children = [];
        for (int i = 0; i < input.RecursiveProofs.Count; i++)
        {
            RecursiveProofInput child = input.RecursiveProofs[i];
            HashSet<FrameDependency> removed = [];
            foreach (FrameDependency dependency in child.InnerDeps)
                if (discarded.Contains(dependency)) removed.Add(dependency);
            if (removed.Count == 0) children.Add(child);
            // With every dependency discarded a child proves nothing, and the empty set has no proof to fold.
            else if (!removed.IsSupersetOf(child.InnerDeps))
                children.Add(ProveChild(new() { RecursiveProofs = [child] }, child.InnerDeps, removed));
        }
        for (int offset = 0; offset < input.Deps.Count;)
        {
            List<FrameDependency> deps = [];
            List<ReadOnlyMemory<byte>> witnesses = [];
            long bytes = 12;
            do
            {
                FrameDependency dependency = input.Deps[offset];
                if (recursiveCoverage.Contains(dependency))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool valid = dependency.Scheme switch
                    {
                        Eip8288Constants.LeanSphincsScheme => verifier.VerifyLeanSphincs(dependency.DataHash, dependency.VerificationKey, input.Witnesses[offset].Span),
                        Eip8288Constants.LeanStarkScheme => verifier.VerifyLeanStark(dependency.DataHash, dependency.VerificationKey, input.Witnesses[offset].Span),
                        _ => false
                    };
                    if (!valid) throw new InvalidOperationException("Invalid direct dependency witness.");
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
        byte[] proof = ProductionProofCache.Prove(verifier, in depsHash, aggregatedVk,
            new AggregationInput { RecursiveProofs = children }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return proof;

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
            byte[] childProof = ProductionProofCache.Prove(verifier, in hash, aggregatedVk, childInput, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(canonical, childProof);
        }
    }

    /// <summary>
    /// Runs the EIP-8288 recursive STARK statement: verify direct dependencies against their witnesses
    /// and each nested proof against its claimed deps, then union and remove discards/duplicates,
    /// returning the filtered set and its commitment.
    /// </summary>
    public static bool TryAggregate(AggregationInput input, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier,
        out IReadOnlyList<FrameDependency> filteredDeps, out ValueHash256 depsHash)
    {
        filteredDeps = [];
        depsHash = default;

        if (input.Deps.Count != input.Witnesses.Count) return false;
        // The circuit checks direct dependencies against ENABLED_SCHEMES; a verified recursive input can prove only enabled
        // schemes, so checking its list as well rejects nothing a valid input carries.
        if (!Eip8288Dependencies.AreSchemesEnabled(input.Deps)) return false;
        foreach (RecursiveProofInput recursiveProof in input.RecursiveProofs)
        {
            if (recursiveProof.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(input));
            if (!Eip8288Dependencies.AreSchemesEnabled(recursiveProof.InnerDeps)) return false;
        }

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
            // A recursive input above MAX_AGGREGATE_PROOF_BYTES is rejected before its verifier runs.
            if (recursiveProof.Proof.Length is 0 or > Eip8288Constants.MaxAggregateProofBytes) return false;
            ValueHash256 innerHash = Eip8288Dependencies.ComputeDepsHash(recursiveProof.InnerDeps);
            if (!verifier.VerifyRecursiveStark(in innerHash, forkVerifier.AggregatedVk.Bytes, recursiveProof.Proof.Span)) return false;
            allDeps.AddRange(recursiveProof.InnerDeps);
        }

        HashSet<FrameDependency> discards = input.Discards.Count == 0 ? [] : [.. input.Discards];
        // EIP-8288: every discard must occur in the verified input union, matched by its full triple.
        if (discards.Count != 0)
        {
            HashSet<FrameDependency> inputs = [.. allDeps];
            if (!discards.IsSubsetOf(inputs)) return false;
        }

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
