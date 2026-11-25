// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>The "verify, union and discard" logic shared by mempool re-forwarders, FOCIL creators, and block builders.</summary>
public static class DependencyProofAggregator
{
    /// <summary>Target serialized-witness size for each direct proving batch, in bytes.</summary>
    public const long MaxProductionWitnessBytes = 4 * 1024 * 1024;
    // Mempool aggregation folds two recursive inputs per step and the guest discards at most MaxProofDependencies per step, while
    // EIP-8288 bounds discards by the sum of a step's input dependency lists. Wider sets, such as the running wrapper aggregate
    // with every inclusion-list proof, fold over bounded steps of a binary tree, pruning each child separately; only the slot
    // deadline bounds the rounds.
    private const int MaxRecursiveChildren = 2;
    /// <summary>Recursive inputs one block-production merge folds together.</summary>
    /// <remarks>The pinned guest takes up to <see cref="Eip8288Constants.MaxRecursiveInputsPerStep"/>. Merging sixteen proofs
    /// of sixteen signatures on 8 arm64 vCPUs took 5.7 s pairwise (15 calls), 3.4 s four at a time, 3.0 s eight at a time and
    /// 2.6 s sixteen at once, while the prover's peak memory, which its allocator keeps for the life of the process, rose
    /// from 1.2 to 2.3, 3.4 and 4.9 GiB. Eight takes most of that gain at about the 3.1 GiB a whole
    /// <see cref="BlockProductionBatchSize"/> leaf already needs.</remarks>
    internal const int BlockProductionFanIn = 8;
    /// <summary>Fresh signatures one block-production direct leaf takes at most: the native step bound.</summary>
    /// <remarks>A block producer proves its statement itself, so no other producer waits behind its longest call, which
    /// <see cref="DirectBatchSize"/> bounds for mempool aggregation. Its signatures are split into the fewest equal leaves
    /// within this bound: on 8 arm64 vCPUs 200 signatures took 1.2 s in one call where 13 leaves of 16 and their merges took
    /// 6.3 s, and 500 took 3.1 s in three calls against 16.4 s in 63.</remarks>
    internal const int BlockProductionBatchSize = Eip8288Constants.MaxRawWitnessesPerStep;
    /// <summary>Fresh signatures folded into one direct leaf, so one native call.</summary>
    /// <remarks>A recursive merge costs about nine signatures, so fewer, wider leaves cost less than more, narrower ones.
    /// Four came from the former in-tree BLAKE2s-to-Keccak backend, whose raw proving witness made wide direct batches
    /// expensive in memory, and was documented there as "the conservative default for smaller CI hosts", not as a bound.
    /// The packaged backend takes up to 256 raw witnesses per call, and <c>MAX_LEANSIG_DEPS_PER_WRAPPER</c> is the
    /// largest fresh-signature set a produced mode-0 wrapper may carry, so one signature-only leaf now covers a whole
    /// wrapper. The batch loop takes whatever is available and never waits to fill a leaf, and
    /// <see cref="MaxProductionWitnessBytes"/> still caps a leaf's serialized witnesses.</remarks>
    internal const int DirectBatchSize = Eip8288Constants.MaxLeanSigDepsPerWrapper;
    /// <summary>Dependencies folded into one direct leaf that holds any leanSTARK dependency.</summary>
    /// <remarks>A native call cannot be interrupted, and a background fold yields to a waiting producer only between calls,
    /// so the longest single call bounds a producer's wait. On the F128 backend (ABI 21) a leaf of four leanSTARK witnesses
    /// takes about 334 ms (808 ms on four cores), the longest call four-wide leaves could issue, while sixteen signatures
    /// plus four leanSTARK witnesses take about 428 ms (971 ms). Keeping any leaf with a leanSTARK dependency four wide
    /// keeps the longest call where it was; signature-only leaves, at about 197 ms for sixteen, stay below it. A leanSTARK
    /// witness is about 163 KB against a signature's 6 KB, and the prover's peak memory tracks a leaf's witness bytes, which
    /// this bounds too.</remarks>
    internal const int GenericDirectBatchSize = 4;

    /// <summary>Runs the STARK check of a block proof against its dependency list.</summary>
    /// <remarks>An empty dependency list carries an empty <c>stark_proof</c> and invokes no verifier; any other list needs
    /// a proof within <see cref="Eip8288Constants.MaxAggregateProofBytes"/>, checked before the verifier runs, that verifies
    /// against <paramref name="depsHash"/> and the block fork's <c>AGGREGATED_VK</c>. Wrappers and inclusion lists never use the
    /// empty form: their dependency sets are nonempty.</remarks>
    public static bool CheckDependencyProof(ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, int dependencyCount, in ValueHash256 depsHash,
        ReadOnlySpan<byte> proof)
        => dependencyCount == 0
            ? proof.IsEmpty
            : proof.Length is > 0 and <= Eip8288Constants.MaxAggregateProofBytes
                && verifier.VerifyDependencyProof(in depsHash, forkVerifier.AggregatedVk.Bytes, proof);

    // Relative native proving costs that rank alternative inputs. First measured on a two-core devnet node with the in-tree
    // backend (about 2.2 s per signature and 20 s per merge); on the F128 backend a merge of two proofs takes about 194 ms
    // and a leaf of sixteen signatures about 197 ms, so a merge still costs well over nine signatures and the ranking holds.
    internal const int SignatureCost = 1;
    internal const int GenericLeafCost = 2;
    internal const int MergeCost = 9;

    /// <summary>Estimates the relative native proving cost of an input from its leaves and recursive merges.</summary>
    /// <remarks>Signatures fold <see cref="DirectBatchSize"/> to a leaf, or <see cref="BlockProductionBatchSize"/> for a block
    /// producer, each generic proof counts as its own leaf, and every node beyond the first needs one recursive merge; a
    /// recursive child losing dependencies needs a call of its own unless the whole input fits one. Generics are counted one
    /// per leaf although the batch loop folds up to <see cref="GenericDirectBatchSize"/> of them together, and a block producer's
    /// <see cref="BlockProductionFanIn"/>-way merges are counted as pairwise ones, so both are over-stated; the estimate only
    /// ranks alternative inputs for the same statement, which that does not disturb.</remarks>
    /// <param name="input">The input to estimate.</param>
    /// <param name="blockProduction">Whether to estimate the schedule <see cref="Prove"/> uses for a block producer.</param>
    public static long EstimatedCost(AggregationInput input, bool blockProduction = false)
    {
        int signatureLeaf = blockProduction ? BlockProductionBatchSize : DirectBatchSize;
        int signatures = 0, generics = 0;
        foreach (FrameDependency dependency in input.Deps)
            if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) generics++;
            else signatures++;
        long nodes = (signatures + signatureLeaf - 1) / signatureLeaf + generics + input.RecursiveProofs.Count;
        long calls = Math.Max(0, nodes - 1);
        if (input.Discards.Count != 0)
        {
            if (input.RecursiveProofs.Count <= (blockProduction ? BlockProductionFanIn : MaxRecursiveChildren)
                && input.Deps.Count <= LeafCapacity(generics, signatureLeaf))
                calls = Math.Max(calls, 1);
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
        if (!wanted.IsSubsetOf(covered))
            throw new ArgumentException("Required dependencies are not covered by the aggregation inputs", nameof(required));
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

    /// <summary>Authenticates the direct witnesses of dependencies a recursive child already proves and drops them.</summary>
    private static AggregationInput WithoutCoveredDeps(AggregationInput input, ILeanProofVerifier verifier, CancellationToken cancellationToken)
    {
        if (input.RecursiveProofs.Count == 0 || input.Deps.Count == 0) return input;
        HashSet<FrameDependency> covered = [];
        foreach (RecursiveProofInput child in input.RecursiveProofs) covered.UnionWith(child.InnerDeps);
        List<FrameDependency>? deps = null;
        List<ReadOnlyMemory<byte>>? witnesses = null;
        for (int i = 0; i < input.Deps.Count; i++)
        {
            FrameDependency dependency = input.Deps[i];
            if (!covered.Contains(dependency))
            {
                deps?.Add(dependency);
                witnesses?.Add(input.Witnesses[i]);
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            bool valid = dependency.Scheme switch
            {
                Eip8288Constants.LeanSphincsScheme => verifier.VerifyLeanSphincs(dependency.DataHash, dependency.VerificationKey, input.Witnesses[i].Span),
                Eip8288Constants.LeanStarkScheme => verifier.VerifyLeanStark(dependency.DataHash, dependency.VerificationKey, input.Witnesses[i].Span),
                _ => false
            };
            if (!valid) throw new InvalidOperationException("Invalid direct dependency witness.");
            if (deps is null)
            {
                deps = new List<FrameDependency>(input.Deps.Count);
                witnesses = new List<ReadOnlyMemory<byte>>(input.Witnesses.Count);
                for (int j = 0; j < i; j++)
                {
                    deps.Add(input.Deps[j]);
                    witnesses.Add(input.Witnesses[j]);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return deps is null
            ? input
            : new() { Deps = deps, Witnesses = witnesses!, RecursiveProofs = input.RecursiveProofs, Discards = input.Discards };
    }

    /// <summary>The widest direct leaf holding <paramref name="generics"/> leanSTARK dependencies.</summary>
    private static int LeafCapacity(int generics, int signatureLeaf = DirectBatchSize) => generics == 0 ? signatureLeaf : GenericDirectBatchSize;

    /// <summary>Orders direct dependencies signatures first and sizes signature leaves to the fewest equal ones within
    /// <see cref="BlockProductionBatchSize"/>.</summary>
    private static (int[] Order, int SignatureLeaf) EqualSignatureLeaves(IReadOnlyList<FrameDependency> deps)
    {
        int[] order = new int[deps.Count];
        int signatures = 0;
        for (int i = 0; i < deps.Count; i++)
            if (deps[i].Scheme != Eip8288Constants.LeanStarkScheme) order[signatures++] = i;
        int next = signatures;
        for (int i = 0; i < deps.Count; i++)
            if (deps[i].Scheme == Eip8288Constants.LeanStarkScheme) order[next++] = i;
        int leaves = Math.Max(1, (signatures + BlockProductionBatchSize - 1) / BlockProductionBatchSize);
        return (order, Math.Max(1, (signatures + leaves - 1) / leaves));
    }

    /// <summary>The leanSTARK dependencies among <paramref name="deps"/>.</summary>
    private static int GenericCount(IReadOnlyList<FrameDependency> deps)
    {
        int generics = 0;
        foreach (FrameDependency dependency in deps)
            if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) generics++;
        return generics;
    }

    /// <summary>Folds inputs in bounded batches before generating the final recursive proof.</summary>
    /// <remarks>Every step proves and verifies under <paramref name="forkVerifier"/>'s <c>AGGREGATED_VK</c>. The schedule changes
    /// only how many native calls fold the input, not the statement proven.</remarks>
    /// <param name="input">The witnesses, recursive proofs and discards to fold.</param>
    /// <param name="verifier">The prover of each step.</param>
    /// <param name="forkVerifier">The fork's <c>AGGREGATED_VK</c>.</param>
    /// <param name="depsHash">The dependency digest of the statement proven.</param>
    /// <param name="cancellationToken">Stops the fold between native calls.</param>
    /// <param name="blockProduction">Whether a block producer proves its own statement: signatures fold in the fewest equal
    /// leaves of up to <see cref="BlockProductionBatchSize"/> and recursive inputs merge <see cref="BlockProductionFanIn"/> at a
    /// time, instead of the <see cref="DirectBatchSize"/> leaves and pairwise merges that keep each mempool aggregation call short.</param>
    public static byte[] Prove(AggregationInput input, ILeanProofVerifier verifier, Eip8288Verifier forkVerifier, in ValueHash256 depsHash,
        CancellationToken cancellationToken = default, bool blockProduction = false)
    {
        int fanIn = blockProduction ? BlockProductionFanIn : MaxRecursiveChildren;
        byte[] aggregatedVk = forkVerifier.AggregatedVk.ToByteArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Deps.Count != input.Witnesses.Count) throw new ArgumentException("Witness count mismatch", nameof(input));
        foreach (RecursiveProofInput child in input.RecursiveProofs)
            if (child.InnerDeps is null) throw new ArgumentException("Uninitialized recursive proof input", nameof(input));
        // A direct dependency a child already proves is authenticated and left out first, so neither the reuse of an unchanged
        // child below nor the one-call limits see its witness.
        input = WithoutCoveredDeps(input, verifier, cancellationToken);
        if (input.Deps.Count == 0 && input.RecursiveProofs.Count == 1 && input.Discards.Count == 0)
        {
            RecursiveProofInput parent = input.RecursiveProofs[0];
            if (parent.InnerDeps.Count <= Eip8288Constants.MaxProofDependencies
                && Eip8288Dependencies.ComputeDepsHash(parent.InnerDeps) == depsHash)
            {
                // The same verified statement needs no new recursive guest execution.
                cancellationToken.ThrowIfCancellationRequested();
                if (parent.Proof.Length is 0 or > Eip8288Constants.MaxAggregateProofBytes
                    || !verifier.VerifyDependencyProof(in depsHash, aggregatedVk, parent.Proof.Span))
                    throw new InvalidOperationException("Invalid recursive dependency proof.");
                cancellationToken.ThrowIfCancellationRequested();
                return parent.Proof.ToArray();
            }
        }
        if (input.RecursiveProofs.Count <= fanIn
            && input.Deps.Count <= LeafCapacity(GenericCount(input.Deps), blockProduction ? BlockProductionBatchSize : DirectBatchSize)
            && input.Discards.Count <= Eip8288Constants.MaxProofDependencies
            && (input.Deps.Count <= 1 || DirectInputSize(input.Witnesses) <= MaxProductionWitnessBytes)
            && InputSize(input) <= Eip8288Constants.MaxAggregationInputBytes)
        {
            byte[] result = DependencyProofCache.Prove(verifier, in depsHash, aggregatedVk, input, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        HashSet<FrameDependency> discarded = [.. input.Discards];
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
        int signatureLeaf = DirectBatchSize;
        int[]? order = null;
        if (blockProduction) (order, signatureLeaf) = EqualSignatureLeaves(input.Deps);
        for (int offset = 0; offset < input.Deps.Count;)
        {
            List<FrameDependency> deps = [];
            List<ReadOnlyMemory<byte>> witnesses = [];
            long bytes = 12;
            int generics = 0;
            do
            {
                int index = order?[offset] ?? offset;
                FrameDependency dependency = input.Deps[index];
                ReadOnlyMemory<byte> witness = input.Witnesses[index];
                long entrySize = Eip8288Constants.DependencyTripleLength + 4L + witness.Length;
                if (deps.Count > 0 && bytes + entrySize > MaxProductionWitnessBytes) break;
                int withThis = generics + (dependency.Scheme == Eip8288Constants.LeanStarkScheme ? 1 : 0);
                if (deps.Count > 0 && deps.Count >= LeafCapacity(withThis, signatureLeaf)) break;
                generics = withThis;
                deps.Add(dependency);
                witnesses.Add(witness);
                bytes += entrySize;
                offset++;
            } while (offset < input.Deps.Count);
            if (deps.Count != 0) children.Add(ProveChild(new() { Deps = deps, Witnesses = witnesses }, deps));
        }
        while (children.Count > fanIn
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
                } while (offset < children.Count && batch.Count < fanIn);
                next.Add(batch.Count == 1 ? batch[0] : ProveChild(new() { RecursiveProofs = batch }, deps));
            }
            if (next.Count >= children.Count) throw new InvalidOperationException("Recursive inputs cannot fit the native aggregation bound.");
            children = next;
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] proof = DependencyProofCache.Prove(verifier, in depsHash, aggregatedVk,
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
            byte[] childProof = DependencyProofCache.Prove(verifier, in hash, aggregatedVk, childInput, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(canonical, childProof);
        }
    }
}
