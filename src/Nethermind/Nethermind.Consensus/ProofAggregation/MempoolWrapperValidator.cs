// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// Validates an EIP-8288 mempool wrapper per the spec "Mempool Wrapper Object" rules, as carried by EIP-8437: every
/// transaction is a frame transaction within the per-transaction limits, the dependency list is their union, mode 0 stays
/// within the direct witness limits and verifies each witness, and mode 1 stays within <c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c>,
/// whatever the schemes, and carries one recursive STARK that verifies against <c>get_deps_hash(deps)</c>. Every listed
/// transaction declares at least one dependency, so the sorted and deduplicated dependency list is nonempty.
/// </summary>
/// <remarks>Proof checks go through <see cref="ProofVerdictCache"/>, so a known proof arriving with another transaction list
/// is not verified again.</remarks>
public static class MempoolWrapperValidator
{
    public const string UnknownMode = "wrapper mode must be 0 (direct) or 1 (recursive)";
    public const string DepsMismatch = "wrapper deps must be the sorted, deduplicated union of transaction dependencies";
    public const string NotFrameTransaction = "wrapper transactions must be EIP-8141 frame transactions";
    public const string TransactionLimits = "transaction exceeds EIP-8288 dependency limits";
    public const string MalformedDependencyFrame = "wrapper transaction has a malformed dependency verification frame";
    public const string UnknownTransaction = "wrapper transaction hash is unknown";
    public const string TooManySigDeps = "mode 0 wrapper exceeds MAX_LEANSIG_DEPS_PER_DIRECT_WRAPPER";
    public const string TooManyStarkDeps = "mode 0 wrapper exceeds MAX_LEANSTARK_DEPS_PER_DIRECT_WRAPPER";
    public const string TooManyAggregateDeps = "mode 1 wrapper exceeds MAX_DEPS_PER_AGGREGATE_WRAPPER";
    public const string EmptyAggregate = "proof wrapper dependencies must not be empty";
    public const string TooManyTransactions = "wrapper exceeds MAX_TXS_PER_WRAPPER";
    public const string ProofCountMismatch = "mode 0 wrapper must carry exactly one proof per dependency";
    public const string InvalidProof = "a wrapper dependency proof failed verification";
    public const string MissingRecursiveStark = "mode 1 wrapper must carry a recursive STARK";
    public const string DepsHashMismatch = "recursive STARK public input must equal hash(deps)";
    public const string TransactionWithoutDependencies = "every wrapper transaction must declare at least one dependency";

    /// <summary>Fully validates a wrapper whose hash entries resolve through <paramref name="resolveTransaction"/>.</summary>
    /// <param name="proofsVerified">The witnesses or aggregate proof already passed <see cref="VerifyClaimedProofs"/> for these
    /// exact claimed dependencies, so only the transaction checks and dependency union remain.</param>
    /// <param name="forkVerifier">The verifier of the wrapper's target fork.</param>
    public static bool Validate(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, out string? error,
        Func<Hash256, Transaction?>? resolveTransaction = null, bool proofsVerified = false)
    {
        error = null;

        if (wrapper.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive))
        {
            error = UnknownMode;
            return false;
        }

        if (wrapper.Transactions.Count is 0 or > Eip8288Constants.MaxTxsPerWrapper)
        {
            error = TooManyTransactions;
            return false;
        }

        List<FrameDependency> expected = [];
        foreach (WrapperTransaction entry in wrapper.Transactions)
        {
            Transaction? transaction = entry.Full ?? (entry.Hash is null ? null : resolveTransaction?.Invoke(entry.Hash));
            if (transaction is null)
            {
                error = UnknownTransaction;
                return false;
            }
            if (!transaction.SupportsFrames)
            {
                error = NotFrameTransaction;
                return false;
            }
            // Each dependency verification frame needs at least one triple with an enabled scheme; a transaction with no
            // dependency frame is rejected below, as wrappers list only dependency-bearing transactions.
            if (!HasWellFormedDependencyFrames(transaction))
            {
                error = MalformedDependencyFrame;
                return false;
            }
            // Per-transaction limits apply in both modes: a larger aggregate does not admit an individually inadmissible transaction.
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

        return proofsVerified || VerifyClaimedProofs(wrapper, verifier, in forkVerifier, out error);
    }

    /// <summary>
    /// Checks the mode's dependency limits and verifies the witnesses or aggregate proof against the claimed dependencies.
    /// </summary>
    /// <remarks>
    /// A preliminary check, run before missing envelopes are recovered. It does not validate the wrapper: the
    /// proof does not authenticate the transaction list, so <see cref="Validate"/> must still resolve every entry and check
    /// the exact dependency union before admission, reaggregation or relay.
    /// </remarks>
    public static bool VerifyClaimedProofs(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, out string? error)
    {
        error = null;
        if (!CheckModeLimits(wrapper, out error)) return false;
        return wrapper.Mode == MempoolWrapper.ModeDirect
            ? ValidateDirect(wrapper, verifier, in forkVerifier, ref error)
            : ValidateRecursive(wrapper, verifier, in forkVerifier, ref error);
    }

    /// <summary>Whether a mode-1 wrapper within EIP-8288's limits exceeds what the native verifier can check.</summary>
    /// <remarks>EIP-8288 makes this a local refusal, not invalidity. Wrappers beyond EIP-8288's own limits are not counted
    /// here: validation rejects them.</remarks>
    internal static bool ExceedsLocalVerifier(MempoolWrapper wrapper)
        => wrapper.Mode == MempoolWrapper.ModeRecursive && CheckModeLimits(wrapper, out _)
            && LeanProofCapacity.CapacityError(new HashSet<FrameDependency>(wrapper.Deps)) is not null;

    /// <summary>Applies the direct witness limits to mode 0, and a nonempty list within <c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c>,
    /// independent of scheme, to mode 1.</summary>
    private static bool CheckModeLimits(MempoolWrapper wrapper, out string? error)
    {
        error = null;
        if (wrapper.Deps.Count == 0) { error = EmptyAggregate; return false; }
        if (!DepsEqual(Eip8288Dependencies.Canonicalize(wrapper.Deps), wrapper.Deps)) { error = DepsMismatch; return false; }
        foreach (FrameDependency dependency in wrapper.Deps)
            if (!Eip8288Dependencies.IsAcceptedScheme(dependency.Scheme)) { error = InvalidProof; return false; }
        switch (wrapper.Mode)
        {
            case MempoolWrapper.ModeDirect:
                (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(wrapper.Deps);
                if (sphincs > Eip8288Constants.MaxLeanSigDepsPerDirectWrapper) error = TooManySigDeps;
                else if (stark > Eip8288Constants.MaxLeanStarkDepsPerDirectWrapper) error = TooManyStarkDeps;
                break;
            case MempoolWrapper.ModeRecursive:
                if (wrapper.Deps.Count == 0) error = EmptyAggregate;
                else if (wrapper.Deps.Count > Eip8288Constants.MaxDepsPerAggregateWrapper) error = TooManyAggregateDeps;
                break;
            default:
                error = UnknownMode;
                break;
        }
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
