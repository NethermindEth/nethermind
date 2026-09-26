// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>
/// The ZisK guest registers its <c>arith256_mod</c> accelerator for MULMOD, which no ordinary machine has.
/// These run the same build variant off the guest, where the software path answers - the stand-in registered
/// below computes with it too - so they pin the fallback and the registration check, and the agreement
/// between the two paths is what the guest relies on once its accelerator is registered.
/// </summary>
public class GuestModularArithmeticTests
{
    private static readonly UInt256 _max = UInt256.MaxValue;
    private static int _standInCalls;

    [TestCase(7ul, 11ul, 13ul, 12ul)]
    [TestCase(0ul, 5ul, 7ul, 0ul)]
    [TestCase(5ul, 0ul, 7ul, 0ul)]
    [TestCase(6ul, 7ul, 1ul, 0ul)]
    public void MultiplyMod_matches_the_expected_residue(ulong a, ulong b, ulong m, ulong expected)
    {
        ModularArithmetic.MultiplyMod((UInt256)a, (UInt256)b, (UInt256)m, out UInt256 result);

        Assert.That(result, Is.EqualTo((UInt256)expected));
    }

    /// <summary>A zero modulus is zero by the Yellow Paper, where the mathematics has no answer.</summary>
    [Test]
    public void MultiplyMod_of_a_zero_modulus_is_zero()
    {
        ModularArithmetic.MultiplyMod((UInt256)6, (UInt256)7, UInt256.Zero, out UInt256 result);

        Assert.That(result, Is.EqualTo(UInt256.Zero));
    }

    /// <summary>The product overflows 256 bits here, which is the case the accelerator exists for.</summary>
    [Test]
    public void MultiplyMod_reduces_a_512_bit_product()
    {
        ModularArithmetic.MultiplyMod(in _max, in _max, (UInt256)97, out UInt256 result);
        UInt256.MultiplyMod(in _max, in _max, (UInt256)97, out UInt256 expected);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void MultiplyMod_agrees_with_the_software_path_across_random_values([Range(1, 64)] int seed)
    {
        System.Random random = new(seed);
        UInt256 a = Random256(random);
        UInt256 b = Random256(random);
        UInt256 m = Random256(random);

        ModularArithmetic.MultiplyMod(in a, in b, in m, out UInt256 result);
        UInt256.MultiplyMod(in a, in b, in m, out UInt256 expected);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public unsafe void MultiplyMod_goes_through_an_accelerator_that_passes_the_check()
    {
        Assert.That(ModularArithmetic.TryRegisterAccelerator(&StandIn), Is.True);
        int calls = _standInCalls;

        ModularArithmetic.MultiplyMod(in _max, in _max, (UInt256)97, out UInt256 result);
        UInt256.MultiplyMod(in _max, in _max, (UInt256)97, out UInt256 expected);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_standInCalls, Is.EqualTo(calls + 1));
            Assert.That(result, Is.EqualTo(expected));
        }
    }

    [Test]
    public unsafe void MultiplyMod_ignores_an_accelerator_that_fails_the_check()
    {
        Assert.That(ModularArithmetic.TryRegisterAccelerator(&Zero), Is.False);

        ModularArithmetic.MultiplyMod((UInt256)7, (UInt256)11, (UInt256)13, out UInt256 result);

        Assert.That(result, Is.EqualTo((UInt256)12));
    }

    private static UInt256 Random256(System.Random random)
    {
        System.Span<byte> bytes = stackalloc byte[32];
        random.NextBytes(bytes);
        return new UInt256(bytes);
    }

    private static void StandIn(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        _standInCalls++;
        UInt256.MultiplyMod(in a, in b, in m, out result);
    }

    private static void Zero(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result) => result = default;
}
