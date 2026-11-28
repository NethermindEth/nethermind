// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>EIP-8141 claim verification: a claim's segregated witness against its resolved dependency.</summary>
/// <remarks>The three protocol verifiers are evaluated natively here, outside the EVM. A custom verifier runs in the
/// pure context, which needs a virtual machine, so <c>TransactionProcessorBase</c> runs it.</remarks>
public static class FrameTxClaimVerifier
{
    public const string InvalidClaim = "frame transaction claim does not verify against its witness";
    public const string MissingWitnesses = "frame transaction carries no witnesses for its claims";
    public const string WitnessCountMismatch = "frame transaction must carry exactly one witness per claim";
    public const string InvalidWitnessLength = "frame transaction claim witness has the wrong length";
    public const string P256NotSupported = "frame transaction P256 claims require the secp256r1 precompile";
    public const string LeanSphincsUnavailable = "frame transaction leanSPHINCS claims require a leanSPHINCS verifier, which is unavailable";

    /// <summary>Whether <paramref name="error"/> rejects a transaction's witnesses rather than its body, which its hash
    /// does not commit to, so a copy carrying other witnesses still has to be judged.</summary>
    public static bool IsWitnessError(string? error) =>
        error is InvalidClaim or MissingWitnesses or WitnessCountMismatch or InvalidWitnessLength;

    /// <summary>The witness a block assigns a leanSPHINCS claim its EIP-8288 aggregated dependency proof has already proven.</summary>
    /// <remarks>Recognized by reference, and only where a block assigned it: <see cref="CheckWitnesses"/> refuses it
    /// unless asked to accept it, so a transaction a reorg returns to the pool is not admitted on it.</remarks>
#pragma warning disable CA1825 // A distinct instance, never Array.Empty, so no decoded witness can be it.
    public static readonly byte[] AggregatedWitness = new byte[0];
#pragma warning restore CA1825

    /// <summary>Address of the secp256r1 (P256VERIFY) precompile — EIP-7951 / RIP-7212.</summary>
    public static readonly Address P256VerifyPrecompileAddress = PrecompiledAddresses.P256Verify;

    /// <summary>The structural checks of the witnesses against the claims: one per claim, each of its resolved length.</summary>
    /// <param name="allowPlaceholders">Accepts an empty witness as a placeholder for a claim that is then not checked;
    /// for simulation, where no signature exists yet.</param>
    /// <param name="acceptAggregated">Accepts <see cref="AggregatedWitness"/> for a leanSPHINCS claim; for a block
    /// whose aggregated dependency proof has been verified.</param>
    public static bool CheckWitnesses(Transaction tx, bool allowPlaceholders, out string? error, bool acceptAggregated = false)
    {
        TxFrameClaim[] claims = tx.FrameClaims ?? [];
        byte[][]? witnesses = tx.ClaimWitnesses;
        if (claims.Length == 0 && witnesses is null or [])
        {
            error = null;
            return true;
        }

        if (witnesses is null)
        {
            return Fail(MissingWitnesses, out error);
        }

        if (witnesses.Length != claims.Length)
        {
            return Fail(WitnessCountMismatch, out error);
        }

        for (int i = 0; i < claims.Length; i++)
        {
            if (acceptAggregated && IsAggregated(claims[i], witnesses[i])) continue;
            if ((ulong)witnesses[i].Length != claims[i].ResolvedWitnessLength && !(allowPlaceholders && witnesses[i].Length == 0))
            {
                return Fail(InvalidWitnessLength, out error);
            }
        }

        error = null;
        return true;
    }

    /// <summary>Verifies the protocol-verifier claims of <paramref name="tx"/>, which need no state, stopping at the first failure.</summary>
    /// <remarks>Assumes <see cref="CheckWitnesses"/> passed without placeholders. Custom-verifier claims are skipped:
    /// they run in the pure context against state.</remarks>
    /// <param name="leanSphincs">The leanSPHINCS verifier, or <see langword="null"/> to skip leanSPHINCS claims.</param>
    /// <param name="preempt">Polled before every verification but the first, so a caller yielding to other work holds
    /// the CPU for at most one more verification.</param>
    /// <param name="preempted">Set when verification stopped early. No verdict was reached then, so the result is
    /// <see langword="false"/> and <paramref name="error"/> is <see langword="null"/>.</param>
    /// <param name="hasSkippedClaims">Whether some claim was skipped and so is still unverified.</param>
    public static bool VerifyProtocolClaims(Transaction tx, IEthereumEcdsa ecdsa, IPrecompile? p256Precompile, ILeanSphincsVerifier? leanSphincs, IReleaseSpec spec,
        Func<bool>? preempt, out bool preempted, out bool hasSkippedClaims, out string? error)
    {
        preempted = false;
        hasSkippedClaims = false;
        error = null;
        TxFrameClaim[] claims = tx.FrameClaims ?? [];
        byte[][] witnesses = tx.ClaimWitnesses ?? [];
        ValueHash256? sigHash = null;
        int verified = 0;
        for (int i = 0; i < claims.Length; i++)
        {
            TxFrameClaim claim = claims[i];
            if (!claim.IsProtocolVerifier || (leanSphincs is null && claim.Verifier == Eip8141Constants.LeanSphincsVerifierAddress))
            {
                hasSkippedClaims = true;
                continue;
            }

            if (verified++ > 0 && preempt?.Invoke() == true)
            {
                preempted = true;
                return false;
            }

            ValueHash256 msg = claim.ResolveMsg(claim.SignsCanonicalHash ? sigHash ??= FrameTxSigHash.ComputeValue(tx) : default);
            if (!VerifyProtocolClaim(claim.Verifier, claim.ResolveKey(tx.SenderAddress!), in msg, witnesses[i], ecdsa, p256Precompile, leanSphincs!, spec, out error))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>EIP-8141 <c>secp256k1_verifier</c>, <c>p256_verifier</c> and <c>leansphincs_verifier</c>.</summary>
    /// <remarks>Assumes <paramref name="verifier"/> is a protocol verifier and <paramref name="witness"/> has its length.</remarks>
    public static bool VerifyProtocolClaim(Address verifier, in ValueHash256 key, in ValueHash256 msg, ReadOnlySpan<byte> witness,
        IEthereumEcdsa ecdsa, IPrecompile? p256Precompile, ILeanSphincsVerifier leanSphincs, IReleaseSpec spec, out string? error)
    {
        error = null;
        bool ok = verifier == Eip8141Constants.Secp256k1VerifierAddress ? VerifySecp256k1(in key, in msg, witness, ecdsa)
            : verifier == Eip8141Constants.P256VerifierAddress ? VerifyP256(in key, in msg, witness, p256Precompile, spec, out error)
            : leanSphincs is UnavailableLeanSphincsVerifier ? Fail(LeanSphincsUnavailable, out error)
            : leanSphincs.Verify(in key, in msg, witness);
        return ok || Fail(error ?? InvalidClaim, out error);
    }

    private static bool VerifySecp256k1(in ValueHash256 key, in ValueHash256 msg, ReadOnlySpan<byte> witness, IEthereumEcdsa ecdsa)
    {
        // v is the recovery id; r and s must be canonical, with low s per EIP-2, so each signature has one encoding.
        UInt256 r = new(witness[1..33], isBigEndian: true);
        UInt256 s = new(witness[33..65], isBigEndian: true);
        if (witness[0] > 1 || r.IsZero || r >= SecP256k1Curve.N || s.IsZero || s > SecP256k1Curve.HalfN) return false;

        Address? signer = ecdsa.RecoverAddress(new Signature(witness[1..33], witness[33..65], (ulong)witness[0] + Signature.VOffset), in msg);
        return signer is not null && signer.Bytes.SequenceEqual(key.Bytes[(ValueHash256.MemorySize - Address.Size)..]);
    }

    private static bool VerifyP256(in ValueHash256 key, in ValueHash256 msg, ReadOnlySpan<byte> witness, IPrecompile? p256Precompile, IReleaseSpec spec, out string? error)
    {
        error = null;
        // P256VERIFY accepts high s, so the EIP-8141 low-s gate has to run here instead.
        UInt256 r = new(witness[..32], isBigEndian: true);
        UInt256 s = new(witness[32..64], isBigEndian: true);
        if (r.IsZero || r >= SecP256r1Curve.N || s.IsZero || s > SecP256r1Curve.HalfN) return false;

        // The key is the address of the public key qx || qy.
        if (!ValueKeccak.Compute(witness[64..128]).Bytes[12..].SequenceEqual(key.Bytes[(ValueHash256.MemorySize - Address.Size)..])) return false;

        if (p256Precompile is null) return Fail(P256NotSupported, out error);

        // Input layout: msg || r || s || qx || qy, verified through P256VERIFY's own code so semantics stay byte-identical.
        const int InputLength = Hash256.Size + Eip8141Constants.P256WitnessLength;
        byte[] input = ArrayPool<byte>.Shared.Rent(InputLength);
        msg.Bytes.CopyTo(input);
        witness.CopyTo(input.AsSpan(Hash256.Size));
        Result<byte[]> result = p256Precompile.Run(input.AsMemory(0, InputLength), spec);
        ArrayPool<byte>.Shared.Return(input);
        return result && result.Data is { Length: > 0 };
    }

    /// <summary>Whether <paramref name="witness"/> stands for <paramref name="claim"/>'s proof in an aggregated dependency proof.</summary>
    public static bool IsAggregated(TxFrameClaim claim, byte[] witness) =>
        ReferenceEquals(witness, AggregatedWitness) && Eip8288Dependencies.IsAggregatable(claim.Verifier);

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
