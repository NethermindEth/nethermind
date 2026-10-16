// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// A self-contained <c>[transactions, [stark_proof, deps_hash]]</c> package submitted to the local mempool: inclusion-list
/// transactions with an aggregate proof of exactly their dependencies.
/// </summary>
/// <remarks>Not the signed EIP-8288 FOCIL, which commits to <c>[stark_proof_hash, deps_hash]</c> or null and leaves the proof
/// to a separate consensus-layer object.</remarks>
public sealed class InclusionListProofPackage
{
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required RecursiveStark RecursiveStark { get; init; }
}

/// <summary>EIP-8288 <c>il_proof = [stark_proof, proof_deps]</c>: a committed proof with its sorted, deduplicated dependency list.</summary>
public sealed record InclusionListProof(byte[] StarkProof, IReadOnlyList<FrameDependency> ProofDeps);

/// <summary>The answer of <c>engine_verifyInclusionListProofV1</c>.</summary>
public enum InclusionListProofStatus
{
    /// <summary>A verified proof is held under the commitment and its <c>proof_deps</c> contain the list's dependencies.</summary>
    Valid,

    /// <summary>The known <c>proof_deps</c> omit a list dependency, or the committed proof fails verification.</summary>
    Invalid,

    /// <summary>No proof is held under the commitment and no matching <c>il_proof</c> was supplied.</summary>
    ProofUnknown
}

/// <summary>EIP-8288 inclusion-list commitments and the obligations they impose (EIPs#12475).</summary>
/// <remarks>
/// A signed list carries <c>recursive_stark = [stark_proof_hash, deps_hash]</c> or null. The commitment is valid when this
/// node holds a verified proof under that pair whose <c>proof_deps</c> contain the list's dependencies; the proof usually comes
/// from a mempool wrapper, otherwise from an <c>il_proof</c>. Only inclusion lists may commit to a superset; the block proof
/// stays exact. The consensus layer counts a committed list once <see cref="VerifyCommitment"/> answers valid, and passes the
/// Engine API each counted list's transactions with its commitment. Obligations then follow from the signed data alone.
/// </remarks>
public static class InclusionListProofValidator
{
    public const string DepsHashMismatch = "FOCIL recursive STARK public input must equal get_deps_hash(dependencies(transactions))";
    public const string InvalidProof = "FOCIL recursive STARK failed verification";
    public const string EmptyDependencies = "a list without dependencies must carry a null recursive_stark";
    public const string EmptyProof = "an empty stark_proof is never a valid inclusion-list proof";
    public const string OversizedProof = "stark_proof exceeds MAX_AGGREGATE_PROOF_BYTES";
    public const string UncoveredDependency = "the committed proof_deps omit a dependency of the list";
    public const string UnknownProof = "no proof is held under the commitment";

    /// <summary>Validates a self-contained <c>[transactions, [stark_proof, deps_hash]]</c> package submitted to the local mempool.</summary>
    public static bool Validate(InclusionListProofPackage package, ILeanProofVerifier verifier, out List<FrameDependency> deps, out string? error)
        => Validate(package.Transactions, package.RecursiveStark, verifier, out deps, out error);

    /// <summary>Validates a package whose every entry must be well formed, against a proof of exactly their dependencies.</summary>
    /// <remarks>Unlike a counted list, a mempool submission rejects malformed entries outright rather than deriving
    /// dependencies without them.</remarks>
    public static bool Validate(IReadOnlyList<Transaction> transactions, RecursiveStark proof,
        ILeanProofVerifier verifier, out List<FrameDependency> deps, out string? error, IReleaseSpec? spec = null)
    {
        deps = [];
        error = null;
        foreach (Transaction tx in transactions)
        {
            if (tx is null) { error = InvalidProof; return false; }
            if (!IsFrameWellFormed(tx, spec, out error)) return false;
        }
        List<FrameDependency> required = DependenciesOf(transactions);
        ValueHash256 claimed = proof.BlockDepsHash.ValueHash256;
        if (required.Count == 0) error = EmptyDependencies;
        else if (claimed != Eip8288Dependencies.ComputeDepsHash(required)) error = DepsHashMismatch;
        else error = CheckProof(proof.StarkProof) ?? (ProofVerdictCache.VerifyRecursive(verifier, in claimed, proof.StarkProof, required) ? null : InvalidProof);
        if (error is not null) return false;
        deps = required;
        return true;
    }

    /// <summary>
    /// <c>engine_verifyInclusionListProofV1</c>: whether a list's non-null <paramref name="commitment"/> is valid for its
    /// <paramref name="transactions"/>.
    /// </summary>
    /// <remarks>
    /// <c>deps</c> is the union over the statically valid entries only. A commitment to a proof <paramref name="proofStore"/>
    /// holds, such as a validated mode-1 wrapper's, is a verdict-cache lookup and subset test with no verification. An
    /// <paramref name="ilProof"/> whose <c>stark_proof</c> or <c>proof_deps</c> do not match the commitment is ignored; a
    /// matching one is verified. A proof answered valid is retained in <paramref name="proofStore"/>, so it stays servable and
    /// available for building through the slot.
    /// </remarks>
    public static InclusionListProofStatus VerifyCommitment(IReadOnlyList<Transaction> transactions, in InclusionListCommitment commitment,
        InclusionListProof? ilProof, ILeanProofVerifier verifier, IReleaseSpec spec, out string? error, LeanProofStore? proofStore = null)
    {
        error = null;
        List<FrameDependency> deps = StaticallyValidDependencies(transactions, spec);
        if (deps.Count == 0)
        {
            error = EmptyDependencies;
            return InclusionListProofStatus.Invalid;
        }
        ProofVerdictCache cache = ProofVerdictCache.For(verifier);
        InclusionListProof? matching = ilProof is not null && Matches(ilProof, commitment) ? ilProof : null;
        ValueHash256 depsHash = commitment.DepsHash;
        byte[]? heldProof = null;
        FrameDependency[]? heldDeps = null;
        bool held = proofStore?.TryGetRecursiveProof(commitment.StarkProofHash, depsHash, out heldProof, out heldDeps) == true;
        IReadOnlyList<FrameDependency>? proofDeps = cache.TryGetPreimage(in depsHash, out FrameDependency[] known) ? known
            : held ? heldDeps : matching?.ProofDeps;
        if (proofDeps is not null && !Covers(proofDeps, deps))
        {
            error = UncoveredDependency;
            return InclusionListProofStatus.Invalid;
        }
        if (cache.TryGetVerdict(commitment.StarkProofHash, depsHash) == false)
        {
            error = InvalidProof;
            return InclusionListProofStatus.Invalid;
        }
        // Holding a proof means holding its bytes with its proof_deps; for a validated wrapper's proof this is a verdict-cache hit.
        (byte[] Proof, IReadOnlyList<FrameDependency> Deps)? candidate = held ? (heldProof!, heldDeps!) : matching is null ? null : (matching.StarkProof, matching.ProofDeps);
        if (candidate is not { } proof)
        {
            error = UnknownProof;
            return InclusionListProofStatus.ProofUnknown;
        }
        if (!ProofVerdictCache.VerifyRecursive(verifier, in depsHash, proof.Proof, proof.Deps))
        {
            error = InvalidProof;
            return InclusionListProofStatus.Invalid;
        }
        // A proof answered VALID stays servable through engine_getInclusionListProofV1 and available to builders, which fold it
        // into the block aggregate and discard the dependencies their block does not need.
        proofStore?.RetainInclusionListProof(proof.Deps, proof.Proof);
        return InclusionListProofStatus.Valid;
    }

    /// <summary>Whether an <c>il_proof</c> is the proof the commitment names: it hashes to <c>stark_proof_hash</c>, and its
    /// canonical <c>proof_deps</c>, within <c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c> entries of enabled schemes, hash to
    /// <c>deps_hash</c>.</summary>
    public static bool Matches(InclusionListProof ilProof, in InclusionListCommitment commitment)
    {
        IReadOnlyList<FrameDependency> proofDeps = ilProof.ProofDeps;
        if (CheckProof(ilProof.StarkProof) is not null || proofDeps.Count is 0 or > Eip8288Constants.MaxDepsPerAggregate
            || !Eip8288Dependencies.AreSchemesEnabled(proofDeps)) return false;
        for (int i = 1; i < proofDeps.Count; i++)
            if (Compare(proofDeps[i - 1], proofDeps[i]) >= 0) return false;
        return ValueKeccak.Compute(ilProof.StarkProof) == commitment.StarkProofHash
            && Eip8288Dependencies.ComputeDepsHash(proofDeps) == commitment.DepsHash;
    }

    private static int Compare(FrameDependency left, FrameDependency right)
    {
        int comparison = left.Scheme.CompareTo(right.Scheme);
        if (comparison != 0) return comparison;
        comparison = left.DataHash.CompareTo(right.DataHash);
        return comparison != 0 ? comparison : left.VerificationKey.CompareTo(right.VerificationKey);
    }

    /// <summary>A proof is never empty and is bounded by <c>MAX_AGGREGATE_PROOF_BYTES</c> before decoding or verification.</summary>
    private static string? CheckProof(byte[]? proof) => proof switch
    {
        not { Length: > 0 } => EmptyProof,
        { Length: > Eip8288Constants.MaxAggregateProofBytes } => OversizedProof,
        _ => null
    };

    /// <summary>Whether <c>set(deps) &lt;= set(proof_deps)</c>.</summary>
    private static bool Covers(IReadOnlyList<FrameDependency> proofDeps, List<FrameDependency> deps)
    {
        HashSet<FrameDependency> covered = [.. proofDeps];
        foreach (FrameDependency dependency in deps)
            if (!covered.Contains(dependency)) return false;
        return true;
    }

    /// <summary>
    /// EIP-8288 inclusion enforcement over the counted lists: obligations follow from signed data alone. Every statically
    /// valid entry of a list with a non-null commitment binds; a list with a null commitment binds only its entries without
    /// dependencies.
    /// </summary>
    /// <remarks>A committed list binds its dependency-bearing entries even for a builder that lacks or cannot verify its
    /// proof: failing to obtain the proof is a liveness failure, not an omission ground. An independent list can still require
    /// the same transaction. Without per-list input, <paramref name="flattened"/> is one list with a null commitment.</remarks>
    /// <param name="flattened">The flattened inclusion list, used only when <paramref name="lists"/> is <c>null</c>.</param>
    /// <param name="spec">The target block's spec, for static validity.</param>
    /// <returns>The union of every list's binding transactions, each once, in first-appearance order.</returns>
    public static Transaction[] SelectEligible(IReadOnlyList<Transaction> flattened, IReadOnlyList<CommitteeInclusionList>? lists,
        IReleaseSpec spec)
    {
        if (lists is null) return SelectEligible(flattened, committed: false, spec);
        List<Transaction> eligible = [];
        HashSet<Transaction> added = new(ReferenceEqualityComparer.Instance);
        HashSet<Hash256> addedHashes = [];
        foreach (CommitteeInclusionList list in lists)
            foreach (Transaction tx in SelectEligible(list.Transactions, list.IsCommitted, spec))
                if (added.Add(tx) && (tx.Hash is null || addedHashes.Add(tx.Hash))) eligible.Add(tx);
        return [.. eligible];
    }

    /// <summary>The counted lists' committed proofs a builder holds and can fold into its block aggregate.</summary>
    /// <remarks>Each input covers the proof's whole <c>proof_deps</c>, which may be a superset of its list's dependencies; the
    /// block aggregation discards whatever the block does not need, so the block proof stays exact. A committed list whose proof
    /// is not held, fails verification or does not cover the list is left out; its obligations stand regardless, so
    /// <paramref name="unusable"/> reports it for the builder to surface.</remarks>
    public static List<RecursiveProofInput> ProvenInputs(IReadOnlyList<CommitteeInclusionList> lists, ILeanProofVerifier verifier,
        IReleaseSpec spec, LeanProofStore? proofStore, out List<ulong> unusable)
    {
        List<RecursiveProofInput> proven = [];
        HashSet<ValueHash256> added = [];
        unusable = [];
        foreach (CommitteeInclusionList list in lists)
        {
            if (list.Commitment is not { } commitment) continue;
            List<FrameDependency> deps = StaticallyValidDependencies(list.Transactions, spec);
            if (deps.Count == 0) continue;
            ValueHash256 depsHash = commitment.DepsHash;
            if (proofStore?.TryGetRecursiveProof(commitment.StarkProofHash, depsHash, out byte[]? proof, out FrameDependency[]? proofDeps) != true
                || !Covers(proofDeps!, deps) || !ProofVerdictCache.VerifyRecursive(verifier, in depsHash, proof!, proofDeps))
            {
                unusable.Add(list.CommitteeIndex);
                continue;
            }
            if (added.Add(commitment.StarkProofHash)) proven.Add(new RecursiveProofInput(Array.AsReadOnly(proofDeps!), proof!, commitment.StarkProofHash));
        }
        return proven;
    }

    /// <summary>One list's binding transactions: every statically valid entry if committed, otherwise those without dependencies.</summary>
    private static Transaction[] SelectEligible(IReadOnlyList<Transaction> transactions, bool committed, IReleaseSpec spec)
    {
        List<Transaction> eligible = new(transactions.Count);
        foreach (Transaction tx in transactions)
            if (IsStaticallyValid(tx, spec) && (committed || Eip8288Dependencies.RecursiveStarkGas(tx) == 0)) eligible.Add(tx);
        return [.. eligible];
    }

    /// <summary>A list's <c>deps</c>: the sorted, deduplicated union of <c>dependencies(tx)</c> over the entries that decode
    /// and pass EIP-8141 and EIP-8288 static validity for the target block's fork; other entries contribute none.</summary>
    public static List<FrameDependency> StaticallyValidDependencies(IReadOnlyList<Transaction> transactions, IReleaseSpec spec)
    {
        List<FrameDependency> declared = [];
        foreach (Transaction tx in transactions)
            if (IsStaticallyValid(tx, spec)) declared.AddRange(Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue));
        return Eip8288Dependencies.Canonicalize(declared);
    }

    /// <summary>Spec <c>dependencies(transactions)</c>: the sorted, deduplicated union over every entry.</summary>
    internal static List<FrameDependency> DependenciesOf(IReadOnlyList<Transaction> transactions)
    {
        List<FrameDependency> declared = [];
        foreach (Transaction tx in transactions)
            if (tx is not null) declared.AddRange(Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue));
        return Eip8288Dependencies.Canonicalize(declared);
    }

    private static bool IsStaticallyValid(Transaction? tx, IReleaseSpec spec) => tx is not null && IsFrameWellFormed(tx, spec, out _);

    private static bool IsFrameWellFormed(Transaction tx, IReleaseSpec? spec, out string? error)
    {
        error = null;
        if (!tx.SupportsFrames || spec is null) return true;
        if (!FrameTxValidation.IsWellFormed(tx, spec, out error)) return false;
        ValidationResult nonceKeys = FrameTxNonceKeysTxValidator.Instance.IsWellFormed(tx, spec);
        if (!nonceKeys) { error = nonceKeys.Error; return false; }
        ValidationResult envelope = FrameTxEnvelopeTxValidator.Instance.IsWellFormed(tx, spec);
        if (!envelope) { error = envelope.Error; return false; }
        return true;
    }
}
