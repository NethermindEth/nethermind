// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// Packages inclusion-list transactions with an aggregate dependency proof and its explicit proven set.
/// </summary>
public sealed class InclusionListProofPackage
{
    public required IReadOnlyList<Transaction> Transactions { get; init; }
    public required RecursiveStark RecursiveStark { get; init; }
    public byte[]? ProvenDependencies { get; init; }
}

/// <summary>Validates aggregate dependency proof coverage for inclusion-list transactions.</summary>
public static class InclusionListProofValidator
{
    public const string DepsHashMismatch = "FOCIL recursive STARK public input must equal hash(deps)";
    public const string InvalidProof = "FOCIL recursive STARK failed verification";
    private static readonly ConditionalWeakTable<ILeanProofVerifier, ProofVerdicts> VerifiedProofs = [];

    public static bool Validate(InclusionListProofPackage package, ILeanProofVerifier verifier, out string? error)
        => Validate(package.Transactions, package.RecursiveStark, verifier, out _, out error, provenDependencies: package.ProvenDependencies);

    public static bool Validate(IReadOnlyList<Transaction> transactions, RecursiveStark? proof,
        ILeanProofVerifier verifier, out List<FrameDependency> deps, out string? error, IReleaseSpec? spec = null,
        byte[]? provenDependencies = null)
    {
        deps = [];
        error = null;
        bool requiresProof = false;
        List<List<FrameDependency>> required = [];
        foreach (Transaction tx in transactions)
        {
            if (tx is null) { error = InvalidProof; return false; }
            if (!IsFrameWellFormed(tx, spec, out error)) return false;
            List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(tx);
            required.Add(dependencies);
            requiresProof |= dependencies.Count > 0;
        }
        if (!requiresProof && proof is null && provenDependencies is null) return true;
        if (!ValidateProof(proof, provenDependencies, verifier, out deps, out error)) return false;
        HashSet<FrameDependency> covered = [.. deps];
        foreach (List<FrameDependency> dependencies in required)
            foreach (FrameDependency dependency in dependencies)
                if (!covered.Contains(dependency)) { error = DepsHashMismatch; return false; }
        return true;
    }

    /// <summary>Preserves independent obligations when another entry or its dependency proof is invalid.</summary>
    public static Transaction[] SelectEligible(IReadOnlyList<Transaction> transactions, RecursiveStark? proof,
        byte[]? provenDependencies, ILeanProofVerifier verifier, IReleaseSpec spec, out List<FrameDependency> proven,
        out string? error)
    {
        List<(Transaction Transaction, List<FrameDependency> Dependencies)> candidates = [];
        bool requiresProof = false;
        foreach (Transaction tx in transactions)
        {
            if (tx is null || !IsFrameWellFormed(tx, spec, out _)) continue;
            List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(tx);
            candidates.Add((tx, dependencies));
            requiresProof |= dependencies.Count > 0;
        }
        proven = [];
        error = null;
        bool proofValid = requiresProof && ValidateProof(proof, provenDependencies, verifier, out proven, out error);
        HashSet<FrameDependency> covered = [.. proven];
        List<Transaction> eligible = new(candidates.Count);
        foreach ((Transaction tx, List<FrameDependency> dependencies) in candidates)
        {
            bool includesAll = dependencies.Count == 0 || proofValid;
            if (includesAll && dependencies.Count > 0)
                foreach (FrameDependency dependency in dependencies)
                    if (!covered.Contains(dependency)) { includesAll = false; break; }
            if (includesAll) eligible.Add(tx);
        }
        return [.. eligible];
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

    private static bool ValidateProof(RecursiveStark? proof, byte[]? encodedDependencies, ILeanProofVerifier verifier,
        out List<FrameDependency> dependencies, out string? error)
    {
        dependencies = [];
        error = InvalidProof;
        if (proof?.BlockDepsHash is null || proof.StarkProof is not { Length: > 0 and <= Eip8288Constants.MaxProofBytes }
            || encodedDependencies is null || !HasValidMetadataLength(encodedDependencies)) return false;
        ReadOnlySpan<byte> bytes = encodedDependencies;
        for (int offset = 0; offset < bytes.Length; offset += Eip8288Constants.DependencyTripleLength)
        {
            ReadOnlySpan<byte> triple = bytes.Slice(offset, Eip8288Constants.DependencyTripleLength);
            if (triple[..31].IndexOfAnyExcept((byte)0) >= 0
                || triple[31] is not (Eip8288Constants.LeanSphincsScheme or Eip8288Constants.LeanStarkScheme)) return false;
        }
        List<FrameDependency> parsed = Eip8288Dependencies.Parse(bytes);
        if (!Eip8288Dependencies.Serialize(Eip8288Dependencies.Canonicalize(parsed)).AsSpan().SequenceEqual(bytes)) return false;
        ValueHash256 depsHash = ValueKeccak.Compute(bytes);
        if (proof.BlockDepsHash.ValueHash256 != depsHash) { error = DepsHashMismatch; dependencies = []; return false; }
        InclusionListProofVerifier? request = verifier as InclusionListProofVerifier;
        if (request is null || !request.TryGetVerdict(in depsHash, Eip8288Constants.AggregatedVk, proof.StarkProof, out bool valid))
        {
            ILeanProofVerifier backend = verifier;
            while (backend is InclusionListProofVerifier memo) backend = memo.Backend;
            valid = VerifiedProofs.GetValue(backend, static _ => new()).Verify(backend, in depsHash, proof.StarkProof);
            request?.RememberVerdict(in depsHash, Eip8288Constants.AggregatedVk, proof.StarkProof, valid);
        }
        if (!valid) { dependencies = []; return false; }
        dependencies = parsed;
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

    /// <summary>Checks the byte bound and fixed-width alignment of encoded dependency metadata.</summary>
    public static bool HasValidMetadataLength(byte[] dependencies)
        => dependencies.Length <= Eip8288Constants.MaxInclusionListDependencyBytes
            && dependencies.Length % Eip8288Constants.DependencyTripleLength == 0;
}
