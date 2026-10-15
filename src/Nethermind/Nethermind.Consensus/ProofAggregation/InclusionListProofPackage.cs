// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

/// <summary>EIP-8288 inclusion-list dependency proofs and the obligations their signed commitments impose (EIPs#12475).</summary>
/// <remarks>
/// A signed list carries <c>recursive_stark = [stark_proof_hash, deps_hash]</c> or null; the proof is a separate
/// consensus-layer object. The consensus layer counts a committed list only once its proof verified, through
/// <see cref="VerifyCommitment"/>, and passes the Engine API each counted list's transactions with that proof, or null for a
/// null commitment. Obligations then follow from the signed data alone.
/// </remarks>
public static class InclusionListProofValidator
{
    public const string DepsHashMismatch = "FOCIL recursive STARK public input must equal get_deps_hash(dependencies(transactions))";
    public const string InvalidProof = "FOCIL recursive STARK failed verification";
    public const string EmptyDependencies = "a list without dependencies must carry a null recursive_stark";
    public const string EmptyProof = "an empty stark_proof is never a valid inclusion-list proof";
    public const string OversizedProof = "stark_proof exceeds MAX_AGGREGATE_PROOF_BYTES";

    /// <summary>Validates a self-contained <c>[transactions, [stark_proof, deps_hash]]</c> package submitted to the local mempool.</summary>
    public static bool Validate(InclusionListProofPackage package, ILeanProofVerifier verifier, out List<FrameDependency> deps, out string? error)
        => Validate(package.Transactions, package.RecursiveStark, verifier, out deps, out error);

    /// <summary>Validates a package whose every entry must be well formed, against its proof.</summary>
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
        if (!VerifyCommitment(required, proof.BlockDepsHash.ValueHash256, proof.StarkProof, verifier, out error)) return false;
        deps = required;
        return true;
    }

    /// <summary>
    /// Whether a list's non-null <c>recursive_stark</c> is valid for <paramref name="transactions"/>: its <c>deps</c> are
    /// nonempty, <c>deps_hash == get_deps_hash(deps)</c>, and <paramref name="starkProof"/> verifies against it.
    /// </summary>
    /// <remarks>Backs <c>engine_verifyInclusionListProofV1</c>. <c>deps</c> is the union over the statically valid entries
    /// only, so an undecodable or statically invalid entry contributes none and cannot invalidate the proof. A null
    /// commitment has no proof to verify.</remarks>
    public static bool VerifyCommitment(IReadOnlyList<Transaction> transactions, in ValueHash256 depsHash, byte[] starkProof,
        ILeanProofVerifier verifier, IReleaseSpec spec, out string? error)
        => VerifyCommitment(StaticallyValidDependencies(transactions, spec), depsHash, starkProof, verifier, out error);

    private static bool VerifyCommitment(List<FrameDependency> deps, ValueHash256? depsHash, byte[]? starkProof,
        ILeanProofVerifier verifier, out string? error)
    {
        error = null;
        if (deps.Count == 0) error = EmptyDependencies;
        else if (depsHash is not { } claimed || claimed != Eip8288Dependencies.ComputeDepsHash(deps)) error = DepsHashMismatch;
        else if (starkProof is not { Length: > 0 }) error = EmptyProof;
        // Checked before proof decoding or verification.
        else if (starkProof.Length > Eip8288Constants.MaxAggregateProofBytes) error = OversizedProof;
        else if (!ProofVerdictCache.VerifyRecursive(verifier, in claimed, starkProof)) error = InvalidProof;
        return error is null;
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

    /// <summary>The counted lists' proofs a builder can fold into its block aggregate, each with the dependencies it covers.</summary>
    /// <remarks>A committed list whose proof is empty, oversized or fails against its statically valid dependencies is left
    /// out; its obligations stand regardless, so <paramref name="unusable"/> reports it for the builder to surface.</remarks>
    public static List<RecursiveProofInput> ProvenInputs(IReadOnlyList<CommitteeInclusionList> lists, ILeanProofVerifier verifier,
        IReleaseSpec spec, out List<ulong> unusable)
    {
        List<RecursiveProofInput> proven = [];
        unusable = [];
        foreach (CommitteeInclusionList list in lists)
        {
            if (list.StarkProof is not { } proof) continue;
            List<FrameDependency> deps = StaticallyValidDependencies(list.Transactions, spec);
            if (deps.Count == 0) continue;
            if (!VerifyCommitment(deps, Eip8288Dependencies.ComputeDepsHash(deps), proof, verifier, out _))
            {
                unusable.Add(list.CommitteeIndex);
                continue;
            }
            proven.Add(new RecursiveProofInput(deps.AsReadOnly(), proof, ValueKeccak.Compute(proof)));
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
