// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.BeaconChain.Test.Crypto;

internal static class OffSubgroupKeys
{
    // ethereum/bls12-381-tests v0.1.2 deserialization_fails_not_in_G1: an on-curve point outside G1.
    private static readonly byte[] NotInG1 = Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    // ethereum/bls12-381-tests v0.1.2 deserialization_fails_not_in_G2: an on-curve point outside G2.
    private static readonly byte[] NotInG2 = Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    // The G2 cofactor h2: #E'(Fp2) = h2 * r for the BLS12-381 twist.
    private static readonly BigInteger G2Cofactor = BigInteger.Parse("05d543a95414e7f1091d50792876a202cd91de4547085abaa68a205b2e5a7ddfa628f1cb4d9e82ef21537e293a6691ae1616ec6e786f0c70cf1c38e31c7238e5", System.Globalization.NumberStyles.HexNumber);

    // The G1 subgroup order r, little-endian: r times a point keeps only its component outside the subgroup.
    private static readonly byte[] SubgroupOrder = Reversed(Bytes.FromHexString("0x73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001"));

    // [sk]G+T pairs like [sk]G; only subgroup validation detects torsion. Opposite torsion cancels when keys are summed.
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

    public static BlsSignature NotInG2Signature() => new(NotInG2);

    public static BlsSignature WithG2Torsion(BlsSignature signature)
    {
        Bls.P2Affine outside = new(new long[Bls.P2Affine.Sz]);
        if (!outside.TryDecode(NotInG2, out _))
            throw new InvalidOperationException("fixture bug: the vector must decode");

        Bls.P2 point = new(new long[Bls.P2.Sz]);
        point.Add(outside);
        point = MultiplyG2(point, SubgroupOrder);
        if (!point.OnCurve() || point.IsInf() || point.InGroup())
            throw new InvalidOperationException("fixture bug: the torsion point must be on the curve and outside G2");

        if (!MultiplyG2(point, G2Cofactor.ToByteArray(isUnsigned: true)).IsInf())
            throw new InvalidOperationException("fixture bug: the torsion point must be killed by the cofactor");

        Bls.P2 result = new(new long[Bls.P2.Sz]);
        result.Decode(signature.Bytes);
        result.Add(point.ToAffine());
        if (!result.OnCurve() || result.ToAffine().InGroup())
            throw new InvalidOperationException("fixture bug: the signature must leave G2");
        return new BlsSignature(result.Compress());
    }

    // Adding a nonzero twist-cofactor point to a signature leaves G2.
    public static IReadOnlyCollection<byte[]> G2TorsionOfOrder(int prime)
    {
        BigInteger cofactorFree = G2Cofactor * new BigInteger(SubgroupOrder, isUnsigned: true);
        while (cofactorFree % prime == 0)
            cofactorFree /= prime;

        byte[] primeBytes = new BigInteger(prime).ToByteArray(isUnsigned: true);
        List<byte[]> generators = [];
        Dictionary<string, byte[]> span = [];
        byte[] seed = (byte[])NotInG2.Clone();
        for (int last = 0; last < 256 && generators.Count < 2; last++)
        {
            seed[^1] = (byte)last;
            Bls.P2 point = new(new long[Bls.P2.Sz]);
            if (!point.TryDecode(seed, out _))
                continue;

            point = MultiplyG2(point, cofactorFree.ToByteArray(isUnsigned: true));
            while (!point.IsInf() && !MultiplyG2(point, primeBytes).IsInf())
                point = MultiplyG2(point, primeBytes);

            if (point.IsInf() || span.ContainsKey(point.Compress().ToHexString()))
                continue;

            generators.Add(point.Compress());
            span = Span(generators, prime);
        }

        return span.Values;
    }

    private static Dictionary<string, byte[]> Span(List<byte[]> generators, int prime)
    {
        Dictionary<string, byte[]> span = [];
        Bls.P2Affine first = new(new long[Bls.P2Affine.Sz]);
        first.TryDecode(generators[0], out _);
        Bls.P2Affine second = new(new long[Bls.P2Affine.Sz]);
        second.TryDecode(generators[^1], out _);
        Bls.P2 row = new(new long[Bls.P2.Sz]);
        for (int i = 0; i < prime; i++)
        {
            Bls.P2 point = new(new long[Bls.P2.Sz]);
            point.Add(row.ToAffine());
            for (int j = 0; j < (generators.Count > 1 ? prime : 1); j++)
            {
                if (!point.IsInf())
                    span.TryAdd(point.Compress().ToHexString(), point.Compress());
                point.Add(second);
            }

            row.Add(first);
        }

        return span;
    }

    private static Bls.P2 MultiplyG2(Bls.P2 point, ReadOnlySpan<byte> scalar)
    {
        Bls.P2 result = new(new long[Bls.P2.Sz]);
        for (int bit = scalar.Length * 8 - 1; bit >= 0; bit--)
        {
            result.Dbl();
            if ((scalar[bit / 8] & (1 << (bit % 8))) != 0)
                result.Add(point);
        }
        return result;
    }

    private static byte[] Reversed(byte[] bytes)
    {
        Array.Reverse(bytes);
        return bytes;
    }
}
