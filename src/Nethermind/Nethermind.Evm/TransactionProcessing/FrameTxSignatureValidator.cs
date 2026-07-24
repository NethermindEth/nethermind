// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Validates the protocol-verified signatures of an EIP-8141 frame transaction (spec
/// <c>validate_signature</c>): every entry must verify before any frame executes. SECP256K1 recovers
/// and compares to the resolved signer; P256 checks the key-derived address then verifies through the
/// secp256r1 (P256VERIFY) precompile; ARBITRARY entries are only structurally checked (verified later
/// by frame code). Deterministic — reads the transaction plus the injected verification primitives.
/// </summary>
public static class FrameTxSignatureValidator
{
    public const string InvalidSignature = "frame transaction has an invalid signature";
    public const string InvalidSignatureLength = "frame transaction signature has the wrong length";
    public const string InvalidMsgLength = "frame transaction signature msg must be empty or a 32-byte digest";
    public const string NonCanonicalSignature = "frame transaction signature must use a 0/1 recovery id and a canonical low s value";
    public const string NonCanonicalP256Signature = "frame transaction P256 signature must be canonical with a low s value";
    public const string InvalidP256Signer = "frame transaction P256 signer does not match the public key";
    public const string P256NotSupported = "frame transaction P256 signatures require the secp256r1 precompile";

    /// <summary>Address of the secp256r1 (P256VERIFY) precompile — EIP-7951 / RIP-7212.</summary>
    public static readonly Address P256VerifyPrecompileAddress = Address.FromNumber(0x100);

    public static bool Validate(Transaction tx, in ValueHash256 sigHash, IEthereumEcdsa ecdsa, IPrecompile? p256Precompile, IReleaseSpec spec, out string? error)
    {
        error = null;
        TxFrameSignature[]? signatures = tx.FrameSignatures;
        if (signatures is null || signatures.Length == 0) return true;

        for (int i = 0; i < signatures.Length; i++)
        {
            TxFrameSignature signature = signatures[i];
            if (signature.Scheme == TxFrameSignature.SchemeArbitrary)
            {
                continue; // structurally checked in FrameTxValidation; the witness is verified by frame code
            }

            // Msg is either empty (canonical hash) or a 32-byte digest. FrameTxValidation enforces
            // this for mempool/block txs, but eth_call/estimateGas/simulate reach the processor with
            // an unvalidated FrameTx, and ValueHash256(span) reads 32 bytes unchecked — a shorter
            // non-empty Msg would over-read. Guard the length on the untrusted input.
            if (!signature.Msg.IsEmpty && signature.Msg.Length != Hash256.Size)
            {
                return Fail(InvalidMsgLength, out error);
            }

            ValueHash256 message = signature.Msg.IsEmpty ? sigHash : new ValueHash256(signature.Msg.Span);
            Address resolvedSigner = signature.Signer ?? tx.SenderAddress!;

            bool ok = signature.Scheme switch
            {
                TxFrameSignature.SchemeSecp256k1 => ValidateSecp256k1(signature, resolvedSigner, in message, ecdsa, out error),
                TxFrameSignature.SchemeP256 => ValidateP256(signature, resolvedSigner, in message, p256Precompile, spec, out error),
                _ => Fail(InvalidSignature, out error),
            };

            if (!ok) return false;
        }

        return true;
    }

    private static bool ValidateSecp256k1(TxFrameSignature signature, Address resolvedSigner, in ValueHash256 message, IEthereumEcdsa ecdsa, out string? error)
    {
        error = null;
        ReadOnlySpan<byte> raw = signature.Signature.Span;
        if (raw.Length != TxFrameSignature.Secp256k1SignatureLength) return Fail(InvalidSignatureLength, out error);

        // EIP8141-GAP: the spec passes v straight to ecrecover without pinning its encoding or the
        // signature's canonicality. Enforced strictly — a 0/1 recovery id and a low s — so every
        // signature has exactly one valid byte encoding (no mempool/tx-shape malleability through
        // the explicit-digest entries, whose bytes stay in the sig-hash preimage). Upstream issue:
        // pin the ecrecover semantics in the spec.
        ulong v = raw[0];
        if (v > 1) return Fail(NonCanonicalSignature, out error);

        UInt256 r = new(raw.Slice(1, 32), isBigEndian: true);
        UInt256 s = new(raw.Slice(33, 32), isBigEndian: true);
        if (r.IsZero || r >= SecP256k1Curve.N || s.IsZero || s > SecP256k1Curve.HalfN)
        {
            return Fail(NonCanonicalSignature, out error);
        }

        Signature ecdsaSignature = new(raw.Slice(1, 32), raw.Slice(33, 32), v + Signature.VOffset);
        Address? recovered = ecdsa.RecoverAddress(ecdsaSignature, in message);
        return recovered is not null && recovered == resolvedSigner || Fail(InvalidSignature, out error);
    }

    private static bool ValidateP256(TxFrameSignature signature, Address resolvedSigner, in ValueHash256 message, IPrecompile? p256Precompile, IReleaseSpec spec, out string? error)
    {
        error = null;
        ReadOnlySpan<byte> raw = signature.Signature.Span;
        if (raw.Length != TxFrameSignature.P256SignatureLength) return Fail(InvalidSignatureLength, out error);

        // P256VERIFY itself accepts a high-s signature, so the canonicality gate must run here: r and s
        // must be in range with low-s, giving each signature exactly one encoding (no tx-hash malleability).
        UInt256 r = new(raw.Slice(0, 32), isBigEndian: true);
        UInt256 s = new(raw.Slice(32, 32), isBigEndian: true);
        if (r.IsZero || r >= SecP256r1Curve.N || s.IsZero || s > SecP256r1Curve.HalfN)
        {
            return Fail(NonCanonicalP256Signature, out error);
        }

        ReadOnlySpan<byte> publicKey = raw.Slice(64, 64); // qx || qy
        Address derived = new(Keccak.Compute(publicKey).Bytes[12..]);
        if (derived != resolvedSigner) return Fail(InvalidP256Signer, out error);

        // The secp256r1 primitive is packaged above Nethermind.Evm and reached through the same
        // P256VERIFY precompile the EVM uses, so verification stays byte-identical to on-chain
        // secp256r1 semantics. Input layout: message || r || s || qx || qy (32 + P256 signature).
        if (p256Precompile is null) return Fail(P256NotSupported, out error);

        byte[] input = new byte[Hash256.Size + TxFrameSignature.P256SignatureLength];
        message.Bytes.CopyTo(input);
        raw.CopyTo(input.AsSpan(Hash256.Size));

        Result<byte[]> result = p256Precompile.Run(input, spec);
        return result && result.Data.Length > 0 || Fail(InvalidSignature, out error);
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
