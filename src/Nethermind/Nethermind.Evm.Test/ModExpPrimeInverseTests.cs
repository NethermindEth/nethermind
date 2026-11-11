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

[Parallelizable(ParallelScope.All)]
public class ModExpPrimeInverseTests
{
    private static readonly string[] Primes =
    [
        "30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001",
        "30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47",
        "fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f",
        "fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141",
        "ffffffff00000001000000000000000000000000ffffffffffffffffffffffff",
        "ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551",
    ];

    private static IEnumerable<TestCaseData> PrimeCases()
    {
        foreach (string prime in Primes)
            yield return new TestCaseData(prime) { TestName = $"Prime {prime[..8]}" };
    }

    [TestCaseSource(nameof(PrimeCases))]
    public void Edge_bases_match_exponentiation(string primeHex)
    {
        BigInteger p = Parse(primeHex);
        BigInteger max = (BigInteger.One << 256) - 1;
        BigInteger[] bases = [0, 1, 2, 3, p - 1, p - 2, p, p + 1, 2 * p, 2 * p + 1, max, max - 1, p >> 1, (p >> 1) + 1, BigInteger.One << 255];

        foreach (BigInteger value in bases)
        {
            if (value <= max)
                AssertMatches(p, value, ToBytes(value, 32));
        }
    }

    [TestCaseSource(nameof(PrimeCases))]
    public void Random_bases_match_exponentiation(string primeHex)
    {
        BigInteger p = Parse(primeHex);
        Random random = new(Convert.FromHexString(primeHex)[^1]);
        byte[] bytes = new byte[32];

        for (int i = 0; i < 500; i++)
        {
            random.NextBytes(bytes);
            // Vary the declared base length so short bases are padded the same way as the precompile pads them
            int length = random.Next(0, 33);
            byte[] baseBytes = bytes[(32 - length)..];
            AssertMatches(p, ParseBytes(baseBytes), baseBytes);
        }
    }

    [TestCaseSource(nameof(PrimeCases))]
    public void Power_of_two_bases_match_exponentiation(string primeHex)
    {
        BigInteger p = Parse(primeHex);

        // 2^k and 2^k - 1 give long runs of zero and one bits, the extremes of the divstep batches
        for (int k = 0; k <= 256; k++)
        {
            BigInteger power = BigInteger.One << k;
            if (k < 256)
                AssertMatches(p, power, ToBytes(power, 32));
            AssertMatches(p, power - 1, ToBytes(power - 1, 32));
        }
    }

    [Explicit("Long differential run against BigInteger.ModPow")]
    [TestCaseSource(nameof(PrimeCases))]
    public void Many_random_bases_match_exponentiation(string primeHex)
    {
        BigInteger p = Parse(primeHex);
        byte[] modulus = Convert.FromHexString(primeHex);
        byte[] exponent = ToBytes(p - 2, 32);
        Random random = new(11);
        byte[] baseBytes = new byte[32];

        for (int i = 0; i < 1_000_000; i++)
        {
            random.NextBytes(baseBytes);
            bool inverted = PrimeFieldInverse.TryInvert(baseBytes, exponent, modulus, out byte[]? inverse);
            BigInteger value = ParseBytes(baseBytes);
            if (!inverted || ParseBytes(inverse!) != BigInteger.ModPow(value, p - 2, p))
                Assert.Fail($"Mismatch for base {Convert.ToHexString(baseBytes)}");
        }
    }

    [TestCaseSource(nameof(PrimeCases))]
    public void Other_shapes_take_the_general_path(string primeHex)
    {
        BigInteger p = Parse(primeHex);
        byte[] modulus = Convert.FromHexString(primeHex);
        byte[] baseBytes = ToBytes(5, 32);

        using (Assert.EnterMultipleScope())
        {
            // Exponent p - 1, p - 3 and the modulus p + 2 are not inversions and must not match
            Assert.That(PrimeFieldInverse.TryInvert(baseBytes, ToBytes(p - 1, 32), modulus, out _), Is.False);
            Assert.That(PrimeFieldInverse.TryInvert(baseBytes, ToBytes(p - 3, 32), modulus, out _), Is.False);
            Assert.That(PrimeFieldInverse.TryInvert(baseBytes, ToBytes(p - 2, 32), ToBytes(p + 2, 32), out _), Is.False);
            // The same values with a leading zero byte are a different length
            Assert.That(PrimeFieldInverse.TryInvert(baseBytes, ToBytes(p - 2, 33), modulus, out _), Is.False);
            Assert.That(PrimeFieldInverse.TryInvert(ToBytes(5, 33), ToBytes(p - 2, 32), modulus, out _), Is.False);
        }

        foreach (BigInteger exponent in new[] { p - 1, p - 3, p })
            AssertRun(BuildInput(baseBytes, ToBytes(exponent, 32), modulus), BigInteger.ModPow(5, exponent, p), 32);
    }

    [TestCaseSource(nameof(PrimeCases))]
    public void Inversion_past_the_batch_bound_takes_the_general_path(string primeHex)
    {
        BigInteger p = Parse(primeHex);
        byte[] exponent = ToBytes(p - 2, 32);
        byte[] modulus = Convert.FromHexString(primeHex);
        // A divstep at most halves the larger of f and g, so no nonzero base finishes within one batch of 62
        byte[] baseBytes = ToBytes(p - 1, 32);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PrimeFieldInverse.TryInvert(baseBytes, exponent, modulus, 1, out byte[]? cut), Is.False);
            Assert.That(cut, Is.Null);
            Assert.That(PrimeFieldInverse.TryInvert(baseBytes, exponent, modulus, PrimeFieldInverse.MaxBatches, out byte[]? inverse), Is.True);
            Assert.That(inverse, Is.EqualTo(ToBytes(p - 1, 32)));
        }
    }

    [TestCaseSource(nameof(PrimeCases))]
    public void Longer_encodings_match_exponentiation(string primeHex)
    {
        BigInteger p = Parse(primeHex);
        BigInteger expected = BigInteger.ModPow(7, p - 2, p);

        // Leading zero bytes on any operand take the general path and give the same value
        AssertRun(BuildInput(ToBytes(7, 40), ToBytes(p - 2, 32), ToBytes(p, 32)), expected, 32);
        AssertRun(BuildInput(ToBytes(7, 32), ToBytes(p - 2, 40), ToBytes(p, 32)), expected, 32);
        AssertRun(BuildInput(ToBytes(7, 32), ToBytes(p - 2, 32), ToBytes(p, 40)), expected, 40);
    }

    [TestCaseSource(nameof(PrimeCases))]
    public void Truncated_input_is_zero_padded(string primeHex)
    {
        BigInteger p = Parse(primeHex);
        byte[] full = BuildInput(ToBytes(7, 32), ToBytes(p - 2, 32), ToBytes(p, 32));

        // Dropping the last modulus byte pads it with a zero, which is no longer the prime
        byte[] truncated = full[..^1];
        BigInteger paddedModulus = ParseBytes(ToBytes(p, 32)[..^1]) << 8;
        AssertRun(truncated, BigInteger.ModPow(7, p - 2, paddedModulus), 32);
    }

    private static void AssertMatches(BigInteger p, BigInteger value, byte[] baseBytes)
    {
        byte[] input = BuildInput(baseBytes, ToBytes(p - 2, 32), ToBytes(p, 32));
        AssertRun(input, BigInteger.ModPow(value, p - 2, p), 32);
        Assert.That(PrimeFieldInverse.TryInvert(baseBytes, ToBytes(p - 2, 32), ToBytes(p, 32), out _), Is.True);
    }

    private static void AssertRun(byte[] input, BigInteger expected, int modulusLength)
    {
        foreach (IReleaseSpec spec in new IReleaseSpec[] { Berlin.Instance, Osaka.Instance })
        {
            Result<byte[]> result = ModExpPrecompile.Instance.Run(input, spec);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.IsSuccess, Is.True);
                Assert.That(result.Data, Is.EqualTo(ToBytes(expected, modulusLength)));
            }
        }
    }

    private static byte[] BuildInput(byte[] baseBytes, byte[] exponent, byte[] modulus)
    {
        byte[] input = new byte[96 + baseBytes.Length + exponent.Length + modulus.Length];
        ToBytes(baseBytes.Length, 32).CopyTo(input, 0);
        ToBytes(exponent.Length, 32).CopyTo(input, 32);
        ToBytes(modulus.Length, 32).CopyTo(input, 64);
        baseBytes.CopyTo(input, 96);
        exponent.CopyTo(input, 96 + baseBytes.Length);
        modulus.CopyTo(input, 96 + baseBytes.Length + exponent.Length);
        return input;
    }

    private static BigInteger Parse(string hex) => ParseBytes(Convert.FromHexString(hex));

    private static BigInteger ParseBytes(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] ToBytes(BigInteger value, int length)
    {
        byte[] result = new byte[length];
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (value.IsZero)
            return result;
        raw.CopyTo(result, length - raw.Length);
        return result;
    }
}
