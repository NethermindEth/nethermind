// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Validates the protocol-verified signatures of an EIP-8141 frame transaction (spec
/// <c>validate_signature</c>): every entry must verify before any frame executes. SECP256K1 recovers
/// and compares to the resolved signer; P256 checks the key-derived address and the secp256r1
/// precompile; ARBITRARY entries are only structurally checked (verified later by frame code).
/// Pure and stateless — reads only the transaction.
/// </summary>
public static class FrameTxSignatureValidator
{
    public const string InvalidSignature = "frame transaction has an invalid signature";
    public const string InvalidSignatureLength = "frame transaction signature has the wrong length";
    public const string NonCanonicalSignature = "frame transaction signature must use a 0/1 recovery id and a canonical low s value";
    public const string InvalidP256Signer = "frame transaction P256 signer does not match the public key";
    public const string P256NotSupported = "frame transaction P256 signatures are not yet supported by the prototype pre-flight";

    public static bool Validate(Transaction tx, in ValueHash256 sigHash, IEthereumEcdsa ecdsa, out string? error)
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

            ValueHash256 message = signature.Msg.IsEmpty ? sigHash : new ValueHash256(signature.Msg.Span);
            Address resolvedSigner = signature.Signer ?? tx.SenderAddress!;

            bool ok = signature.Scheme switch
            {
                TxFrameSignature.SchemeSecp256k1 => ValidateSecp256k1(signature, resolvedSigner, in message, ecdsa, out error),
                TxFrameSignature.SchemeP256 => ValidateP256(signature, resolvedSigner, in message, out error),
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

    private static bool ValidateP256(TxFrameSignature signature, Address resolvedSigner, in ValueHash256 message, out string? error)
    {
        error = null;
        ReadOnlySpan<byte> raw = signature.Signature.Span;
        if (raw.Length != TxFrameSignature.P256SignatureLength) return Fail(InvalidSignatureLength, out error);

        ReadOnlySpan<byte> publicKey = raw.Slice(64, 64); // qx || qy
        Address derived = new(Keccak.Compute(publicKey).Bytes[12..]);
        if (derived != resolvedSigner) return Fail(InvalidP256Signer, out error);

        // EIP8141-GAP: the secp256r1 verification primitive lives in a package referenced by
        // Nethermind.Evm.Precompiles, which sits ABOVE Nethermind.Evm (precompiles are invoked
        // through an injected provider, not a direct reference). P256 frame-signature verification
        // must therefore route through that provider — deferred to a later slice. secp256k1 default
        // code covers every milestone example in the meantime.
        _ = message;
        return Fail(P256NotSupported, out error);
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
