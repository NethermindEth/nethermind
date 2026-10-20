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

/// <summary>EIP-8288 <c>il_proof = [stark_proof, proof_deps]</c>: a committed proof with its dependency list, which a valid
/// proof has sorted and deduplicated.</summary>
/// <param name="DepsHash"><c>get_deps_hash</c> over <c>proof_deps</c> exactly as given, so a reordered or duplicated list never
/// matches the commitment of its canonical form.</param>
/// <param name="HasWideScheme">Whether an entry's 32-byte scheme word exceeds a byte, a scheme outside every
/// <c>ENABLED_SCHEMES</c>.</param>
public sealed record InclusionListProof(byte[] StarkProof, IReadOnlyList<FrameDependency> ProofDeps, ValueHash256 DepsHash, bool HasWideScheme = false)
{
    public InclusionListProof(byte[] starkProof, IReadOnlyList<FrameDependency> proofDeps)
        : this(starkProof, proofDeps, Blake2s.Compute(Eip8288Dependencies.Serialize(proofDeps))) { }
}

/// <summary>The answer of <c>engine_verifyInclusionListProofV1</c> and <c>engine_submitInclusionListProofV1</c>.</summary>
public enum InclusionListProofStatus
{
    /// <summary>A verified proof is held under the commitment for the target fork, with its bytes retained until the
    /// attestation deadline, and its <c>proof_deps</c> contain the list's dependencies; or a submitted proof verified.</summary>
    Valid,

    /// <summary>Final for the list: its dependencies are empty, the known <c>proof_deps</c> omit one, an <c>INVALID</c> verdict
    /// is held under the commitment for the target fork, or the matching proof fails verification.</summary>
    Invalid,

    /// <summary>The proof's bytes are not held under the commitment for the target fork, even if its <c>VALID</c> verdict is,
    /// and no matching <c>il_proof</c> was supplied.</summary>
    ProofUnknown
}

/// <summary>
/// EIP-8288 inclusion-list proofs (EIPs#12475, execution-apis#945): commitment checks, <c>il_proof</c> serving and submission,
/// retention until consensus-layer deadlines, and the obligations a counted list imposes.
/// </summary>
/// <remarks>
/// Every call takes the target block's timestamp, whose fork fixes the static validity rules and <c>AGGREGATED_VK</c>; a
/// verdict or held proof under another fork's key never counts. A proof is held under <c>(stark_proof_hash, deps_hash)</c> only
/// with all three of its bytes, a <c>VALID</c> aggregate verdict under the target fork's key and its <c>proof_deps</c>: from a
/// fully validated mode-1 wrapper, or from an <c>il_proof</c> retained by this service. A verdict without the bytes only spares
/// verifying the same proof again when it is supplied again.
/// </remarks>
public sealed class InclusionListProofService(ISpecProvider specProvider, ILeanProofVerifier verifier, LeanProofStore proofStore,
    ITimestamper timestamper, ProofWrapperService? wrappers = null)
{
    /// <summary>Proofs retained until the deadlines the consensus layer supplied.</summary>
    public InclusionListProofRetention Retention { get; } = new(timestamper);

    /// <summary><c>engine_verifyInclusionListProofV1</c>: whether a counted list's non-null <paramref name="commitment"/> is valid
    /// for its <paramref name="transactions"/> under the fork at <paramref name="timestamp"/>.</summary>
    /// <remarks>A commitment to a held proof is a lookup and subset test with no verification. An <paramref name="ilProof"/>
    /// that does not hash to the commitment is ignored, whatever its size or shape; a matching one is checked against a cached
    /// verdict, and verified only without one. A matching one failing the submission shape checks is <c>INVALID</c> without
    /// verification. Either way its verdict is recorded. A held <c>INVALID</c> verdict is final with or without an
    /// <paramref name="ilProof"/>.</remarks>
    /// <param name="attestationDeadlineMs">The EIP-7805 attestation deadline of the target block's slot, in Unix milliseconds,
    /// until which a proof answered valid keeps its bytes and <c>proof_deps</c>.</param>
    public InclusionListProofStatus Verify(ulong timestamp, IReadOnlyList<Transaction> transactions, in InclusionListCommitment commitment,
        InclusionListProof? ilProof, ulong attestationDeadlineMs, out string? error)
    {
        error = null;
        IReleaseSpec spec = SpecAt(timestamp);
        Eip8288Verifier forkVerifier = Eip8288Verifier.For(spec);
        List<FrameDependency> deps = InclusionListProofValidator.StaticallyValidDependencies(transactions, spec);
        if (deps.Count == 0)
        {
            error = InclusionListProofValidator.EmptyDependencies;
            return InclusionListProofStatus.Invalid;
        }
        InclusionListProof? matching = ilProof is not null && InclusionListProofValidator.Matches(ilProof, commitment) ? ilProof : null;
        ValueHash256 proofHash = commitment.StarkProofHash;
        ValueHash256 depsHash = commitment.DepsHash;
        ProofVerdictCache cache = ProofVerdictCache.For(verifier);
        bool held = TryGetHeld(proofHash, depsHash, forkVerifier, out byte[]? heldProof, out FrameDependency[]? heldDeps);
        IReadOnlyList<FrameDependency>? proofDeps = cache.TryGetPreimage(in depsHash, out FrameDependency[] known)
            ? known : held ? heldDeps : matching?.ProofDeps;
        if (proofDeps is not null && !InclusionListProofValidator.Covers(proofDeps, deps))
        {
            error = InclusionListProofValidator.UncoveredDependency;
            return InclusionListProofStatus.Invalid;
        }
        if (cache.TryGetVerdict(proofHash, depsHash, forkVerifier.AggregatedVk) == false)
        {
            error = InclusionListProofValidator.InvalidProof;
            return InclusionListProofStatus.Invalid;
        }
        if (held)
        {
            Retention.Retain(heldProof!, heldDeps!, commitment, forkVerifier, attestationDeadlineMs);
            return InclusionListProofStatus.Valid;
        }
        if (matching is null)
        {
            error = InclusionListProofValidator.UnknownProof;
            return InclusionListProofStatus.ProofUnknown;
        }
        if (InclusionListProofValidator.CheckShape(matching, spec) is { } shapeError)
        {
            ProofVerdictCache.RecordInvalidRecursive(verifier, in forkVerifier, in proofHash, in depsHash);
            error = shapeError;
            return InclusionListProofStatus.Invalid;
        }
        // A cached VALID verdict answers without verifying again; a new verdict is recorded under the target fork's key.
        if (!ProofVerdictCache.VerifyRecursive(verifier, in forkVerifier, in depsHash, matching.StarkProof, matching.ProofDeps))
        {
            error = InclusionListProofValidator.InvalidProof;
            return InclusionListProofStatus.Invalid;
        }
        Retention.Retain(matching.StarkProof, [.. matching.ProofDeps], commitment, forkVerifier, attestationDeadlineMs);
        return InclusionListProofStatus.Valid;
    }

    /// <summary><c>engine_submitInclusionListProofV1</c>: verifies a proof-topic <c>il_proof</c> under the fork at
    /// <paramref name="timestamp"/> and records its verdict, retaining a valid proof's bytes with its <c>proof_deps</c>.</summary>
    /// <remarks>A proof over the size bounds, or with <c>proof_deps</c> that are noncanonical or name a scheme outside
    /// <c>ENABLED_SCHEMES</c>, is <c>INVALID</c> without verification, and that verdict is recorded too. A verdict already held
    /// under the same key answers without verifying again.</remarks>
    /// <param name="viewFreezeDeadlineMs">The EIP-7805 view freeze deadline of the proof's slot, in Unix milliseconds, until which
    /// a valid proof is retained.</param>
    public InclusionListProofStatus Submit(ulong timestamp, InclusionListProof ilProof, ulong viewFreezeDeadlineMs, out string? error)
    {
        IReleaseSpec spec = SpecAt(timestamp);
        Eip8288Verifier forkVerifier = Eip8288Verifier.For(spec);
        ValueHash256 proofHash = ValueKeccak.Compute(ilProof.StarkProof);
        ValueHash256 depsHash = ilProof.DepsHash;
        error = InclusionListProofValidator.CheckShape(ilProof, spec);
        if (error is not null)
        {
            ProofVerdictCache.RecordInvalidRecursive(verifier, in forkVerifier, in proofHash, in depsHash);
            return InclusionListProofStatus.Invalid;
        }
        if (!ProofVerdictCache.VerifyRecursive(verifier, in forkVerifier, in depsHash, ilProof.StarkProof, ilProof.ProofDeps))
        {
            error = InclusionListProofValidator.InvalidProof;
            return InclusionListProofStatus.Invalid;
        }
        InclusionListCommitment commitment = new(proofHash, depsHash);
        Retention.Retain(ilProof.StarkProof, [.. ilProof.ProofDeps], commitment, forkVerifier, viewFreezeDeadlineMs);
        return InclusionListProofStatus.Valid;
    }

    /// <summary><c>engine_getInclusionListProofV1</c>: the held <c>il_proof</c> hashing to <paramref name="starkProofHash"/> with a
    /// <c>VALID</c> verdict under the fork at <paramref name="timestamp"/>, or <c>null</c>, including once its bytes are released.</summary>
    public InclusionListProof? Get(ulong timestamp, in ValueHash256 starkProofHash)
    {
        Eip8288Verifier forkVerifier = Eip8288Verifier.For(SpecAt(timestamp));
        if (Retention.TryGet(starkProofHash, null, forkVerifier, out byte[]? proof, out FrameDependency[]? deps)) return new(proof!, deps!);
        if (!proofStore.TryGetRecursiveProof(starkProofHash, out proof, out deps)) return null;
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(deps!);
        return IsVerifiedUnder(starkProofHash, depsHash, forkVerifier) ? new(proof!, deps!) : null;
    }

    /// <summary>
    /// Marks each counted list whose non-null commitment this node holds a valid proof for, under the fork of
    /// <paramref name="targetSpec"/>; every other commitment is treated as null.
    /// </summary>
    /// <remarks>A list stays counted either way: a signed list always obligates its dependency-free transactions, and its
    /// dependency-bearing ones only with a held proof covering them.</remarks>
    public void ResolveObligations(IReadOnlyList<CommitteeInclusionList>? lists, IReleaseSpec targetSpec)
    {
        if (lists is null || !targetSpec.IsEip8288Enabled) return;
        Eip8288Verifier forkVerifier = Eip8288Verifier.For(targetSpec);
        foreach (CommitteeInclusionList list in lists)
            list.HoldsValidProof = list.Commitment is { } commitment && Covers(list.Transactions, commitment, targetSpec, forkVerifier, out _, out _);
    }

    /// <summary>The held proofs of the lists <see cref="ResolveObligations"/> marked, each once, for the builder to fold into its
    /// block aggregate.</summary>
    /// <remarks>Each covers its whole <c>proof_deps</c>, possibly a superset of its list's dependencies; block aggregation
    /// discards what the block does not need, so the block proof stays exact.</remarks>
    public List<RecursiveProofInput> ProvenInputs(IReadOnlyList<CommitteeInclusionList> lists, IReleaseSpec targetSpec)
    {
        List<RecursiveProofInput> proven = [];
        HashSet<ValueHash256> added = [];
        Eip8288Verifier forkVerifier = Eip8288Verifier.For(targetSpec);
        foreach (CommitteeInclusionList list in lists)
            if (list.HoldsValidProof && list.Commitment is { } commitment
                && Covers(list.Transactions, commitment, targetSpec, forkVerifier, out byte[]? proof, out FrameDependency[]? proofDeps)
                && added.Add(commitment.StarkProofHash))
                proven.Add(new RecursiveProofInput(Array.AsReadOnly(proofDeps!), proof!, commitment.StarkProofHash));
        return proven;
    }

    /// <summary>Whether this node holds a valid proof for <paramref name="commitment"/> covering the list's dependencies.</summary>
    private bool Covers(IReadOnlyList<Transaction> transactions, in InclusionListCommitment commitment, IReleaseSpec spec,
        in Eip8288Verifier forkVerifier, out byte[]? proof, out FrameDependency[]? proofDeps)
    {
        List<FrameDependency> deps = InclusionListProofValidator.StaticallyValidDependencies(transactions, spec);
        bool held = TryGetHeld(commitment.StarkProofHash, commitment.DepsHash, forkVerifier, out proof, out proofDeps);
        return held && deps.Count != 0 && InclusionListProofValidator.Covers(proofDeps!, deps);
    }

    /// <summary>Whether this node holds the bytes of a proof under <c>(stark_proof_hash, deps_hash)</c> with its
    /// <c>proof_deps</c> and a <c>VALID</c> verdict under <paramref name="forkVerifier"/>.</summary>
    private bool TryGetHeld(in ValueHash256 proofHash, in ValueHash256 depsHash, in Eip8288Verifier forkVerifier, out byte[]? proof,
        out FrameDependency[]? proofDeps)
    {
        if (Retention.TryGet(proofHash, depsHash, forkVerifier, out proof, out proofDeps)) return true;
        if (proofStore.TryGetRecursiveProof(proofHash, depsHash, out proof, out proofDeps) && IsVerifiedUnder(proofHash, depsHash, forkVerifier))
            return true;
        proof = null;
        proofDeps = null;
        return false;
    }

    /// <summary>Whether the mempool proof store's proof is verified under <paramref name="forkVerifier"/>: by its cached verdict, or,
    /// once that is evicted, because the verifier upgrade rule keeps the store to the current target fork's verifier.</summary>
    private bool IsVerifiedUnder(in ValueHash256 proofHash, in ValueHash256 depsHash, in Eip8288Verifier forkVerifier)
    {
        if (ProofVerdictCache.For(verifier).TryGetVerdict(proofHash, depsHash, forkVerifier.AggregatedVk) is { } verdict) return verdict;
        // A pending upgrade rule has not yet kept the store to the current verifier, so its proofs are not counted as verified.
        try { return wrappers?.RefreshVerifier() == forkVerifier; }
        catch (InvalidOperationException) { return false; }
    }

    private IReleaseSpec SpecAt(ulong timestamp) => specProvider.GetSpec(ForkActivation.TimestampOnly(timestamp));
}

/// <summary>EIP-8288 inclusion-list rules shared by the Engine API, block building and inclusion enforcement.</summary>
public static class InclusionListProofValidator
{
    public const string InvalidProof = "the committed proof failed verification under the target fork's AGGREGATED_VK";
    public const string EmptyDependencies = "a list without dependencies must carry a null recursive_stark";
    public const string EmptyProof = "an empty stark_proof is never a valid inclusion-list proof";
    public const string OversizedProof = "stark_proof exceeds MAX_AGGREGATE_PROOF_BYTES";
    public const string UncoveredDependency = "the committed proof_deps omit a dependency of the list";
    public const string UnknownProof = "the committed proof's bytes are not held for the target fork";
    public const string EmptyProofDeps = "proof_deps must not be empty";
    public const string TooManyProofDeps = "proof_deps exceed MAX_DEPS_PER_AGGREGATE_WRAPPER";
    public const string NonCanonicalProofDeps = "proof_deps must be sorted and deduplicated";
    public const string DisabledScheme = "proof_deps name a scheme outside the target fork's ENABLED_SCHEMES";

    /// <summary>Whether an <c>il_proof</c> is the proof <paramref name="commitment"/> names: hashing to <c>stark_proof_hash</c>,
    /// with <c>proof_deps</c>, exactly as given, hashing to <c>deps_hash</c>.</summary>
    /// <remarks>A matching proof that is not well formed is <c>INVALID</c> like any proof failing verification.</remarks>
    public static bool Matches(InclusionListProof ilProof, in InclusionListCommitment commitment)
        => ilProof.DepsHash == commitment.DepsHash && ValueKeccak.Compute(ilProof.StarkProof) == commitment.StarkProofHash;

    /// <summary>The <c>il_proof</c> shape checks, made before any verification: a nonempty proof within
    /// <c>MAX_AGGREGATE_PROOF_BYTES</c>, and a nonempty, sorted, deduplicated <c>proof_deps</c> of enabled schemes within
    /// <c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c> entries.</summary>
    /// <returns>The violated bound, or <c>null</c>.</returns>
    public static string? CheckShape(InclusionListProof ilProof, IReleaseSpec spec)
    {
        IReadOnlyList<FrameDependency> proofDeps = ilProof.ProofDeps;
        if (CheckProof(ilProof.StarkProof) is { } proofError) return proofError;
        if (proofDeps.Count == 0) return EmptyProofDeps;
        if (proofDeps.Count > Eip8288Constants.MaxDepsPerAggregateWrapper) return TooManyProofDeps;
        if (ilProof.HasWideScheme) return DisabledScheme;
        for (int i = 1; i < proofDeps.Count; i++)
            if (Compare(proofDeps[i - 1], proofDeps[i]) >= 0) return NonCanonicalProofDeps;
        return spec.IsEip8288Enabled && Eip8288Dependencies.AreSchemesEnabled(proofDeps) ? null : DisabledScheme;
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
    public static bool Covers(IReadOnlyList<FrameDependency> proofDeps, List<FrameDependency> deps)
    {
        HashSet<FrameDependency> covered = [.. proofDeps];
        foreach (FrameDependency dependency in deps)
            if (!covered.Contains(dependency)) return false;
        return true;
    }

    /// <summary>
    /// EIP-8288 inclusion enforcement over the counted lists: a signed list always obligates its dependency-free
    /// transactions, and its dependency-bearing ones only when this node holds a valid proof for its commitment.
    /// </summary>
    /// <remarks>Without per-list input, <paramref name="flattened"/> is one list whose commitment is treated as null. An
    /// independent list can still require the same transaction.</remarks>
    /// <param name="flattened">The flattened inclusion list, used only when <paramref name="lists"/> is <c>null</c>.</param>
    /// <param name="spec">The target block's spec, for static validity.</param>
    /// <returns>The union of every list's binding transactions, each once, in first-appearance order.</returns>
    public static Transaction[] SelectEligible(IReadOnlyList<Transaction> flattened, IReadOnlyList<CommitteeInclusionList>? lists,
        IReleaseSpec spec)
    {
        if (lists is null) return SelectEligible(flattened, holdsValidProof: false, spec);
        List<Transaction> eligible = [];
        HashSet<Transaction> added = new(ReferenceEqualityComparer.Instance);
        HashSet<Hash256> addedHashes = [];
        foreach (CommitteeInclusionList list in lists)
            foreach (Transaction tx in SelectEligible(list.Transactions, list.HoldsValidProof, spec))
                if (added.Add(tx) && (tx.Hash is null || addedHashes.Add(tx.Hash))) eligible.Add(tx);
        return [.. eligible];
    }

    /// <summary>One list's binding transactions: every statically valid entry with a held proof, otherwise its statically
    /// valid dependency-free entries, those without a <c>DEP_VERIFY_FRAME_MODE</c> frame.</summary>
    /// <remarks>An entry with a dependency verification frame is dependency-bearing even when the frame carries no triples or
    /// fails static validity, and EIP-7805 excuses its omission as an invalid transaction. An entry that does not decode never
    /// reaches a list, so, like a dependency-bearing one, it is not obligated without a proof.</remarks>
    private static Transaction[] SelectEligible(IReadOnlyList<Transaction> transactions, bool holdsValidProof, IReleaseSpec spec)
    {
        List<Transaction> eligible = new(transactions.Count);
        foreach (Transaction tx in transactions)
            if (IsStaticallyValid(tx, spec) && (holdsValidProof || !Eip8288Dependencies.IsDependencyBearing(tx))) eligible.Add(tx);
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

    private static bool IsStaticallyValid(Transaction? tx, IReleaseSpec spec)
    {
        if (tx is null) return false;
        if (!tx.SupportsFrames) return true;
        return FrameTxValidation.IsWellFormed(tx, spec, out _)
            && FrameTxNonceKeysTxValidator.Instance.IsWellFormed(tx, spec);
    }
}
