// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// EIP-8288 inclusion-list proofs (EIPs#12475): each signed list carries its <c>InclusionListProof</c> inline, verified under the
/// target block's fork to derive the list's dependency-bearing obligations and to feed verified proofs to block aggregation.
/// </summary>
/// <remarks>Verdicts are cached by <c>(keccak256(stark_proof), get_deps_hash(proof_deps), AGGREGATED_VK)</c>, so the same signed
/// list arriving through forkchoiceUpdated and newPayload is verified once.</remarks>
/// <param name="verifier">The backend every inclusion-list proof is verified with.</param>
/// <param name="proofStore">The held verified proofs a locally built list may reuse; without it a local list carries no proof.</param>
public sealed class InclusionListProofService(ILeanProofVerifier verifier, LeanProofStore? proofStore = null)
{
    /// <summary>Held proofs considered for a locally built list, the most covering first.</summary>
    internal const int MaxHeldProofCandidates = 8;

    /// <summary>Held proofs verified under the target fork per local list, so a verifier miss cannot stall
    /// <c>engine_getInclusionListV2</c> past its timeout.</summary>
    internal const int MaxHeldProofVerifications = 2;

    /// <summary>Chooses the held verified recursive proof a locally built list attaches: the one, among those that verify under
    /// <paramref name="targetSpec"/> and whose <c>InclusionListProof</c> container fits <c>MAX_IL_PROOF_BYTES</c>, that covers the
    /// most of <paramref name="candidates"/>.</summary>
    /// <remarks>Never proves: <c>engine_getInclusionListV2</c> has a 1 s timeout, well under a recursive aggregation. Held proofs
    /// are the validated mode-1 wrapper proofs and the background aggregation cycle's own, whose verdicts are normally cached,
    /// so verification is usually a cache hit; at most <see cref="MaxHeldProofVerifications"/> are verified per call. A proof
    /// covering surplus dependencies is reused whole, which EIPs#12475 allows for inclusion-list proofs only.</remarks>
    /// <param name="candidates">The dependencies of each dependency-bearing candidate.</param>
    /// <returns>The proof and its <c>proof_deps</c>, or <c>null</c> when none covers a candidate.</returns>
    public HeldInclusionListProof? ChooseHeldProof(IReadOnlyList<IReadOnlyList<FrameDependency>> candidates, IReleaseSpec targetSpec)
    {
        if (proofStore is null || candidates.Count == 0 || !targetSpec.IsEip8288Enabled) return null;
        HashSet<FrameDependency> wanted = [];
        foreach (IReadOnlyList<FrameDependency> dependencies in candidates) wanted.UnionWith(dependencies);

        List<(int Covered, long Size, RecursiveProofInput Proof)> ranked = [];
        foreach (RecursiveProofInput held in proofStore.ProvenIntersecting(wanted, MaxHeldProofCandidates))
        {
            long size = 2 * sizeof(uint) + held.Proof.Length + (long)held.InnerDeps.Count * Eip8288Constants.DependencyTripleLength;
            if (size > Eip8288Constants.MaxIlProofBytes || held.Proof.IsEmpty) continue;
            HashSet<FrameDependency> proven = [.. held.InnerDeps];
            int covered = 0;
            foreach (IReadOnlyList<FrameDependency> dependencies in candidates)
                if (proven.IsSupersetOf(dependencies)) covered++;
            if (covered > 0) ranked.Add((covered, size, held));
        }
        ranked.Sort(static (x, y) => x.Covered != y.Covered ? y.Covered.CompareTo(x.Covered) : x.Size.CompareTo(y.Size));

        Eip8288Verifier forkVerifier = Eip8288Verifier.For(targetSpec);
        for (int i = 0; i < ranked.Count && i < MaxHeldProofVerifications; i++)
        {
            List<FrameDependency> proofDeps = Eip8288Dependencies.Canonicalize(ranked[i].Proof.InnerDeps);
            if (proofDeps.Count != ranked[i].Proof.InnerDeps.Count || !Eip8288Dependencies.AreSchemesEnabled(proofDeps)) continue;
            byte[] starkProof = ranked[i].Proof.Proof.ToArray();
            ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(proofDeps);
            if (!ProofVerdictCache.VerifyRecursive(verifier, in forkVerifier, in depsHash, starkProof)) continue;
            byte[][] encoded = new byte[proofDeps.Count][];
            for (int j = 0; j < encoded.Length; j++) encoded[j] = Eip8288Dependencies.Serialize([proofDeps[j]]);
            return new HeldInclusionListProof(starkProof, encoded, new HashSet<FrameDependency>(proofDeps));
        }
        return null;
    }

    /// <summary>Sets each list's <see cref="CommitteeInclusionList.VerifiedProofDeps"/> under the fork of
    /// <paramref name="targetSpec"/>: its <c>proof_deps</c> when its nonempty proof verifies, and <c>null</c> otherwise.</summary>
    public void ResolveObligations(IReadOnlyList<CommitteeInclusionList>? lists, IReleaseSpec targetSpec)
    {
        if (lists is null) return;
        foreach (CommitteeInclusionList list in lists)
            list.VerifiedProofDeps = targetSpec.IsEip8288Enabled && TryVerify(list, targetSpec, out List<FrameDependency>? proofDeps)
                ? new HashSet<FrameDependency>(proofDeps!)
                : null;
    }

    /// <summary>The verified proofs of the lists <see cref="ResolveObligations"/> resolved, each once, for the builder to fold into
    /// its block aggregate.</summary>
    /// <remarks>Each covers its whole <c>proof_deps</c>, possibly a superset of what the block includes; block aggregation discards
    /// exactly the dependencies absent from <c>dependencies(block)</c>.</remarks>
    public static List<RecursiveProofInput> ProvenInputs(IReadOnlyList<CommitteeInclusionList> lists)
    {
        List<RecursiveProofInput> proven = [];
        HashSet<ValueHash256> added = [];
        foreach (CommitteeInclusionList list in lists)
            if (list.VerifiedProofDeps is not null
                && InclusionListProofValidator.TryParseProofDeps(list.ProofDeps, out List<FrameDependency>? proofDeps)
                && added.Add(ValueKeccak.Compute(list.StarkProof)))
                proven.Add(new RecursiveProofInput(proofDeps!, list.StarkProof));
        return proven;
    }

    private bool TryVerify(CommitteeInclusionList list, IReleaseSpec spec, out List<FrameDependency>? proofDeps)
    {
        proofDeps = null;
        if (list.StarkProof.Length == 0 || list.ProofDeps.Length == 0
            || !InclusionListProofValidator.TryParseProofDeps(list.ProofDeps, out proofDeps))
            return false;
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(proofDeps!);
        return ProofVerdictCache.VerifyRecursive(verifier, Eip8288Verifier.For(spec), in depsHash, list.StarkProof);
    }
}

/// <summary>A held verified proof a locally built inclusion list attaches as its <c>InclusionListProof</c>.</summary>
/// <param name="StarkProof">The proof bytes.</param>
/// <param name="ProofDeps">Its canonical <c>proof_deps</c>, each a 96-byte dependency encoding.</param>
/// <param name="Dependencies">The same dependencies, for coverage checks.</param>
public sealed record HeldInclusionListProof(byte[] StarkProof, byte[][] ProofDeps, IReadOnlySet<FrameDependency> Dependencies)
{
    /// <summary>Whether the proof covers every dependency of <paramref name="dependencies"/>.</summary>
    public bool Covers(IReadOnlyList<FrameDependency> dependencies)
    {
        foreach (FrameDependency dependency in dependencies)
            if (!Dependencies.Contains(dependency)) return false;
        return true;
    }
}

/// <summary>EIP-8288 inclusion-list rules shared by the Engine API, block building and inclusion enforcement.</summary>
public static class InclusionListProofValidator
{
    /// <summary>Parses <c>proof_deps</c>, failing when an entry is not a 96-byte leanSPHINCS encoding, the only accepted scheme, or
    /// the list is not sorted and deduplicated; either invalidates the proof, not the signed list.</summary>
    public static bool TryParseProofDeps(byte[][] encoded, out List<FrameDependency>? proofDeps)
    {
        proofDeps = new List<FrameDependency>(encoded.Length);
        byte[]? previous = null;
        foreach (byte[] entry in encoded)
        {
            if (entry is not { Length: Eip8288Constants.DependencyTripleLength } || !entry.AsSpan(0, 31).IsZero()
                || !Eip8288Dependencies.IsAcceptedScheme(entry[31])
                || previous is not null && Bytes.BytesComparer.Compare(previous, entry) >= 0)
            {
                proofDeps = null;
                return false;
            }
            proofDeps.AddRange(Eip8288Dependencies.Parse(entry));
            previous = entry;
        }
        return true;
    }

    /// <summary>
    /// EIP-8288 inclusion enforcement over the counted lists: each entry binds independently, a dependency-free one always, and a
    /// dependency-bearing one when its list's proof verified and its <c>proof_deps</c> contain <c>dependencies(tx)</c>.
    /// </summary>
    /// <remarks>Without per-list input, <paramref name="flattened"/> is one list without a proof. An independent list can still
    /// require the same transaction.</remarks>
    /// <param name="flattened">The flattened inclusion list, used only when <paramref name="lists"/> is <c>null</c>.</param>
    /// <param name="spec">The target block's spec, for static validity.</param>
    /// <returns>The union of every list's binding transactions, each once, in first-appearance order.</returns>
    public static Transaction[] SelectEligible(IReadOnlyList<Transaction> flattened, IReadOnlyList<CommitteeInclusionList>? lists,
        IReleaseSpec spec)
    {
        if (lists is null) return SelectBinding(flattened, null, spec);
        List<Transaction> eligible = [];
        HashSet<Transaction> added = new(ReferenceEqualityComparer.Instance);
        HashSet<Hash256> addedHashes = [];
        foreach (CommitteeInclusionList list in lists)
            foreach (Transaction tx in SelectBinding(list.Transactions, list.VerifiedProofDeps, spec))
                if (added.Add(tx) && (tx.Hash is null || addedHashes.Add(tx.Hash))) eligible.Add(tx);
        return [.. eligible];
    }

    /// <summary>One list's binding transactions: its statically valid entries that are dependency-free, those without a
    /// <c>DEP_VERIFY_FRAME_MODE</c> frame, or whose dependencies <paramref name="proofDeps"/> covers.</summary>
    /// <remarks>An entry with a dependency verification frame is dependency-bearing even when the frame carries no triples or
    /// fails static validity, and EIP-7805 excuses its omission as an invalid transaction. An entry that does not decode never
    /// reaches a list.</remarks>
    private static Transaction[] SelectBinding(IReadOnlyList<Transaction> transactions, IReadOnlySet<FrameDependency>? proofDeps,
        IReleaseSpec spec)
    {
        List<Transaction> eligible = new(transactions.Count);
        foreach (Transaction tx in transactions)
            if (IsStaticallyValid(tx, spec) && (!Eip8288Dependencies.IsDependencyBearing(tx) || proofDeps is not null && Covers(proofDeps, tx)))
                eligible.Add(tx);
        return [.. eligible];
    }

    private static bool Covers(IReadOnlySet<FrameDependency> proofDeps, Transaction tx)
    {
        foreach (FrameDependency dependency in Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue))
            if (!proofDeps.Contains(dependency)) return false;
        return true;
    }

    /// <summary>EIP-8141 and EIP-8288 static validity under <paramref name="spec"/>; any non-frame transaction passes.</summary>
    public static bool IsStaticallyValid(Transaction? tx, IReleaseSpec spec)
    {
        if (tx is null) return false;
        if (!tx.SupportsFrames) return true;
        return FrameTxValidation.IsWellFormed(tx, spec, out _)
            && FrameTxNonceKeysTxValidator.Instance.IsWellFormed(tx, spec);
    }
}
