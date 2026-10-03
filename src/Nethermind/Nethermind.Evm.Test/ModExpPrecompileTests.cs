// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class ModExpPrecompileTests : PrecompileTests<ModExpPrecompile, ModExpPrecompileTests>, IPrecompileTests
{
    [TestCase(
        "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000002003fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2efffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f",
        "11",
        TestName = "32-byte modulus result, one trailing byte"
    )]
    [TestCase(
        "000000000000000000000000000000000000000000000000000000000000004000000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000040e09ad9675465c53a109fac66a445c91b292d2bb2c5268addb30cd82f80fcb0033ff97c80a5fc6f39193ae969c6ede6710a6b7ac27078a06d90ef1c72e5c85fb502fc9e1f6beb81516545975218075ec2af118cd8798df6e08a147c60fd6095ac2bb02c2908cf4dd7c81f11c289e4bce98f3553768f392a80ce22bf5c4f4a248c6b",
        "deadbeef",
        TestName = "64-byte modulus result, four trailing bytes"
    )]
    [TestCase(
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000",
        "cafebabe",
        TestName = "baseLength=0 modulusLength=0, exp ignored"
    )]
    [TestCase(
        "00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000ffffffff0000000000000000000000000000000000000000000000000000000000000001",
        "11223344",
        TestName = "modulusLength uint32 overflow path"
    )]
    [TestCase(
        "0000000000000000000000000000000000000000000000000000000000000000ffffffff000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "11223344",
        TestName = "expLength uint32 overflow path"
    )]
    [TestCase(
        "ffffffff000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001",
        "11223344",
        TestName = "baseLength uint32 overflow path"
    )]
    public void NormalizedInput_SameOutput(string input, string trailing)
    {
        RunEffectiveInputTest(input, trailing, Prague.Instance);
        RunEffectiveInputTest(input, trailing, Osaka.Instance);
    }

    [TestCaseSource(nameof(OversizedLengths))]
    public void TestOversizedLengths(string input, string expectedOutput, bool status) =>
        RunTest(input, expectedOutput, status, Prague.Instance);

    static IEnumerable<(string file, IReleaseSpec spec)> IPrecompileTests.TestFilesWithSpec()
    {
        yield return ("modexp_eip2565.json", Prague.Instance);
        yield return ("modexp_eip7883.json", Osaka.Instance);
    }

    public static IEnumerable<TestCaseData<string, string, bool>> OversizedLengths
    {
        get
        {
            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000ffffffff000000000000000000000000000000000000000000000000000000000000000102",
                "00",
                true
            )
            { TestName = "expLen=uint32.MaxValue (0xffffffff): huge expLength wraps modulus offset to base, must return zero (pre-EIP-7823)" };

            yield return new(
                "00000000000000000000000000000000000000000000000000000000ffffffbb00000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000001",
                "00",
                true
            )
            { TestName = "baseLen=uint32.MaxValue-68 (0xffffffbb): huge baseLength wraps exponent offset to header, must return zero (pre-EIP-7823)" };
        }
    }

    /// <summary>
    /// An exponent of p - 2 with a listed prime p is computed as an inverse; the result must match the exponentiation,
    /// whatever the base's size and however the exponent is padded, and a neighbouring exponent must still exponentiate.
    /// </summary>
    [TestCase("30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001", TestName = "Inverse_BN254ScalarField")]
    [TestCase("30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47", TestName = "Inverse_BN254BaseField")]
    [TestCase("fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141", TestName = "Inverse_Secp256k1Order")]
    [TestCase("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff", TestName = "Inverse_Secp256r1BaseField")]
    [TestCase("1a0111ea397fe69a4b1ba7b6434bacd764774b84f38512bf6730d2a0f6b0f6241eabfffeb153ffffb9feffffffffaaab", TestName = "Inverse_Bls12381BaseField")]
    public void PrimeMinusTwoExponent_MatchesTheExponentiation(string primeHex)
    {
        BigInteger p = BigInteger.Parse("0" + primeHex, System.Globalization.NumberStyles.HexNumber);
        byte[] modulus = Convert.FromHexString(primeHex);
        Random random = new(primeHex.Length);
        List<BigInteger> bases = [2, 3, p - 1, p + 2, 2 * p + 5, (BigInteger.One << 400) + 11];
        for (int i = 0; i < 40; i++)
        {
            byte[] b = new byte[random.Next(1, 65)];
            random.NextBytes(b);
            bases.Add(new BigInteger(b, isUnsigned: true, isBigEndian: true));
        }

        foreach (BigInteger exponent in new[] { p - 2, p - 3, p - 1 })
            foreach (int padding in new[] { 0, 5 })
                foreach (BigInteger b in bases)
                {
                    byte[] baseBytes = b.IsZero ? [0] : b.ToByteArray(isUnsigned: true, isBigEndian: true);
                    byte[] exponentBytes = new byte[padding + exponent.GetByteCount(isUnsigned: true)];
                    exponent.ToByteArray(isUnsigned: true, isBigEndian: true).CopyTo(exponentBytes, padding);
                    byte[] input = new byte[96 + baseBytes.Length + exponentBytes.Length + modulus.Length];
                    new BigInteger(baseBytes.Length).ToByteArray(isUnsigned: true, isBigEndian: true).CopyTo(input.AsSpan(32 - new BigInteger(baseBytes.Length).GetByteCount(isUnsigned: true)));
                    new BigInteger(exponentBytes.Length).ToByteArray(isUnsigned: true, isBigEndian: true).CopyTo(input.AsSpan(64 - new BigInteger(exponentBytes.Length).GetByteCount(isUnsigned: true)));
                    new BigInteger(modulus.Length).ToByteArray(isUnsigned: true, isBigEndian: true).CopyTo(input.AsSpan(96 - new BigInteger(modulus.Length).GetByteCount(isUnsigned: true)));
                    baseBytes.CopyTo(input, 96);
                    exponentBytes.CopyTo(input, 96 + baseBytes.Length);
                    modulus.CopyTo(input, 96 + baseBytes.Length + exponentBytes.Length);

                    byte[] expected = new byte[modulus.Length];
                    byte[] power = BigInteger.ModPow(b, exponent, p).ToByteArray(isUnsigned: true, isBigEndian: true);
                    power.CopyTo(expected, expected.Length - power.Length);

                    Result<byte[]> result = ModExpPrecompile.Instance.Run(input, Osaka.Instance);
                    Assert.That(result.Data, Is.EqualTo(expected), $"base {b:x}, exponent p{exponent - p}, padding {padding}");
                }
    }

    [TestCaseSource(nameof(ReducibleBases))]
    public void TestReducibleBase(string input, string expectedOutput, bool status)
    {
        RunTest(input, expectedOutput, status, Prague.Instance);
        RunTest(input, expectedOutput, status, Osaka.Instance);
    }

    /// <summary>Bases that reduce to 0 or 1 modulo the modulus, which skip the exponentiation ladder.</summary>
    public static IEnumerable<TestCaseData<string, string, bool>> ReducibleBases
    {
        get
        {
            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000001070507",
                "00",
                true
            )
            { TestName = "base equal to the modulus reduces to 0" };

            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000001080507",
                "01",
                true
            )
            { TestName = "base one above the modulus reduces to 1" };

            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000001080007",
                "01",
                true
            )
            { TestName = "a zero exponent is 1 even when the base reduces to 1" };

            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000001070007",
                "01",
                true
            )
            { TestName = "0^0 is 1 when the base reduces to 0" };

            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000001080501",
                "00",
                true
            )
            { TestName = "a modulus of 1 always yields 0" };

            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000001000001",
                "00",
                true
            )
            { TestName = "0^0 modulo 1 is 0" };

            yield return new(
                "000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000020fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe",
                "0000000000000000000000000000000000000000000000000000000000000001",
                true
            )
            { TestName = "zkevm worst case: 2^256-1 modulo 2^256-2 reduces to 1" };

            yield return new(
                "000000000000000000000000000000000000000000000000000000000000000800000000000000000000000000000000000000000000000000000000000000400000000000000000000000000000000000000000000000000000000000000008ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                "0000000000000000",
                true
            )
            { TestName = "exp_heavy: base equal to the modulus reduces to 0" };

            yield return new(
                "000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000180000000000000000000000000000000000000000000000000000000000000020ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                "0000000000000000000000000000000000000000000000000000000000000000",
                true
            )
            { TestName = "exp_heavy: a 32-byte base equal to the modulus reduces to 0" };
        }
    }
}
