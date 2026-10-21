// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.MclBindings;
using NUnit.Framework;
using static Nethermind.MclBindings.Mcl;

namespace Nethermind.Evm.Test;

/// <remarks>
/// The precompile forwarders (<see cref="BN254AddPrecompile"/>, <see cref="BN254MulPrecompile"/>) always
/// normalise call data to the exact length before invoking <see cref="BN254"/>, so these length guards are
/// unreachable through the public surface. They are exercised directly here because a violated length would
/// otherwise read or write past the backing buffer through the raw pointers inside <c>BN254</c>.
/// </remarks>
public class BN254Tests
{
    // 128-byte input / 64-byte output vector from BN254AddPrecompileTests.
    private const string ValidAddInput =
        "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b3625f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd5850b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cf";

    // 96-byte input / 64-byte output vector from BN254MulPrecompileTests.
    private const string ValidMulInput =
        "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    [Test]
    public void Add_rejects_wrong_input_length([Values(0, 64, 96, 127, 129, 192)] int inputLength) =>
        Assert.That(BN254.Add(new byte[64], new byte[inputLength]), Is.Not.Null);

    [Test]
    public void Add_rejects_short_output([Values(0, 63)] int outputLength) =>
        Assert.That(BN254.Add(new byte[outputLength], new byte[128]), Is.Not.Null);

    [Test]
    public void Add_accepts_exact_or_oversized_output([Values(64, 128)] int outputLength) =>
        Assert.That(BN254.Add(new byte[outputLength], Bytes.FromHexString(ValidAddInput)), Is.Null);

    [Test]
    public void Mul_rejects_wrong_input_length([Values(0, 64, 95, 97, 128)] int inputLength) =>
        Assert.That(BN254.Mul(new byte[64], new byte[inputLength]), Is.Not.Null);

    [Test]
    public void Mul_rejects_short_output([Values(0, 63)] int outputLength) =>
        Assert.That(BN254.Mul(new byte[outputLength], new byte[96]), Is.Not.Null);

    [Test]
    public void Mul_accepts_exact_or_oversized_output([Values(64, 128)] int outputLength) =>
        Assert.That(BN254.Mul(new byte[outputLength], Bytes.FromHexString(ValidMulInput)), Is.Null);

    [Test]
    public unsafe void IsInG2_agrees_with_the_generic_order_check(
        [Range(0, 15)] int seed,
        [Values(BN254PairingCheckPrecompileTests.G2NotInSubgroup, BN254PairingCheckPrecompileTests.G2OfOrder10069)] string offset)
    {
        RuntimeHelpers.RunClassConstructor(typeof(BN254).TypeHandle);

        // hashAndMapTo clears the cofactor and leaves the point in Jacobian coordinates
        mclBnG2 point = default;
        mclBnG2_hashAndMapTo(ref point, (byte*)&seed, sizeof(int));
        mclBnG2 shifted = default;
        mclBnG2_add(ref shifted, point, ParseG2(offset));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mclBnG2_isValidOrder(point), Is.EqualTo(1));
            Assert.That(BN254.IsInG2(point), Is.True);
            Assert.That(mclBnG2_isValidOrder(shifted), Is.Zero);
            Assert.That(BN254.IsInG2(shifted), Is.False);
        }
    }

    [Test]
    public void IsInG2_kernel_meets_the_twist_only_in_G2()
    {
        BigInteger x = BN254.Seed;
        BigInteger p = 36 * BigInteger.Pow(x, 4) + 36 * BigInteger.Pow(x, 3) + 24 * x * x + 6 * x + 1;
        BigInteger r = 36 * BigInteger.Pow(x, 4) + 36 * BigInteger.Pow(x, 3) + 18 * x * x + 6 * x + 1;
        BigInteger t = 6 * x * x + 1;

        // (x + 1) + xψ + xψ² - 2xψ³ reduced to a + bψ with ψ² = tψ - p, so ψ³ = (t² - p)ψ - tp
        BigInteger a = x + 1 - x * p + 2 * x * t * p;
        BigInteger b = x + x * t - 2 * x * (t * t - p);
        BigInteger degree = a * a + a * b * t + b * b * p;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(p, Is.EqualTo(BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208583")));
            Assert.That(r, Is.EqualTo(BigInteger.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617")));
            Assert.That(degree % r, Is.EqualTo(BigInteger.Zero));
            Assert.That(BigInteger.GreatestCommonDivisor(degree, 2 * p - r), Is.EqualTo(BigInteger.One));
        }
    }

    /// <summary>Parses the EIP-197 encoding x.im, x.re, y.im, y.re (each 32 bytes, big-endian) into an affine point.</summary>
    private static unsafe mclBnG2 ParseG2(string hex)
    {
        const int fieldHexLength = 64;

        mclBnG2 point = default;
        SetFp(ref point.x.d1, hex.AsSpan(0, fieldHexLength));
        SetFp(ref point.x.d0, hex.AsSpan(fieldHexLength, fieldHexLength));
        SetFp(ref point.y.d1, hex.AsSpan(2 * fieldHexLength, fieldHexLength));
        SetFp(ref point.y.d0, hex.AsSpan(3 * fieldHexLength, fieldHexLength));
        mclBnFp_setInt32(ref point.z.d0, 1);
        return point;

        static void SetFp(ref mclBnFp fp, ReadOnlySpan<char> value)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(value.ToArray());
            fixed (byte* ptr = ascii)
                Assert.That(mclBnFp_setStr(ref fp, ptr, (nuint)ascii.Length, 16), Is.Zero);
        }
    }
}
