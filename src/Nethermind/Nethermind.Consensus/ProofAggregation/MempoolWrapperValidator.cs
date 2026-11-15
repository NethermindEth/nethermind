// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// Validates an EIP-8288 mempool wrapper per the spec "Mempool Wrapper Object" rules: every transaction is a frame
/// transaction within the per-transaction limits and declares at least one dependency, the dependency list is their union,
/// mode 0 stays within <c>MAX_LEANSIG_DEPS_PER_WRAPPER</c> and <c>MAX_LEANSTARK_DEPS_PER_WRAPPER</c> and carries one verifying
/// proof per dependency, and mode 1 carries one recursive STARK that verifies against <c>get_deps_hash(deps)</c>. Mode 1 has no
/// wrapper-level dependency-count limit; <c>MAX_WRAPPER_BYTES</c>, checked on decoding, bounds it. EIP-8437's mode 2 takes mode
/// 0's limits, and each raw witness must pass the native check of a recognized program whose claim is the dependency's
/// <c>data_hash</c>.
/// </summary>
/// <remarks>Proof checks go through <see cref="ProofVerdictCache"/>, so a known proof arriving with another transaction list
/// is not verified again. Every failure here is a proof or dependency-rule failure, which EIP-8437 discards without
/// penalizing the sender; encoding violations are rejected earlier, by <see cref="MempoolWrapperDecoder"/>.</remarks>
public static class MempoolWrapperValidator
{
    public const string UnknownMode = "wrapper mode must be 0 (direct), 1 (recursive) or 2 (raw)";
    public const string UnrecognizedProgram = "mode 2 raw witness names no recognized program under the target fork";
    public const string InvalidRawWitness = "a mode 2 raw signature or its claim failed the native check";
    public const string DepsMismatch = "wrapper deps must be the sorted, deduplicated union of transaction dependencies";
    public const string NotFrameTransaction = "wrapper transactions must be EIP-8141 frame transactions";
    public const string TransactionLimits = "transaction exceeds EIP-8288 dependency limits";
    public const string MalformedDependencyFrame = "wrapper transaction has a malformed dependency verification frame";
    public const string TooManySigDeps = "mode 0 wrapper exceeds MAX_LEANSIG_DEPS_PER_WRAPPER";
    public const string TooManyStarkDeps = "mode 0 wrapper exceeds MAX_LEANSTARK_DEPS_PER_WRAPPER";
    public const string EmptyAggregate = "proof wrapper dependencies must not be empty";
    public const string ProofCountMismatch = "mode 0 wrapper must carry exactly one proof per dependency";
    public const string InvalidProof = "a wrapper dependency proof failed verification";
    public const string MissingRecursiveStark = "mode 1 wrapper must carry a recursive STARK";
    public const string DepsHashMismatch = "recursive STARK public input must equal hash(deps)";
    public const string TransactionWithoutDependencies = "every wrapper transaction must declare at least one dependency";

    /// <summary>Fully validates a wrapper under <paramref name="forkVerifier"/>, the verifier of its target fork.</summary>
    /// <param name="recognized">The recognized programs a mode-2 wrapper's raw witnesses are checked against; without them, every
    /// raw witness is declined.</param>
    public static bool Validate(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, out string? error,
        IRecognizedProgramVerifier? recognized = null)
    {
        error = null;

        if (wrapper.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive or MempoolWrapper.ModeRaw))
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
            if (!HasWellFormedDependencyFrames(transaction))
            {
                error = MalformedDependencyFrame;
                return false;
            }
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

        return VerifyProofs(wrapper, verifier, in forkVerifier, out error, recognized);
    }

    /// <summary>Checks the dependency limits and verifies the witnesses or aggregate proof against the wrapper's dependencies.</summary>
    /// <remarks>Does not check the transactions against <see cref="MempoolWrapper.Deps"/>; <see cref="Validate"/> does.</remarks>
    public static bool VerifyProofs(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier, out string? error,
        IRecognizedProgramVerifier? recognized = null)
    {
        error = null;
        if (!CheckLimits(wrapper, out error)) return false;
        return wrapper.Mode switch
        {
            MempoolWrapper.ModeDirect => ValidateDirect(wrapper, verifier, in forkVerifier, ref error),
            MempoolWrapper.ModeRaw => ValidateRaw(wrapper, verifier, in forkVerifier, recognized, ref error),
            _ => ValidateRecursive(wrapper, verifier, in forkVerifier, ref error),
        };
    }

    /// <summary>The direct witnesses of a mode-0 or mode-2 wrapper by scheme: every dependency's in mode 0, and in mode 2 those without a
    /// raw entry, since raw entries do not consume the direct-proof allowance (EIP-8437).</summary>
    public static (int Sphincs, int Stark) CountDirect(MempoolWrapper wrapper)
    {
        if (wrapper.Mode != MempoolWrapper.ModeRaw || wrapper.RawWitnesses is not { } raws || raws.Count != wrapper.Deps.Count)
            return Eip8288Dependencies.CountByScheme(wrapper.Deps);
        List<FrameDependency> direct = [];
        for (int i = 0; i < raws.Count; i++)
            if (raws[i] is null) direct.Add(wrapper.Deps[i]);
        return Eip8288Dependencies.CountByScheme(direct);
    }

    /// <summary>Checks the dependency list is nonempty, canonical and of enabled schemes, and applies
    /// <c>MAX_LEANSIG_DEPS_PER_WRAPPER</c> and <c>MAX_LEANSTARK_DEPS_PER_WRAPPER</c> to the direct witnesses of modes 0 and 2.</summary>
    private static bool CheckLimits(MempoolWrapper wrapper, out string? error)
    {
        error = null;
        if (wrapper.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive or MempoolWrapper.ModeRaw)) { error = UnknownMode; return false; }
        if (wrapper.Deps.Count == 0) { error = EmptyAggregate; return false; }
        if (!DepsEqual(Eip8288Dependencies.Canonicalize(wrapper.Deps), wrapper.Deps)) { error = DepsMismatch; return false; }
        foreach (FrameDependency dependency in wrapper.Deps)
            if (!Eip8288Dependencies.IsAcceptedScheme(dependency.Scheme)) { error = InvalidProof; return false; }
        if (wrapper.Mode == MempoolWrapper.ModeRecursive) return true;
        (int sphincs, int stark) = CountDirect(wrapper);
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

    /// <summary>Checks each raw witness natively and verifies the ordinary witnesses beside it as mode 0 does.</summary>
    /// <remarks>Recognized programs publish batch-leaf statements, which only the batch-pricing guest takes, so no raw witness is
    /// recognized under EIP-8288's guest. A recognized check establishes no proof coverage: the caller must keep the wrapper out
    /// of the proof store.</remarks>
    private static bool ValidateRaw(MempoolWrapper wrapper, ILeanProofVerifier verifier, in Eip8288Verifier forkVerifier,
        IRecognizedProgramVerifier? recognized, ref string? error)
    {
        IReadOnlyList<byte[]>? proofs = wrapper.Proofs;
        IReadOnlyList<RawSignature?>? raws = wrapper.RawWitnesses;
        if (proofs is null || raws is null || proofs.Count != wrapper.Deps.Count || raws.Count != proofs.Count)
        {
            error = ProofCountMismatch;
            return false;
        }

        bool batched = forkVerifier.AggregatedVk == Eip8288Verifier.BatchedAggregatedVk;
        for (int i = 0; i < wrapper.Deps.Count; i++)
        {
            FrameDependency dependency = wrapper.Deps[i];
            if (raws[i] is not { } raw) continue;
            if (!batched || recognized is null || !recognized.Recognizes(dependency.VerificationKey))
            {
                error = UnrecognizedProgram;
                return false;
            }
            if (!recognized.Verify(dependency.VerificationKey, raw, out ValueHash256 claim) || claim != dependency.DataHash)
            {
                error = InvalidRawWitness;
                return false;
            }
        }

        for (int i = 0; i < wrapper.Deps.Count; i++)
        {
            if (raws[i] is null && !ProofVerdictCache.VerifyDirect(verifier, in forkVerifier, wrapper.Deps[i], proofs[i]))
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

        if (recursiveStark.BlockDepsHash.ValueHash256 != Eip8288Dependencies.ComputeDepsHash(wrapper.Deps))
        {
            error = DepsHashMismatch;
            return false;
        }

        // The wrapper binds its deps by EIP-8288's digest, as the decoder derives it; the proof's statement is the fork's.
        ValueHash256 depsHash = forkVerifier.DepsHash(wrapper.Deps);
        if (recursiveStark.StarkProof is not { Length: > 0 and <= Eip8288Constants.MaxAggregateProofBytes }
            || !ProofVerdictCache.VerifyRecursive(verifier, in forkVerifier, in depsHash, recursiveStark.StarkProof))
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
