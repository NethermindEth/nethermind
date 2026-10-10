// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// Validates an EIP-8288 mempool wrapper per the spec "Mempool Wrapper Object" rules and the EIP-8437 kind-1 rules: every
/// transaction is a frame transaction within the per-transaction limits, the dependency list is their union, mode 0 stays
/// within the direct witness limits and verifies each witness, and mode 1 stays within <c>MAX_DEPS_PER_AGGREGATE</c> and
/// <c>MAX_LEANSTARK_DEPS_PER_AGGREGATE</c> and carries one recursive STARK that verifies against <c>get_deps_hash(deps)</c>. A wrapper with
/// empty <c>deps</c> uses mode 0 with no proofs.
/// </summary>
public static class MempoolWrapperValidator
{
    public const string UnknownMode = "wrapper mode must be 0 (direct) or 1 (recursive)";
    public const string DepsMismatch = "wrapper deps must be the sorted, deduplicated union of transaction dependencies";
    public const string NotFrameTransaction = "wrapper transactions must be EIP-8141 frame transactions";
    public const string TransactionLimits = "transaction exceeds EIP-8288 dependency limits";
    public const string UnknownTransaction = "wrapper transaction hash is unknown";
    public const string TooManySigDeps = "mode 0 wrapper exceeds MAX_LEANSIG_DEPS_PER_WRAPPER";
    public const string TooManyStarkDeps = "mode 0 wrapper exceeds MAX_LEANSTARK_DEPS_PER_WRAPPER";
    public const string TooManyAggregateDeps = "mode 1 wrapper exceeds MAX_DEPS_PER_AGGREGATE";
    public const string TooManyAggregateStarkDeps = "mode 1 wrapper exceeds MAX_LEANSTARK_DEPS_PER_AGGREGATE";
    public const string EmptyAggregate = "a wrapper with empty deps must use mode 0";
    public const string TooManyTransactions = "wrapper exceeds MAX_TXS_PER_WRAPPER";
    public const string ProofCountMismatch = "mode 0 wrapper must carry exactly one proof per dependency";
    public const string InvalidProof = "a wrapper dependency proof failed verification";
    public const string MissingRecursiveStark = "mode 1 wrapper must carry a recursive STARK";
    public const string DepsHashMismatch = "recursive STARK public input must equal hash(deps)";

    /// <summary>Fully validates a wrapper whose hash entries resolve through <paramref name="resolveTransaction"/>.</summary>
    /// <param name="proofsVerified">The witnesses or aggregate proof already passed <see cref="VerifyClaimedProofs"/> for these
    /// exact claimed dependencies, so only the transaction checks and dependency union remain.</param>
    public static bool Validate(MempoolWrapper wrapper, ILeanProofVerifier verifier, out string? error, Func<Hash256, Transaction?>? resolveTransaction = null,
        bool proofsVerified = false)
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
            // Per-transaction limits apply in both modes: a larger aggregate does not admit an individually inadmissible transaction.
            List<FrameDependency> transactionDeps = Eip8288Dependencies.Canonicalize(Eip8288Dependencies.ForTransaction(transaction));
            (int transactionSphincs, int transactionStark) = Eip8288Dependencies.CountByScheme(transactionDeps);
            if (transactionSphincs > Eip8288Constants.MaxSigsPerTx || transactionStark > Eip8288Constants.MaxStarksPerTx)
            {
                error = TransactionLimits;
                return false;
            }
            foreach (FrameDependency dependency in transactionDeps)
            {
                if (!Eip8288Dependencies.IsAcceptedScheme(dependency.Scheme))
                {
                    error = InvalidProof;
                    return false;
                }
            }
            expected.AddRange(transactionDeps);
        }
        if (!DepsEqual(Eip8288Dependencies.Canonicalize(expected), wrapper.Deps))
        {
            error = DepsMismatch;
            return false;
        }

        return proofsVerified || VerifyClaimedProofs(wrapper, verifier, out error);
    }

    /// <summary>
    /// Checks the mode's dependency limits and verifies the witnesses or aggregate proof against the claimed dependencies.
    /// </summary>
    /// <remarks>
    /// The EIP-8437 preliminary check, run before missing transactions are fetched. It does not validate the wrapper: the
    /// proof does not authenticate the transaction list, so <see cref="Validate"/> must still resolve every entry and check
    /// the exact dependency union before admission, reaggregation or relay.
    /// </remarks>
    public static bool VerifyClaimedProofs(MempoolWrapper wrapper, ILeanProofVerifier verifier, out string? error)
    {
        error = null;
        if (!CheckModeLimits(wrapper, out error)) return false;
        return wrapper.Mode == MempoolWrapper.ModeDirect
            ? ValidateDirect(wrapper, verifier, ref error)
            : ValidateRecursive(wrapper, verifier, ref error);
    }

    /// <summary>Applies the direct witness limits to mode 0, and a nonempty list within <c>MAX_DEPS_PER_AGGREGATE</c> and
    /// <c>MAX_LEANSTARK_DEPS_PER_AGGREGATE</c> to mode 1.</summary>
    private static bool CheckModeLimits(MempoolWrapper wrapper, out string? error)
    {
        error = null;
        switch (wrapper.Mode)
        {
            case MempoolWrapper.ModeDirect:
                (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(wrapper.Deps);
                if (sphincs > Eip8288Constants.MaxLeanSigDepsPerWrapper) error = TooManySigDeps;
                else if (stark > Eip8288Constants.MaxLeanStarkDepsPerWrapper) error = TooManyStarkDeps;
                break;
            case MempoolWrapper.ModeRecursive:
                if (wrapper.Deps.Count == 0) error = EmptyAggregate;
                else if (wrapper.Deps.Count > Eip8288Constants.MaxDepsPerAggregate) error = TooManyAggregateDeps;
                else if (Eip8288Dependencies.CountByScheme(wrapper.Deps).Stark > Eip8288Constants.MaxLeanStarkDepsPerAggregate)
                    error = TooManyAggregateStarkDeps;
                break;
            default:
                error = UnknownMode;
                break;
        }
        return error is null;
    }

    private static bool ValidateDirect(MempoolWrapper wrapper, ILeanProofVerifier verifier, ref string? error)
    {
        IReadOnlyList<byte[]>? proofs = wrapper.Proofs;
        if (proofs is null || proofs.Count != wrapper.Deps.Count)
        {
            error = ProofCountMismatch;
            return false;
        }

        for (int i = 0; i < wrapper.Deps.Count; i++)
        {
            FrameDependency dep = wrapper.Deps[i];
            bool valid = dep.Scheme switch
            {
                Eip8288Constants.LeanSphincsScheme => verifier.VerifyLeanSphincs(dep.DataHash, dep.VerificationKey, proofs[i]),
                Eip8288Constants.LeanStarkScheme => verifier.VerifyLeanStark(dep.DataHash, dep.VerificationKey, proofs[i]),
                _ => false,
            };
            if (!valid)
            {
                error = InvalidProof;
                return false;
            }
        }

        return true;
    }

    private static bool ValidateRecursive(MempoolWrapper wrapper, ILeanProofVerifier verifier, ref string? error)
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

        if (!RecursiveStarkAggregator.VerifyStarkCheck(verifier, wrapper.Deps.Count, in depsHash, recursiveStark.StarkProof))
        {
            error = InvalidProof;
            return false;
        }

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
