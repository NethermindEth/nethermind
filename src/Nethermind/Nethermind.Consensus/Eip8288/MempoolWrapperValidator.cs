// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Eip8288;

/// <summary>
/// Validates an EIP-8288 mempool wrapper per the spec "Mempool Wrapper Object" rules: the dependency
/// list must match the wrapped transactions, the per-wrapper leanSPHINCS/leanSTARK limits must hold,
/// and either every direct proof (mode 0) or the single recursive STARK (mode 1) must verify.
/// </summary>
public static class MempoolWrapperValidator
{
    public const string UnknownMode = "wrapper mode must be 0 (direct) or 1 (recursive)";
    public const string DepsMismatch = "wrapper deps must be the sorted, deduplicated union of transaction dependencies";
    public const string UnknownTransaction = "wrapper transaction hash is unknown";
    public const string TooManySigDeps = "wrapper exceeds MAX_LEANSIG_DEPS_PER_WRAPPER";
    public const string TooManyStarkDeps = "wrapper exceeds MAX_LEANSTARK_DEPS_PER_WRAPPER";
    public const string ProofCountMismatch = "mode 0 wrapper must carry exactly one proof per dependency";
    public const string InvalidProof = "a wrapper dependency proof failed verification";
    public const string MissingRecursiveStark = "mode 1 wrapper must carry a recursive STARK";
    public const string DepsHashMismatch = "recursive STARK public input must equal hash(deps)";

    public static bool Validate(MempoolWrapper wrapper, ILeanProofVerifier verifier, out string? error, Func<Hash256, Transaction?>? resolveTransaction = null)
    {
        error = null;

        if (wrapper.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive))
        {
            error = UnknownMode;
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
                error = DepsMismatch;
                return false;
            }
            List<FrameDependency> transactionDeps = Eip8288Dependencies.Canonicalize(Eip8288Dependencies.ForTransaction(transaction));
            (int transactionSphincs, int transactionStark) = Eip8288Dependencies.CountByScheme(transactionDeps);
            if (transactionSphincs > Eip8288Constants.MaxSigsPerTx || transactionStark > Eip8288Constants.MaxStarksPerTx)
            {
                error = "transaction exceeds EIP-8288 dependency limits";
                return false;
            }
            foreach (FrameDependency dependency in transactionDeps)
            {
                if (dependency.Scheme is not (Eip8288Constants.LeanSphincsScheme or Eip8288Constants.LeanStarkScheme))
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

        (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(wrapper.Deps);
        if (sphincs > Eip8288Constants.MaxLeanSigDepsPerWrapper)
        {
            error = TooManySigDeps;
            return false;
        }

        if (stark > Eip8288Constants.MaxLeanStarkDepsPerWrapper)
        {
            error = TooManyStarkDeps;
            return false;
        }

        return wrapper.Mode == MempoolWrapper.ModeDirect
            ? ValidateDirect(wrapper, verifier, ref error)
            : ValidateRecursive(wrapper, verifier, ref error);
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

        if (!verifier.VerifyRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, recursiveStark.StarkProof))
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
