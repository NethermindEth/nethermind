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

/// <summary>EIP-8141 <c>validate_signature</c>: every protocol-verified entry must verify before any frame
/// executes. ARBITRARY entries are only structurally checked; their witness is verified by frame code.</summary>
public static class FrameTxSignatureValidator
{
    public const string InvalidSignature = "frame transaction has an invalid signature";
    // Distinct from InvalidSignature so a signer that does not match is told apart from a signature
    // that does not verify, as P256 already does through InvalidP256Signer.
    public const string InvalidSecp256k1Signer = "frame transaction SECP256K1 signer does not match the recovered address";
    public const string InvalidSignatureLength = "frame transaction signature has the wrong length";
    public const string InvalidMsgLength = "frame transaction signature msg must be empty or a 32-byte digest";
    public const string NonCanonicalSignature = "frame transaction signature must use a 0/1 recovery id and a canonical low s value";
    public const string NonCanonicalP256Signature = "frame transaction P256 signature must be canonical with a low s value";
    public const string InvalidP256Signer = "frame transaction P256 signer does not match the public key";
    public const string P256NotSupported = "frame transaction P256 signatures require the secp256r1 precompile";

    /// <summary>Address of the secp256r1 (P256VERIFY) precompile — EIP-7951 / RIP-7212.</summary>
    public static readonly Address P256VerifyPrecompileAddress = PrecompiledAddresses.P256Verify;

    /// <summary>Validation for callers without a sig hash: computed lazily, so a transaction whose
    /// entries all carry an explicit digest never pays for it.</summary>
    public static bool Validate(Transaction tx, IEthereumEcdsa ecdsa, IPrecompile? p256Precompile, IReleaseSpec spec, out string? error)
    {
        ValueHash256? sigHash = null;
        return Validate(tx, ref sigHash, ecdsa, p256Precompile, spec, out error, allowEmptySignatures: false, skipVerification: false);
    }

    /// <summary>Validation that stops before the next verifying entry once <paramref name="preempt"/> returns
    /// <see langword="true"/>, so a caller yielding to other work holds the CPU for at most one more verification.</summary>
    /// <param name="preempted">Set when validation stopped early. No verdict was reached then, so the result is
    /// <see langword="false"/> and <paramref name="error"/> is <see langword="null"/>.</param>
    public static bool Validate(Transaction tx, IEthereumEcdsa ecdsa, IPrecompile? p256Precompile, IReleaseSpec spec, Func<bool>? preempt, out bool preempted, out string? error)
    {
        ValueHash256? sigHash = null;
        return Validate(tx, ref sigHash, ecdsa, p256Precompile, spec, out error, allowEmptySignatures: false, skipVerification: false, preempt, out preempted);
    }

    /// <summary>Same validation, optionally accepting a SECP256K1 or P256 entry with empty signature bytes as a
    /// placeholder. Simulation can also skip signature verification while retaining structural checks.</summary>
    /// <param name="sigHash">The canonical signature hash when the caller has it; otherwise <see langword="null"/>,
    /// set here only if an entry signs it.</param>
    /// <remarks>With <paramref name="skipVerification"/> only the length and the SECP256K1 recovery id are checked:
    /// execution-apis#907 exempts signature checks from eth_simulateV1, so placeholder bytes need not be a
    /// canonical signature nor, for P256, carry the signer's public key.</remarks>
    internal static bool Validate(Transaction tx, ref ValueHash256? sigHash, IEthereumEcdsa ecdsa, IPrecompile? p256Precompile, IReleaseSpec spec, out string? error, bool allowEmptySignatures, bool skipVerification) =>
        Validate(tx, ref sigHash, ecdsa, p256Precompile, spec, out error, allowEmptySignatures, skipVerification, preempt: null, out _);

    private static bool Validate(Transaction tx, ref ValueHash256? sigHash, IEthereumEcdsa ecdsa, IPrecompile? p256Precompile, IReleaseSpec spec, out string? error, bool allowEmptySignatures, bool skipVerification, Func<bool>? preempt, out bool preempted)
    {
        error = null;
        preempted = false;
        TxFrameSignature[]? signatures = tx.FrameSignatures;
        if (signatures is null || signatures.Length == 0) return true;

        for (int i = 0; i < signatures.Length; i++)
        {
            TxFrameSignature signature = signatures[i];
            if (signature.Scheme == TxFrameSignature.SchemeArbitrary)
            {
                continue; // structurally checked in FrameTxValidation; the witness is verified by frame code
            }

            // eth_call/estimateGas/simulate arrive unvalidated, and ValueHash256(span) reads 32 bytes
            // unchecked, so a shorter non-empty Msg would over-read.
            if (!signature.Msg.IsEmpty && signature.Msg.Length != Hash256.Size)
            {
                return Fail(InvalidMsgLength, out error);
            }

            if (allowEmptySignatures && signature.Signature.IsEmpty)
            {
                if (signature.Scheme == TxFrameSignature.SchemeSecp256k1) continue;
                if (signature.Scheme == TxFrameSignature.SchemeP256)
                {
                    if (p256Precompile is null) return Fail(P256NotSupported, out error);
                    continue;
                }
                return Fail(InvalidSignature, out error);
            }

            // Polled per entry rather than once, since the elliptic-curve work is what a caller yields.
            if (preempt?.Invoke() == true)
            {
                preempted = true;
                return false;
            }

            ValueHash256 message = signature.Msg.IsEmpty ? sigHash ??= FrameTxSigHash.ComputeValue(tx) : new ValueHash256(signature.Msg.Span);
            Address resolvedSigner = signature.Signer ?? tx.SenderAddress!;

            bool ok = signature.Scheme switch
            {
                TxFrameSignature.SchemeSecp256k1 => ValidateSecp256k1(signature, resolvedSigner, in message, ecdsa, skipVerification, out error),
                TxFrameSignature.SchemeP256 => ValidateP256(signature, resolvedSigner, in message, p256Precompile, spec, skipVerification, out error),
                _ => Fail(InvalidSignature, out error),
            };

            if (!ok) return false;
        }

        return true;
    }

    /// <summary>Recovers the signer of every well-formed SECP256K1 entry into <see cref="TxFrameSignature.Recovered"/>,
    /// so a later validation of the same transaction skips the recovery.</summary>
    /// <remarks>Never decides validity: an entry is still fully checked when the transaction executes.</remarks>
    public static void RecoverSecp256k1Signers(Transaction tx, IEthereumEcdsa ecdsa)
    {
        TxFrameSignature[]? signatures = tx.FrameSignatures;
        if (signatures is null) return;

        ValueHash256 sigHash = default;
        bool sigHashComputed = false;
        foreach (TxFrameSignature signature in signatures)
        {
            ReadOnlySpan<byte> raw = signature.Signature.Span;
            if (signature.Scheme != TxFrameSignature.SchemeSecp256k1
                || (!signature.Msg.IsEmpty && signature.Msg.Length != Hash256.Size)
                || raw.Length != TxFrameSignature.Secp256k1SignatureLength
                || raw[0] > 1
                || !HasCanonicalSecp256k1RS(raw))
            {
                continue;
            }

            if (signature.Msg.IsEmpty && !sigHashComputed)
            {
                sigHash = FrameTxSigHash.ComputeValue(tx);
                sigHashComputed = true;
            }

            RecoverSecp256k1(signature, raw, signature.Msg.IsEmpty ? sigHash : new ValueHash256(signature.Msg.Span), ecdsa);
        }
    }

    /// <summary>Whether every SECP256K1 entry carries a recovered signer.</summary>
    public static bool Secp256k1SignersRecovered(Transaction tx)
    {
        foreach (TxFrameSignature signature in tx.FrameSignatures ?? [])
        {
            if (signature.Scheme == TxFrameSignature.SchemeSecp256k1 && signature.Recovered is null) return false;
        }

        return true;
    }

    private static bool HasCanonicalSecp256k1RS(ReadOnlySpan<byte> raw)
    {
        UInt256 r = new(raw[1..33], isBigEndian: true);
        UInt256 s = new(raw[33..65], isBigEndian: true);
        return !(r.IsZero || r >= SecP256k1Curve.N || s.IsZero || s > SecP256k1Curve.HalfN);
    }

    private static Address? RecoverSecp256k1(TxFrameSignature signature, ReadOnlySpan<byte> raw, in ValueHash256 message, IEthereumEcdsa ecdsa)
    {
        if (signature.Recovered is { } cached && cached.Message == message) return cached.Signer;

        Address? recovered = ecdsa.RecoverAddress(new Signature(raw[1..33], raw[33..65], (ulong)raw[0] + Signature.VOffset), in message);
        if (recovered is not null) signature.Recovered = new TxFrameSignature.SignerRecovery(message, recovered);
        return recovered;
    }

    private static bool ValidateSecp256k1(TxFrameSignature signature, Address resolvedSigner, in ValueHash256 message, IEthereumEcdsa ecdsa, bool skipVerification, out string? error)
    {
        error = null;
        ReadOnlySpan<byte> raw = signature.Signature.Span;
        if (raw.Length != TxFrameSignature.Secp256k1SignatureLength) return Fail(InvalidSignatureLength, out error);

        ulong v = raw[0];
        if (v > 1) return Fail(NonCanonicalSignature, out error);
        if (skipVerification) return true;

        if (!HasCanonicalSecp256k1RS(raw))
        {
            return Fail(NonCanonicalSignature, out error);
        }

        // Split as the P256 arm is: recovery failing outright is the signature not verifying, and only a
        // recovered address that differs from the signer is a signer mismatch. The canonicality gate above
        // bounds r but cannot make it a curve x-coordinate, so a null recovery is reachable.
        Address? recovered = RecoverSecp256k1(signature, raw, in message, ecdsa);
        if (recovered is null) return Fail(InvalidSignature, out error);
        return recovered == resolvedSigner || Fail(InvalidSecp256k1Signer, out error);
    }

    private static bool ValidateP256(TxFrameSignature signature, Address resolvedSigner, in ValueHash256 message, IPrecompile? p256Precompile, IReleaseSpec spec, bool skipVerification, out string? error)
    {
        error = null;
        ReadOnlySpan<byte> raw = signature.Signature.Span;
        if (raw.Length != TxFrameSignature.P256SignatureLength) return Fail(InvalidSignatureLength, out error);
        if (skipVerification) return p256Precompile is not null || Fail(P256NotSupported, out error);

        // P256VERIFY accepts high-s, so the EIP-8141 low-s gate has to run here instead.
        UInt256 r = new(raw[..32], isBigEndian: true);
        UInt256 s = new(raw[32..64], isBigEndian: true);
        if (r.IsZero || r >= SecP256r1Curve.N || s.IsZero || s > SecP256r1Curve.HalfN)
        {
            return Fail(NonCanonicalP256Signature, out error);
        }

        ReadOnlySpan<byte> publicKey = raw[64..128]; // qx || qy
        Address derived = new(ValueKeccak.Compute(publicKey).Bytes[12..]);
        if (derived != resolvedSigner) return Fail(InvalidP256Signer, out error);

        // Verified through the EVM's own P256VERIFY so semantics stay byte-identical.
        // Input layout: message || r || s || qx || qy.
        if (p256Precompile is null) return Fail(P256NotSupported, out error);

        const int InputLength = Hash256.Size + TxFrameSignature.P256SignatureLength;
        byte[] input = ArrayPool<byte>.Shared.Rent(InputLength);
        message.Bytes.CopyTo(input);
        raw.CopyTo(input.AsSpan(Hash256.Size));

        Result<byte[]> result = p256Precompile.Run(input.AsMemory(0, InputLength), spec);
        ArrayPool<byte>.Shared.Return(input);
        return result && result.Data is { Length: > 0 } || Fail(InvalidSignature, out error);
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
