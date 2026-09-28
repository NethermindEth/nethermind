// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.BeaconChain.Test.Crypto;

/// <summary>Points outside their prime-order subgroups: public keys that still verify their secret key's signatures, and signatures outside G2.</summary>
internal static class OffSubgroupKeys
{
    // ethereum/bls12-381-tests v0.1.2 deserialization_fails_not_in_G1: an on-curve point outside G1.
    private static readonly byte[] NotInG1 = Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    // ethereum/bls12-381-tests v0.1.2 deserialization_fails_not_in_G2: an on-curve point outside G2.
    private static readonly byte[] NotInG2 = Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    // The G1 subgroup order r, little-endian: r times a point keeps only its component outside the subgroup.
    private static readonly byte[] SubgroupOrder = Reversed(Bytes.FromHexString("0x73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001"));

    /// <summary>
    /// <c>[sk]G + T</c> for a nonzero torsion point <c>T</c>: it pairs with G2 points as <c>[sk]G</c> does, so
    /// <c>sk</c>'s signatures verify under it, and only a subgroup check tells it apart. With <paramref name="negateTorsion"/>
    /// it is <c>[sk]G - T</c>, whose torsion cancels the other's in a sum.
    /// </summary>
    public static BlsPublicKey WithTorsion(Bls.SecretKey key, bool negateTorsion = false)
    {
        Bls.P1Affine outside = new(new long[Bls.P1Affine.Sz]);
        if (!outside.TryDecode(NotInG1, out _))
            throw new InvalidOperationException("fixture bug: the vector must decode");

        Bls.P1 torsion = new(new long[Bls.P1.Sz]);
        torsion.Add(outside);
        torsion.Mult(SubgroupOrder);
        if (negateTorsion)
            torsion.Neg();

        Bls.P1 point = new(key);
        point.Add(torsion.ToAffine());

        Bls.P1Affine result = point.ToAffine();
        if (torsion.IsInf() || result.IsInf() || result.InGroup())
            throw new InvalidOperationException("fixture bug: the key must be a finite point outside G1");
        return new BlsPublicKey(point.Compress());
    }

    /// <summary>ethereum/bls12-381-tests v0.1.2 <c>deserialization_fails_not_in_G2</c>: an on-curve G2 point outside the subgroup.</summary>
    public static BlsSignature NotInG2Signature() => new(NotInG2);

    /// <summary><paramref name="signature"/> plus a nonzero G2 torsion point: on the curve, outside G2.</summary>
    public static BlsSignature WithG2Torsion(BlsSignature signature)
    {
        Bls.P2Affine outside = new(new long[Bls.P2Affine.Sz]);
        if (!outside.TryDecode(NotInG2, out _))
            throw new InvalidOperationException("fixture bug: the vector must decode");

        Bls.P2 point = new(new long[Bls.P2.Sz]);
        point.Add(outside);
        point.Mult(SubgroupOrder);
        if (point.IsInf())
            throw new InvalidOperationException("fixture bug: the torsion point must be nonzero");

        Bls.P2 result = new(new long[Bls.P2.Sz]);
        result.Decode(signature.Bytes);
        result.Add(point.ToAffine());
        if (result.ToAffine().InGroup())
            throw new InvalidOperationException("fixture bug: the signature must leave G2");
        return new BlsSignature(result.Compress());
    }

    private static byte[] Reversed(byte[] bytes)
    {
        Array.Reverse(bytes);
        return bytes;
    }
}
