// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;
using G2Affine = Nethermind.Crypto.Bls.P2Affine;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>
/// One BLS verification obligation: a (possibly already-aggregated) public key, the message it
/// signs, and the matching signature. This is the unit <see cref="BatchSignatureVerifier"/> and the
/// existing per-call verifiers in <see cref="SignatureSets"/> both operate on - a proposer
/// signature, a RANDAO reveal, one indexed attestation's aggregate, one sync aggregate, and so on
/// each reduce to exactly one set.
/// </summary>
/// <remarks>
/// Construction is the only place encodings are decoded, so it is the only place they are
/// validated: <see cref="TryCreate"/> checks both points are on-curve (via <c>TryDecode</c>), both
/// are in their prime-order subgroup (via <c>InGroup</c> - <c>TryDecode</c> alone does not check
/// this), and the public key is not the point at infinity (an infinity public key pairs trivially
/// with any signature, which would otherwise be a forgery). A signature is allowed to be infinity;
/// that is the spec's own "no participants" placeholder for e.g. an empty sync aggregate.
/// </remarks>
public sealed class BlsSignatureSet
{
    private readonly long[] _publicKey;
    private readonly long[] _signature;

    /// <summary>The domain-separated signing root (or other spec-defined message) the signature covers.</summary>
    public byte[] Message { get; }

    private BlsSignatureSet(long[] publicKey, long[] signature, byte[] message)
    {
        _publicKey = publicKey;
        _signature = signature;
        Message = message;
    }

    public G1Affine PublicKey => new(_publicKey);
    public G2Affine Signature => new(_signature);

    /// <summary>
    /// Decodes and fully validates a compressed public key and signature before pairing anything.
    /// Returns false - never throws - for a malformed encoding, an out-of-subgroup point, or an
    /// infinity public key.
    /// </summary>
    public static bool TryCreate(
        ReadOnlySpan<byte> compressedPublicKey,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> compressedSignature,
        out BlsSignatureSet? set)
    {
        long[] publicKey = new long[G1Affine.Sz];
        G1Affine pk = new(publicKey);
        if (!pk.TryDecode(compressedPublicKey, out _) || pk.IsInf() || !pk.InGroup())
        {
            set = null;
            return false;
        }

        long[] signature = new long[G2Affine.Sz];
        G2Affine sig = new(signature);
        if (!sig.TryDecode(compressedSignature, out _) || !sig.InGroup())
        {
            set = null;
            return false;
        }

        set = new BlsSignatureSet(publicKey, signature, message.ToArray());
        return true;
    }
}
