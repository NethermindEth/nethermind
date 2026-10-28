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

/// <summary>
/// EIP-8198 derives the maximum per-block base fee change from the spacing of a block and its parent:
/// <c>seconds_elapsed / (8 × 12 × slots_elapsed)</c>, capped at the EIP-1559 maximum change.
/// </summary>
[TestFixture]
public class Eip8198BaseFeeTests
{
    private const ulong BlockGasLimit = 30_000_000;
    private const ulong GasTarget = BlockGasLimit / 2;
    private const ulong ParentTimestamp = 1_000_000;
    private const ulong ParentSlot = 1_000;

    private static readonly IReleaseSpec Enabled = Bogota.Instance;
    private static readonly IReleaseSpec Disabled = new OverridableReleaseSpec(Bogota.Instance) { IsEip8198Enabled = false };

    private static readonly ulong[] BaseFees = [7, 9, 10, 19, 20, 1_000_000_000, 1_000_000_007];

    private static readonly ulong[] GasUsedCases =
        [0, GasTarget - 1, GasTarget, GasTarget + 1, BlockGasLimit, 100_000, BlockGasLimit - 100_000];

    // Vectors from execution-specs#3873 test_base_fee_per_gas_calculator_bogota, ten-second slots with and without missed slots.
    [TestCase(1_000_000_000UL, 30_000_000UL, 1_104_166_666UL, TestName = "Full block")]
    [TestCase(1_000_000_000UL, 0UL, 895_833_334UL, TestName = "Empty block")]
    [TestCase(9UL, 30_000_000UL, 10UL, TestName = "One wei increase floor")]
    [TestCase(9UL, 0UL, 9UL, TestName = "Decrease truncated to zero")]
    [TestCase(20UL, 29_900_000UL, 21UL, TestName = "Near full")]
    [TestCase(20UL, 100_000UL, 19UL, TestName = "Near empty")]
    public void Matches_execution_specs_vectors(ulong parentBaseFee, ulong parentGasUsed, ulong expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Calculate(Enabled, parentBaseFee, parentGasUsed, 10, 1), Is.EqualTo((UInt256)expected), "10s");
            Assert.That(Calculate(Enabled, parentBaseFee, parentGasUsed, 20, 2), Is.EqualTo((UInt256)expected), "10s_missed_slot");
            Assert.That(Calculate(Enabled, parentBaseFee, parentGasUsed, 50, 5), Is.EqualTo((UInt256)expected), "10s_four_missed_slots");
        }
    }

    // seconds_elapsed / 96 per slot, which is exact for these slot durations.
    [TestCase(12UL, 1, 8, TestName = "12s slots give 1/8")]
    [TestCase(10UL, 5, 48, TestName = "10s slots give 5/48")]
    [TestCase(8UL, 1, 12, TestName = "8s slots give 1/12")]
    [TestCase(6UL, 1, 16, TestName = "6s slots give 1/16")]
    public void Slot_duration_sets_the_maximum_change(ulong slotSeconds, int numerator, int denominator)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach ((ulong baseFee, ulong gasUsed) in Matrix())
            {
                UInt256 expected = Reference(baseFee, gasUsed, numerator, denominator);
                Assert.That(Calculate(Enabled, baseFee, gasUsed, slotSeconds, 1), Is.EqualTo(expected), $"{baseFee} {gasUsed}");
                Assert.That(Calculate(Enabled, baseFee, gasUsed, slotSeconds * 3, 3), Is.EqualTo(expected), $"{baseFee} {gasUsed}, two missed slots");
            }
        }
    }

    [TestCase(12UL, 1UL, TestName = "12s")]
    [TestCase(24UL, 2UL, TestName = "12s_missed_slot")]
    [TestCase(120UL, 10UL, TestName = "12s_nine_missed_slots")]
    public void Twelve_second_slots_keep_the_eip1559_rule(ulong secondsElapsed, ulong slotsElapsed)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach ((ulong baseFee, ulong gasUsed) in Matrix())
            {
                Assert.That(Calculate(Enabled, baseFee, gasUsed, secondsElapsed, slotsElapsed),
                    Is.EqualTo(Calculate(Amsterdam.Instance, baseFee, gasUsed, secondsElapsed, slotsElapsed)), $"{baseFee} {gasUsed}");
            }
        }
    }

    [Test]
    public void Earlier_forks_ignore_block_spacing()
    {
        using (Assert.EnterMultipleScope())
        {
            foreach ((ulong baseFee, ulong gasUsed) in Matrix())
            {
                UInt256 expected = Reference(baseFee, gasUsed, 1, 8);
                Assert.That(Calculate(Disabled, baseFee, gasUsed, 10, 1), Is.EqualTo(expected));
                Assert.That(Calculate(Amsterdam.Instance, baseFee, gasUsed, 10, 1), Is.EqualTo(expected));
                Assert.That(Calculate(London.Instance, baseFee, gasUsed, 6, 1), Is.EqualTo(expected));
            }
        }
    }

    [Test]
    public void Bogota_enables_eip8198_and_keeps_the_denominator()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enabled.IsEip8198Enabled, Is.True);
            Assert.That(Enabled.BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)8));
            Assert.That(Amsterdam.Instance.IsEip8198Enabled, Is.False);
            Assert.That(Amsterdam.Instance.BaseFeeMaxChangeDenominator, Is.EqualTo((UInt256)8));
        }
    }

    [Test]
    public void Flag_alone_derives_the_change_on_an_earlier_fork()
    {
        IReleaseSpec flagOnly = new OverridableReleaseSpec(London.Instance) { IsEip8198Enabled = true };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Calculate(flagOnly, 1_000_000_000, BlockGasLimit, 10, 1), Is.EqualTo((UInt256)1_104_166_666));
            Assert.That(Calculate(flagOnly, 1_000_000_000, BlockGasLimit, 12, 1), Is.EqualTo((UInt256)1_125_000_000));
        }
    }

    [Test]
    public void Configured_denominator_is_scaled_by_block_spacing()
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { BaseFeeMaxChangeDenominator = 50 };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Calculate(spec, 1_200_000_000, BlockGasLimit, 12, 1), Is.EqualTo((UInt256)1_224_000_000));
            Assert.That(Calculate(spec, 1_200_000_000, BlockGasLimit, 10, 1), Is.EqualTo((UInt256)1_220_000_000));
            Assert.That(Calculate(spec, 1_200_000_000, BlockGasLimit, 120, 1), Is.EqualTo((UInt256)1_224_000_000));
        }
    }

    [Test]
    public void Overridable_eip1559_spec_carries_the_flag()
    {
        OverridableEip1559Spec spec = new(Enabled);
        OverridableEip1559Spec overridden = new(Enabled) { IsEip8198Enabled = false };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Calculate(spec, 1_000_000_000, 0, 10, 1), Is.EqualTo((UInt256)895_833_334));
            Assert.That(Calculate(overridden, 1_000_000_000, 0, 10, 1), Is.EqualTo((UInt256)875_000_000));
        }
    }

    // Header validation rejects these, so the calculator keeps the EIP-1559 rule rather than dividing by zero or wrapping.
    [TestCase(10UL, 0L, TestName = "Same slot as the parent")]
    [TestCase(10UL, -1L, TestName = "Slot before the parent's")]
    [TestCase(0UL, 1L, TestName = "Same timestamp as the parent")]
    public void Invalid_spacing_keeps_the_eip1559_rule(ulong secondsElapsed, long slotsElapsed)
    {
        BlockHeader parent = Parent(1_000_000_000, BlockGasLimit);
        Assert.That(BaseFeeCalculator.Calculate(parent, ParentTimestamp + secondsElapsed, (ulong)((long)ParentSlot + slotsElapsed), Enabled),
            Is.EqualTo((UInt256)1_125_000_000));
    }

    [Test]
    public void Missing_slot_numbers_keep_the_eip1559_rule()
    {
        BlockHeader slotless = Parent(1_000_000_000, BlockGasLimit);
        slotless.SlotNumber = null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BaseFeeCalculator.Calculate(slotless, ParentTimestamp + 10, ParentSlot + 1, Enabled), Is.EqualTo((UInt256)1_125_000_000), "parent");
            Assert.That(BaseFeeCalculator.Calculate(Parent(1_000_000_000, BlockGasLimit), ParentTimestamp + 10, null, Enabled), Is.EqualTo((UInt256)1_125_000_000), "child");
        }
    }

    [Test]
    public void Spacing_at_or_above_12s_per_slot_keeps_the_eip1559_cap(
        [Values(0UL, BlockGasLimit, GasTarget / 2)] ulong gasUsed,
        [Values(12UL, 13UL, 96UL, 200UL)] ulong slotSeconds,
        [Values(1UL, 2UL)] ulong slotsElapsed) =>
        Assert.That(Calculate(Enabled, 1_000_000_000, gasUsed, slotSeconds * slotsElapsed, slotsElapsed),
            Is.EqualTo(Reference(1_000_000_000, gasUsed, 1, 8)));

    [Test]
    public void Multiplication_does_not_overflow_256_bits()
    {
        // An empty parent makes target_fee_gas_delta the parent's base fee, and 2^200 × 10 × 2^60 overflows 256 bits.
        UInt256 baseFee = UInt256.One << 200;
        const ulong slotsElapsed = 1UL << 60;
        BlockHeader parent = Parent(0, 0);
        parent.BaseFeePerGas = baseFee;
        parent.Timestamp = 0;
        parent.SlotNumber = 0;

        BigInteger expected = (BigInteger)baseFee - (BigInteger)baseFee * (10 * (BigInteger)slotsElapsed) / (96 * (BigInteger)slotsElapsed);
        Assert.That(BaseFeeCalculator.Calculate(parent, 10 * slotsElapsed, slotsElapsed, Enabled), Is.EqualTo((UInt256)expected));
    }

    [Test]
    public void Slot_jump_does_not_overflow_64_bits()
    {
        // 12 × 2^63 slots wraps a ulong; 10 s per slot over them is still 5/48.
        const ulong slotsElapsed = 1UL << 63;
        BlockHeader parent = Parent(1_000_000_000, BlockGasLimit);
        parent.Timestamp = 0;
        parent.SlotNumber = 0;
        Assert.That(BaseFeeCalculator.Calculate(parent, ulong.MaxValue, slotsElapsed, Enabled),
            Is.EqualTo((UInt256)(1_000_000_000 + (BigInteger)1_000_000_000 * ulong.MaxValue / (96 * (BigInteger)slotsElapsed))));
    }

    [Test]
    public void Next_slot_prediction_uses_the_slot_after_the_parent()
    {
        BlockHeader parent = Parent(1_000_000_000, BlockGasLimit);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BaseFeeCalculator.CalculateForNextSlot(parent, ParentTimestamp + 10, Enabled), Is.EqualTo((UInt256)1_104_166_666));
            Assert.That(BaseFeeCalculator.CalculateForNextSlot(parent, ParentTimestamp + 12, Enabled), Is.EqualTo((UInt256)1_125_000_000));
            Assert.That(BaseFeeCalculator.CalculateForReferenceSlot(parent, Enabled), Is.EqualTo((UInt256)1_125_000_000));
            Assert.That(BaseFeeCalculator.CalculateForNextSlot(parent, ParentTimestamp + 10, Amsterdam.Instance), Is.EqualTo((UInt256)1_125_000_000));
        }
    }

    private static IEnumerable<(ulong BaseFee, ulong GasUsed)> Matrix()
    {
        foreach (ulong baseFee in BaseFees)
            foreach (ulong gasUsed in GasUsedCases)
                yield return (baseFee, gasUsed);
    }

    private static BlockHeader Parent(ulong parentBaseFee, ulong parentGasUsed) => Build.A.BlockHeader
        .WithNumber(2001)
        .WithGasLimit(BlockGasLimit)
        .WithGasUsed(parentGasUsed)
        .WithBaseFee(parentBaseFee)
        .WithTimestamp(ParentTimestamp)
        .WithSlotNumber(ParentSlot)
        .TestObject;

    private static UInt256 Calculate(IEip1559Spec spec, ulong parentBaseFee, ulong parentGasUsed, ulong secondsElapsed, ulong slotsElapsed) =>
        BaseFeeCalculator.Calculate(Parent(parentBaseFee, parentGasUsed), ParentTimestamp + secondsElapsed, ParentSlot + slotsElapsed, spec);

    /// <summary>execution-specs <c>calculate_base_fee_per_gas</c> with unbounded integers and a fixed fraction.</summary>
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
