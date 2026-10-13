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

    private static readonly IReleaseSpec Enabled = new OverridableReleaseSpec(Bogota.Instance) { IsEip8198Enabled = true };
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
    public void Disabled_keeps_one_eighth_rule(ulong parentBaseFee, ulong parentGasUsed)
    {
        UInt256 expected = Reference(parentBaseFee, parentGasUsed, 1, 8);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Calculate(Disabled, parentBaseFee, parentGasUsed), Is.EqualTo(expected));
            Assert.That(Calculate(Bogota.Instance, parentBaseFee, parentGasUsed), Is.EqualTo(expected));
        }
    }

    [Test]
    public void Enabled_ignores_configured_denominator()
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip8198Enabled = true, BaseFeeMaxChangeDenominator = 50 };
        Assert.That(Calculate(spec, 1_000_000_000, BlockGasLimit), Is.EqualTo((UInt256)1_104_166_666));
    }

    [Test]
    public void Overridable_eip1559_spec_carries_flag()
    {
        OverridableEip1559Spec spec = new(Enabled);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.IsEip8198Enabled, Is.True);
            Assert.That(Calculate(spec, 1_000_000_000, 0), Is.EqualTo((UInt256)895_833_334));
        }
    }

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
