// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// Validates an EIP-8288 mempool wrapper per the spec "Mempool Wrapper Object" rules: every transaction is a frame
/// transaction within the per-transaction limits and declares at least one dependency, the dependency list is their union,
/// both modes stay within <c>MAX_LEANSIG_DEPS_PER_WRAPPER</c> and <c>MAX_LEANSTARK_DEPS_PER_WRAPPER</c>, mode 0 carries one
/// verifying proof per dependency, and mode 1 carries one recursive STARK that verifies against <c>get_deps_hash(deps)</c>.
/// </summary>
/// <remarks>Proof checks go through <see cref="ProofVerdictCache"/>, so a known proof arriving with another transaction list
/// is not verified again. Every failure here is a proof or dependency-rule failure, which EIP-8437 discards without
/// penalizing the sender; encoding violations are rejected earlier, by <see cref="MempoolWrapperDecoder"/>.</remarks>
public static class MempoolWrapperValidator
{
    public const string UnknownMode = "wrapper mode must be 0 (direct) or 1 (recursive)";
    public const string DepsMismatch = "wrapper deps must be the sorted, deduplicated union of transaction dependencies";
    public const string NotFrameTransaction = "wrapper transactions must be EIP-8141 frame transactions";
    public const string TransactionLimits = "transaction exceeds EIP-8288 dependency limits";
    public const string MalformedDependencyFrame = "wrapper transaction has a malformed dependency verification frame";
    public const string TooManySigDeps = "wrapper exceeds MAX_LEANSIG_DEPS_PER_WRAPPER";
    public const string TooManyStarkDeps = "wrapper exceeds MAX_LEANSTARK_DEPS_PER_WRAPPER";
    public const string EmptyAggregate = "proof wrapper dependencies must not be empty";
    public const string ProofCountMismatch = "mode 0 wrapper must carry exactly one proof per dependency";
    public const string InvalidProof = "a wrapper dependency proof failed verification";
    public const string MissingRecursiveStark = "mode 1 wrapper must carry a recursive STARK";
    public const string DepsHashMismatch = "recursive STARK public input must equal hash(deps)";
    public const string TransactionWithoutDependencies = "every wrapper transaction must declare at least one dependency";

    /// <summary>Fully validates a wrapper under <paramref name="forkVerifier"/>, the verifier of its target fork.</summary>
    public static bool Validate(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, out string? error)
    {
        error = null;

        if (wrapper.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive))
        {
            error = UnknownMode;
            return false;
        }

        List<FrameDependency> expected = [];
        foreach (Transaction transaction in wrapper.Transactions)
        {
            if (!transaction.SupportsFrames)
            {
                error = NotFrameTransaction;
                return false;
            }
            // Each dependency verification frame needs at least one triple with an enabled scheme.
            if (!HasWellFormedDependencyFrames(transaction))
            {
                error = MalformedDependencyFrame;
                return false;
            }
            // Per-transaction limits apply in both modes.
            List<FrameDependency> transactionDeps = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue);
            if (transactionDeps.Count == 0)
            {
                error = TransactionWithoutDependencies;
                return false;
            }
            (int transactionSphincs, int transactionStark) = Eip8288Dependencies.CountByScheme(transactionDeps);
            if (transactionSphincs > Eip8288Constants.MaxSigsPerTx || transactionStark > Eip8288Constants.MaxStarksPerTx)
            {
                error = TransactionLimits;
                return false;
            }
            expected.AddRange(transactionDeps);
        }
        if (!DepsEqual(Eip8288Dependencies.Canonicalize(expected), wrapper.Deps))
        {
            error = DepsMismatch;
            return false;
        }

        return VerifyProofs(wrapper, verifier, in forkVerifier, out error);
    }

    /// <summary>Checks the dependency limits and verifies the witnesses or aggregate proof against the wrapper's dependencies.</summary>
    /// <remarks>Does not check the transactions against <see cref="MempoolWrapper.Deps"/>; <see cref="Validate"/> does.</remarks>
    public static bool VerifyProofs(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, out string? error)
    {
        error = null;
        if (!CheckLimits(wrapper, out error)) return false;
        return wrapper.Mode == MempoolWrapper.ModeDirect
            ? ValidateDirect(wrapper, verifier, in forkVerifier, ref error)
            : ValidateRecursive(wrapper, verifier, in forkVerifier, ref error);
    }

    /// <summary>Applies <c>MAX_LEANSIG_DEPS_PER_WRAPPER</c> and <c>MAX_LEANSTARK_DEPS_PER_WRAPPER</c> to a nonempty, canonical
    /// dependency list, in either mode.</summary>
    private static bool CheckLimits(MempoolWrapper wrapper, out string? error)
    {
        error = null;
        if (wrapper.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive)) { error = UnknownMode; return false; }
        if (wrapper.Deps.Count == 0) { error = EmptyAggregate; return false; }
        if (!DepsEqual(Eip8288Dependencies.Canonicalize(wrapper.Deps), wrapper.Deps)) { error = DepsMismatch; return false; }
        foreach (FrameDependency dependency in wrapper.Deps)
            if (!Eip8288Dependencies.IsAcceptedScheme(dependency.Scheme)) { error = InvalidProof; return false; }
        (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(wrapper.Deps);
        if (sphincs > Eip8288Constants.MaxLeanSigDepsPerWrapper) error = TooManySigDeps;
        else if (stark > Eip8288Constants.MaxLeanStarkDepsPerWrapper) error = TooManyStarkDeps;
        return error is null;
    }

    private static bool ValidateDirect(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, ref string? error)
    {
        IReadOnlyList<byte[]>? proofs = wrapper.Proofs;
        if (proofs is null || proofs.Count != wrapper.Deps.Count)
        {
            error = ProofCountMismatch;
            return false;
        }

        for (int i = 0; i < wrapper.Deps.Count; i++)
        {
            if (proofs[i] is null || !ProofVerdictCache.VerifyDirect(verifier, in forkVerifier, wrapper.Deps[i], proofs[i]))
            {
                error = InvalidProof;
                return false;
            }
        }

        return true;
    }

    private static bool ValidateRecursive(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, ref string? error)
    {
        RecursiveStark? recursiveStark = wrapper.RecursiveStark;
        if (recursiveStark is null)
        {
            error = MissingRecursiveStark;
            return false;
        }

        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(wrapper.Deps);
        if (recursiveStark.BlockDepsHash.ValueHash256 != depsHash)
        {
            error = DepsHashMismatch;
            return false;
        }

        if (recursiveStark.StarkProof is not { Length: > 0 and <= Eip8288Constants.MaxAggregateProofBytes }
            || !ProofVerdictCache.VerifyRecursive(verifier, in forkVerifier, in depsHash, recursiveStark.StarkProof, wrapper.Deps))
        {
            error = InvalidProof;
            return false;
        }

        return true;
    }

    private static bool HasWellFormedDependencyFrames(Transaction transaction)
    {
        foreach (TxFrame frame in transaction.Frames ?? [])
            if (Eip8288Dependencies.IsDependencyFrame(frame) && !FrameTxValidation.IsWellFormedDependencyData(frame.Data.Span, out _)) return false;
        return true;
    }

    private static bool DepsEqual(IReadOnlyList<FrameDependency> a, IReadOnlyList<FrameDependency> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i])) return false;
        }

        return true;
    }
}
