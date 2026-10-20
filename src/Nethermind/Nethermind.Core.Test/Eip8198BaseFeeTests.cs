// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>EIP-8198 lowers the maximum per-block base fee change from 1/8 to 5/48.</summary>
[TestFixture]
public class Eip8198BaseFeeTests
{
    private const ulong BlockGasLimit = 30_000_000;
    private const ulong GasTarget = BlockGasLimit / 2;

    private static readonly IReleaseSpec Enabled = Bogota.Instance;
    private static readonly IReleaseSpec Disabled = new OverridableReleaseSpec(Bogota.Instance) { IsEip8198Enabled = false };

    private static readonly ulong[] BaseFees = [7, 9, 10, 19, 20, 1_000_000_000, 1_000_000_007];

    private static readonly ulong[] GasUsedCases =
        [0, GasTarget - 1, GasTarget, GasTarget + 1, BlockGasLimit, 100_000, BlockGasLimit - 100_000];

    // Vectors from execution-specs test_base_fee_per_gas_calculator_bogota.
    [TestCase(1_000_000_000UL, 30_000_000UL, 1_104_166_666UL, TestName = "Full block")]
    [TestCase(1_000_000_000UL, 0UL, 895_833_334UL, TestName = "Empty block")]
    [TestCase(9UL, 30_000_000UL, 10UL, TestName = "One wei increase floor")]
    [TestCase(9UL, 0UL, 9UL, TestName = "Decrease truncated to zero")]
    [TestCase(20UL, 29_900_000UL, 21UL, TestName = "Near full")]
    [TestCase(20UL, 100_000UL, 19UL, TestName = "Near empty")]
    public void Matches_execution_specs_vectors(ulong parentBaseFee, ulong parentGasUsed, ulong expected) =>
        Assert.That(Calculate(Enabled, parentBaseFee, parentGasUsed), Is.EqualTo((UInt256)expected));

    [TestCaseSource(nameof(Matrix))]
    public void Enabled_follows_five_forty_eighths_rule(ulong parentBaseFee, ulong parentGasUsed) =>
        Assert.That(Calculate(Enabled, parentBaseFee, parentGasUsed), Is.EqualTo(Reference(parentBaseFee, parentGasUsed, 5, 48)));

    [TestCaseSource(nameof(Matrix))]
    public void Earlier_forks_keep_one_eighth_rule(ulong parentBaseFee, ulong parentGasUsed)
    {
        UInt256 expected = Reference(parentBaseFee, parentGasUsed, 1, 8);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Calculate(Disabled, parentBaseFee, parentGasUsed), Is.EqualTo(expected));
            Assert.That(Calculate(Amsterdam.Instance, parentBaseFee, parentGasUsed), Is.EqualTo(expected));
            Assert.That(Calculate(London.Instance, parentBaseFee, parentGasUsed), Is.EqualTo(expected));
        }
    }

    [Test]
    public void Bogota_sets_five_forty_eighths_and_earlier_forks_one_eighth()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enabled.BaseFeeMaxChangeNumerator, Is.EqualTo((UInt256)5));
            Assert.That(Enabled.BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)48));
            Assert.That(Amsterdam.Instance.BaseFeeMaxChangeNumerator, Is.EqualTo(UInt256.One));
            Assert.That(Amsterdam.Instance.BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)8));
        }
    }

    [Test]
    public void Flag_alone_gives_five_forty_eighths_on_an_earlier_fork()
    {
        IReleaseSpec flagOnly = new OverridableReleaseSpec(London.Instance) { IsEip8198Enabled = true };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flagOnly.BaseFeeMaxChangeNumerator, Is.EqualTo((UInt256)5));
            Assert.That(flagOnly.BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)48));
            Assert.That(Calculate(flagOnly, 1_000_000_000, BlockGasLimit), Is.EqualTo((UInt256)1_104_166_666));
        }
    }

    [TestCase(1UL, 50UL)]
    [TestCase(5UL, 50UL)]
    [TestCase(1UL, 250UL)]
    public void Calculator_applies_the_spec_fraction(ulong numerator, ulong denominator)
    {
        IEip1559Spec spec = new OverridableEip1559Spec(London.Instance)
        {
            BaseFeeMaxChangeNumerator = numerator,
            BaseFeeMaxChangeDenominator = denominator
        };

        using (Assert.EnterMultipleScope())
        {
            foreach (ulong gasUsed in GasUsedCases)
            {
                Assert.That(Calculate(spec, 1_000_000_007, gasUsed), Is.EqualTo(Reference(1_000_000_007, gasUsed, (int)numerator, (int)denominator)));
            }
        }
    }

    [Test]
    public void Enabled_ignores_configured_denominator()
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip8198Enabled = true, BaseFeeMaxChangeDenominator = 50 };
        Assert.That(Calculate(spec, 1_000_000_000, BlockGasLimit), Is.EqualTo((UInt256)1_104_166_666));
    }

    [Test]
    public void Overridable_eip1559_spec_carries_the_fraction()
    {
        OverridableEip1559Spec spec = new(Enabled);
        OverridableEip1559Spec overridden = new(Enabled) { BaseFeeMaxChangeNumerator = 1, BaseFeeMaxChangeDenominator = 8 };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Calculate(spec, 1_000_000_000, 0), Is.EqualTo((UInt256)895_833_334));
            Assert.That(Calculate(overridden, 1_000_000_000, 0), Is.EqualTo((UInt256)875_000_000));
        }
    }

    [TestCase(10_000UL, 12_000UL, 1UL, 8UL, 5UL, 48UL, TestName = "ScaledToSlot_10s_is_five_forty_eighths")]
    [TestCase(8_000UL, 12_000UL, 1UL, 8UL, 1UL, 12UL, TestName = "ScaledToSlot_8s_is_one_twelfth")]
    [TestCase(12_000UL, 12_000UL, 1UL, 8UL, 1UL, 8UL, TestName = "ScaledToSlot_12s_is_one_eighth")]
    [TestCase(6_000UL, 12_000UL, 1UL, 8UL, 1UL, 16UL, TestName = "ScaledToSlot_6s_is_one_sixteenth")]
    [TestCase(10_000UL, 12_000UL, 2UL, 8UL, 5UL, 24UL, TestName = "ScaledToSlot_reduces_a_non_unit_base")]
    [TestCase(5_000UL, 10_000UL, 5UL, 48UL, 5UL, 96UL, TestName = "ScaledToSlot_from_another_reference")]
    public void ScaledToSlot_keeps_the_per_second_rate_in_lowest_terms(
        ulong designMs, ulong referenceMs, ulong baseNumerator, ulong baseDenominator, ulong numerator, ulong denominator)
    {
        (UInt256 actualNumerator, UInt256 actualDenominator) = BaseFeeMaxChange.ScaledToSlot(designMs, referenceMs, baseNumerator, baseDenominator);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualNumerator, Is.EqualTo((UInt256)numerator));
            Assert.That(actualDenominator, Is.EqualTo((UInt256)denominator));
        }
    }

    [Test]
    public void ScaledToSlot_of_the_eip8198_design_slot_is_five_forty_eighths() =>
        Assert.That(BaseFeeMaxChange.ScaledToSlot(Eip8198Constants.DesignSlotDurationMs), Is.EqualTo(((UInt256)5, (UInt256)48)));

    private static IEnumerable<TestCaseData> Matrix()
    {
        foreach (ulong baseFee in BaseFees)
            foreach (ulong gasUsed in GasUsedCases)
                yield return new TestCaseData(baseFee, gasUsed).SetName($"BaseFee {baseFee}, gas used {gasUsed}");
    }

    private static UInt256 Calculate(IEip1559Spec spec, ulong parentBaseFee, ulong parentGasUsed)
    {
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(2001)
            .WithGasLimit(BlockGasLimit)
            .WithGasUsed(parentGasUsed)
            .WithBaseFee(parentBaseFee)
            .TestObject;
        return BaseFeeCalculator.Calculate(parent, spec);
    }

    /// <summary>execution-specs <c>calculate_base_fee_per_gas</c> with unbounded integers.</summary>
    private static UInt256 Reference(ulong parentBaseFee, ulong parentGasUsed, int numerator, int denominator)
    {
        BigInteger baseFee = parentBaseFee;
        BigInteger result;
        if (parentGasUsed == GasTarget)
        {
            result = baseFee;
        }
        else if (parentGasUsed > GasTarget)
        {
            BigInteger targetFeeGasDelta = baseFee * (parentGasUsed - GasTarget) / GasTarget;
            result = baseFee + BigInteger.Max(targetFeeGasDelta * numerator / denominator, 1);
        }
        else
        {
            BigInteger targetFeeGasDelta = baseFee * (GasTarget - parentGasUsed) / GasTarget;
            result = baseFee - targetFeeGasDelta * numerator / denominator;
        }

        return (ulong)result;
    }
}
