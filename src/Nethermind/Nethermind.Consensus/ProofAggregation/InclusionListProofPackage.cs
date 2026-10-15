// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// EIP-8288 FOCIL <c>[transactions, recursive_stark]</c>: inclusion-list transactions with an aggregate proof of
/// exactly their dependencies.
/// </summary>
public sealed class InclusionListProofPackage
{
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required RecursiveStark RecursiveStark { get; init; }
}

/// <summary>Validates aggregate dependency proof coverage for inclusion-list transactions.</summary>
public static class InclusionListProofValidator
{
    public const string DepsHashMismatch = "FOCIL recursive STARK public input must equal get_deps_hash(dependencies(transactions))";
    public const string InvalidProof = "FOCIL recursive STARK failed verification";
    private static readonly ConditionalWeakTable<ILeanProofVerifier, ProofVerdicts> VerifiedProofs = [];

    public static bool Validate(InclusionListProofPackage package, ILeanProofVerifier verifier, out List<FrameDependency> deps, out string? error)
        => Validate(package.Transactions, package.RecursiveStark, verifier, out deps, out error);

    public static bool Validate(IReadOnlyList<Transaction> transactions, RecursiveStark? proof,
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
        if (required.Count == 0 && proof is null) return true;
        if (!ValidateProof(proof, required, verifier, out error)) return false;
        deps = required;
        return true;
    }

    /// <summary>
    /// EIP-8288 inclusion enforcement over the counted lists: a list's transactions without dependencies bind even if its
    /// dependency proof is missing or invalid, and its transactions with dependencies bind only if that proof is valid.
    /// </summary>
    /// <remarks>A failed proof affects only its own list, so another list with a valid proof can still require the same
    /// transaction. Each proof covers its list's <c>dependencies(transactions)</c> exactly, so it covers every such entry of
    /// the list or none. Without per-list input, <paramref name="flattened"/> is one list with no proof.</remarks>
    /// <param name="flattened">The flattened inclusion list, used only when <paramref name="lists"/> is <c>null</c>.</param>
    /// <param name="spec">The block's spec, for frame well-formedness.</param>
    /// <param name="proven">Each valid nonempty proof with its dependencies, for the builder's block aggregate.</param>
    /// <returns>The union of every list's binding transactions, each once, in first-appearance order.</returns>
    public static Transaction[] SelectEligible(IReadOnlyList<Transaction> flattened, IReadOnlyList<CommitteeInclusionList>? lists,
        ILeanProofVerifier verifier, IReleaseSpec spec, out List<RecursiveProofInput> proven)
    {
        proven = [];
        if (lists is null) return SelectEligible(flattened, (RecursiveStark?)null, verifier, spec, out List<FrameDependency> _);
        List<Transaction> eligible = [];
        HashSet<Transaction> added = new(ReferenceEqualityComparer.Instance);
        HashSet<Hash256> addedHashes = [];
        foreach (CommitteeInclusionList list in lists)
        {
            Transaction[] selected = SelectEligible(list.Transactions, list.Proof, verifier, spec, out List<FrameDependency> deps);
            foreach (Transaction tx in selected)
                if (added.Add(tx) && (tx.Hash is null || addedHashes.Add(tx.Hash))) eligible.Add(tx);
            if (deps.Count > 0) proven.Add(new RecursiveProofInput(deps.AsReadOnly(), list.Proof!.StarkProof, ValueKeccak.Compute(list.Proof.StarkProof)));
        }
        return [.. eligible];
    }

    /// <summary>One list's binding transactions: every well-formed entry if <paramref name="proof"/> is valid, otherwise only
    /// those without dependencies.</summary>
    private static Transaction[] SelectEligible(IReadOnlyList<Transaction> transactions, RecursiveStark? proof,
        ILeanProofVerifier verifier, IReleaseSpec spec, out List<FrameDependency> proven)
    {
        List<FrameDependency> required = DependenciesOf(transactions);
        proven = [];
        bool proofValid = required.Count > 0 && ValidateProof(proof, required, verifier, out _);
        if (proofValid) proven = required;
        List<Transaction> eligible = new(transactions.Count);
        foreach (Transaction tx in transactions)
        {
            if (tx is null || !IsFrameWellFormed(tx, spec, out _)) continue;
            if (proofValid || Eip8288Dependencies.RecursiveStarkGas(tx) == 0) eligible.Add(tx);
        }
        return [.. eligible];
    }

    /// <summary>Spec <c>dependencies(transactions)</c>: the sorted, deduplicated union over every entry.</summary>
    internal static List<FrameDependency> DependenciesOf(IReadOnlyList<Transaction> transactions)
    {
        List<FrameDependency> declared = [];
        foreach (Transaction tx in transactions)
            if (tx is not null) declared.AddRange(Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue));
        return Eip8288Dependencies.Canonicalize(declared);
    }

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

    /// <summary>The per-list dependency proof checks: <c>deps_hash</c> commits to <paramref name="required"/>, and the proof
    /// passes the EIP-8288 STARK check against it.</summary>
    private static bool ValidateProof(RecursiveStark? proof, List<FrameDependency> required, ILeanProofVerifier verifier, out string? error)
    {
        error = InvalidProof;
        if (proof?.BlockDepsHash is null || proof.StarkProof is not { Length: <= Eip8288Constants.MaxAggregateProofBytes }) return false;
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(required);
        if (proof.BlockDepsHash.ValueHash256 != depsHash) { error = DepsHashMismatch; return false; }
        if (proof.StarkProof.Length == 0 != (required.Count == 0)) return false;
        if (required.Count == 0)
        {
            error = null;
            return true;
        }
        InclusionListProofVerifier? request = verifier as InclusionListProofVerifier;
        if (request is null || !request.TryGetVerdict(in depsHash, Eip8288Constants.AggregatedVk, proof.StarkProof, out bool valid))
        {
            ILeanProofVerifier backend = verifier;
            while (backend is InclusionListProofVerifier memo) backend = memo.Backend;
            valid = VerifiedProofs.GetValue(backend, static _ => new()).Verify(backend, in depsHash, proof.StarkProof);
            request?.RememberVerdict(in depsHash, Eip8288Constants.AggregatedVk, proof.StarkProof, valid);
        }
        if (!valid) return false;
        error = null;
        return true;
    }

    private sealed class ProofVerdicts
    {
        private const int Capacity = 64;
        private readonly object _gate = new();
        private readonly HashSet<(ValueHash256 Dependencies, ValueHash256 Proof)> _verified = [];
        private readonly Queue<(ValueHash256 Dependencies, ValueHash256 Proof)> _order = new();

        public bool Verify(ILeanProofVerifier verifier, in ValueHash256 dependencies, byte[] proof)
        {
            ValueHash256 proofHash = ValueKeccak.Compute(proof);
            (ValueHash256, ValueHash256) key = (dependencies, proofHash);
            lock (_gate)
                if (_verified.Contains(key)) return true;
            if (!verifier.VerifyRecursiveStark(in dependencies, Eip8288Constants.AggregatedVk, proof)) return false;
            // Public callers can mutate proof arrays; never cache a verdict under stale bytes.
            if (ValueKeccak.Compute(proof) != proofHash) return false;
            lock (_gate)
            {
                if (_verified.Add(key))
                {
                    _order.Enqueue(key);
                    if (_order.Count > Capacity) _verified.Remove(_order.Dequeue());
                }
            }
            return true;
        }
    }
}
