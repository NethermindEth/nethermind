// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Plans the EIP-8288 proof of a block being produced when the proof of its own body is not ready.</summary>
internal static class DependencyProofPlanner
{
    private const int MaxExtensionCandidates = 4;

    /// <summary>Estimated cost (<see cref="DependencyProofAggregator.EstimatedCost"/>) above which a scheduled statement is cut to
    /// a nonce-ordered prefix of the body: two recursive merges and one whole direct leaf.</summary>
    /// <remarks>Kept to that shape, not to its former value, as the producer's direct leaf widened to
    /// <see cref="DependencyProofAggregator.BlockProductionBatchSize"/>: one wide leaf costs far less than the narrow leaves and
    /// merges the same signatures took. A full statement took about 3 s on 8 arm64 vCPUs and scales inversely with cores; one
    /// made mostly of recursive inputs is over-counted, so it is cut sooner rather than later.</remarks>
    internal const long MaxScheduledStatementCost = 2 * DependencyProofAggregator.MergeCost
        + DependencyProofAggregator.BlockProductionBatchSize * DependencyProofAggregator.SignatureCost;

    /// <summary>Offers the verified recursive proofs of the inclusion lists as reusable statements.</summary>
    /// <remarks>
    /// A rebuild under a deadline keeps only transactions whose dependencies form a proven statement. A verified list proof is one,
    /// so the rebuild can keep the list's transactions, which attesters still obligate, and reuse that proof unchanged.
    /// </remarks>
    internal static void OfferInclusionListProofs(BlockToProduce producing, LeanProofStore store)
    {
        if (producing.LeanProduction.InclusionListProofInput is not { } inclusionList) return;
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
    internal static void ScheduleProductionProof(BlockToProduce producing, IncludableTransactions includable, HashSet<FrameDependency> attempted,
        List<FrameDependency> deps, in ValueHash256 depsHash, HashSet<FrameDependency> provenLimit, int provenKept, LeanProofStore store,
        Eip8288Verifier verifier, DependencyProofCache cache)
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
            AggregationInput narrowing = new() { RecursiveProofs = [narrowed], Discards = discards };
            if (DependencyProofAggregator.EstimatedCost(narrowing, blockProduction: true) <= MaxScheduledStatementCost)
            {
                cache.Schedule(narrowedTarget, Eip8288Dependencies.ComputeDepsHash(narrowedTarget), narrowing, verifier, store);
                return;
            }
        }
        HashSet<FrameDependency> pending = includable.NeededAfter(provenLimit);
        if (pending.Count == 0) return;
        List<RecursiveProofInput> overlaps = store.ProvenOverlaps(pending, MaxExtensionCandidates);
        (AggregationInput scheduled, long cost) = Cheapest(pending);
        if (cost > MaxScheduledStatementCost || !Eip8288Dependencies.FitsProofList(pending))
        {
            // A statement over the whole backlog can take many slots while more arrives; prove a nonce-ordered prefix that fits
            // the budget instead, and let later passes take the rest. A statement the pinned guest cannot hold, one over more
            // dependencies than a list may carry, is never sent to the prover: a prefix that fits is.
            pending = includable.BoundedNeed(provenLimit, pending,
                set => Eip8288Dependencies.FitsProofList(set) ? Cheapest(set).Cost : long.MaxValue, MaxScheduledStatementCost);
            if (pending.Count == 0) return;
            (scheduled, cost) = Cheapest(pending);
        }
        List<FrameDependency> target = pending.Count == deps.Count ? deps : Eip8288Dependencies.Canonicalize(pending);
        if (store.TryGetRecursiveProof(target, out _)) return;
        ValueHash256 targetHash = pending.Count == deps.Count ? depsHash : Eip8288Dependencies.ComputeDepsHash(target);
        cache.Schedule(target, targetHash, scheduled, verifier, store);

        // Folding every witness again, or extending a verified overlapping statement, whichever is estimated cheaper.
        (AggregationInput Input, long Cost) Cheapest(HashSet<FrameDependency> set)
        {
            List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(set);
            AggregationInput best = DependencyProofAggregator.Combine(producing.LeanProduction.ProofInputs, canonical);
            long bestCost = DependencyProofAggregator.EstimatedCost(best, blockProduction: true);
            foreach (RecursiveProofInput parent in overlaps)
            {
                if (!Overlaps(parent, set)) continue;
                AggregationInput extended = DependencyProofAggregator.Combine(producing.LeanProduction.ProofInputs, canonical, parent);
                long extendedCost = DependencyProofAggregator.EstimatedCost(extended, blockProduction: true);
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
    internal sealed class IncludableTransactions
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
        /// under <paramref name="limit"/> whose proving cost stays within <paramref name="budget"/>; empty when none fits.</summary>
        /// <remarks>The run is found by binary search over its length, taking cost to grow with the run, so <paramref name="cost"/>
        /// is evaluated a logarithmic number of times. A first transaction that alone exceeds the budget is passed over with the rest
        /// of its nonce domain, at most <see cref="MaxOversizedSkips"/> times.</remarks>
        public HashSet<FrameDependency> BoundedNeed(IReadOnlySet<FrameDependency> limit, IReadOnlySet<FrameDependency> needed,
            Func<HashSet<FrameDependency>, long> cost, long budget)
        {
            bool[] kept = new bool[_dependencies.Length];
            Keep(limit, [], kept);
            HashSet<(Address?, UInt256)> skipped = [];
            for (int attempt = 0; attempt <= MaxOversizedSkips; attempt++)
            {
                List<(List<FrameDependency> Dependencies, (Address?, UInt256) Domain)> run = [];
                for (int i = 0; i < _dependencies.Length; i++)
                {
                    if (kept[i] || skipped.Contains(_domains[i])) continue;
                    List<FrameDependency> own = [];
                    foreach (FrameDependency dependency in _dependencies[i])
                        if (needed.Contains(dependency)) own.Add(dependency);
                    if (own.Count != 0) run.Add((own, _domains[i]));
                }
                if (run.Count == 0) break;
                int fits = 0;
                for (int low = 1, high = run.Count; low <= high;)
                {
                    int middle = low + (high - low) / 2;
                    if (cost(Prefix(run, middle)) <= budget) (fits, low) = (middle, middle + 1);
                    else high = middle - 1;
                }
                if (fits != 0) return Prefix(run, fits);
                skipped.Add(run[0].Domain);
            }
            return [];

            static HashSet<FrameDependency> Prefix(List<(List<FrameDependency> Dependencies, (Address?, UInt256) Domain)> run, int length)
            {
                HashSet<FrameDependency> prefix = [];
                for (int i = 0; i < length; i++) prefix.UnionWith(run[i].Dependencies);
                return prefix;
            }
        }

        private const int MaxOversizedSkips = 4;

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
}
