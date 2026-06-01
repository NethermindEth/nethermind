// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Taiko.ZkGas;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.ZkGas;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ZkGasMeterTests
{
    // ── schedule constants ────────────────────────────────────────────────────

    [Test]
    public void BlockLimit_Matches_Spec() =>
        Assert.That(ZkGasSchedule.BlockZkGasLimit, Is.EqualTo(100_000_000UL));

    [Test]
    public void OpcodeMultipliers_Spot_Check_Spec_Values()
    {
        // Recalibrated default schedule (taiko-mono#21720 / alethia-reth#187).
        Assert.That(ZkGasSchedule.OpcodeMultipliers.Span[0x20], Is.EqualTo((ushort)31)); // keccak256
        Assert.That(ZkGasSchedule.OpcodeMultipliers.Span[0xf1], Is.EqualTo((ushort)20)); // call
        Assert.That(ZkGasSchedule.OpcodeMultipliers.Span[0xfe], Is.EqualTo((ushort)0));  // invalid (terminal)
        Assert.That(ZkGasSchedule.OpcodeMultipliers.Span[0xac], Is.EqualTo(ushort.MaxValue)); // unlisted -> failsafe
    }

    [Test]
    public void PrecompileMultipliers_Spot_Check_Spec_Values()
    {
        // Recalibrated default schedule (taiko-mono#21720 / alethia-reth#187).
        Assert.That(ZkGasSchedule.PrecompileMultipliers.Span[0x05], Is.EqualTo((ushort)923)); // modexp
        Assert.That(ZkGasSchedule.PrecompileMultipliers.Span[0x01], Is.EqualTo((ushort)47));  // ecrecover
        Assert.That(ZkGasSchedule.PrecompileMultipliers.Span[0x04], Is.EqualTo((ushort)6));   // identity
        Assert.That(ZkGasSchedule.PrecompileMultipliers.Span[0x14], Is.EqualTo(ushort.MaxValue)); // unlisted -> failsafe
    }

    [Test]
    public void Meter_charges_using_supplied_override_table()
    {
        // A meter built with an explicit table (as the chainspec-driven path supplies) charges
        // against that table, not the recalibrated default. This is what lets a network such as
        // Masaya pin its own frozen schedule purely from chainspec, with no chain-id branching in code.
        ushort[] frozenOpcodes = new ushort[256];
        frozenOpcodes.AsSpan().Fill(ZkGasSchedule.FailsafeMultiplier);
        frozenOpcodes[0x20] = 85; // pre-recalibration keccak256 (default is 31)

        ZkGasMeter overrideMeter = new(opcodeMultipliers: frozenOpcodes);
        ZkGasMeter defaultMeter = new();

        overrideMeter.ChargeOpcode(0x20, 1);
        defaultMeter.ChargeOpcode(0x20, 1);

        Assert.That(overrideMeter.TxZkGasUsed, Is.EqualTo(85UL), "override table is used");
        Assert.That(defaultMeter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasSchedule.OpcodeMultipliers.Span[0x20]),
            "default meter still uses the recalibrated table");
        Assert.That(overrideMeter.TxZkGasUsed, Is.Not.EqualTo(defaultMeter.TxZkGasUsed));
    }

    [Test]
    public void Meter_with_empty_override_falls_back_to_recalibrated_default()
    {
        // Empty tables (the no-override sentinel passed through from a spec with no chainspec
        // override) must resolve to the recalibrated default rather than charging nothing.
        ZkGasMeter meter = new(opcodeMultipliers: default, precompileMultipliers: default);

        meter.ChargeOpcode(0x20, 1);
        meter.ChargePrecompile(0x05, 1);

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(
            (ulong)ZkGasSchedule.OpcodeMultipliers.Span[0x20] + ZkGasSchedule.PrecompileMultipliers.Span[0x05]));
    }

    [Test]
    public void SpawnEstimates_Match_Spec()
    {
        Assert.That(ZkGasSchedule.SpawnEstimateCall, Is.EqualTo(12_500UL));
        Assert.That(ZkGasSchedule.SpawnEstimateCallCode, Is.EqualTo(12_500UL));
        Assert.That(ZkGasSchedule.SpawnEstimateDelegateCall, Is.EqualTo(3_500UL));
        Assert.That(ZkGasSchedule.SpawnEstimateStaticCall, Is.EqualTo(3_500UL));
        Assert.That(ZkGasSchedule.SpawnEstimateCreate, Is.EqualTo(37_000UL));
        Assert.That(ZkGasSchedule.SpawnEstimateCreate2, Is.EqualTo(44_500UL));
    }

    // ── commit / reset ────────────────────────────────────────────────────────

    [Test]
    public void CommitTransaction_Promotes_TxGas_Into_BlockGas()
    {
        ZkGasMeter meter = new();
        byte addOpcode = 0x01; // multiplier = 19 (recalibrated)
        meter.ChargeOpcode(addOpcode, 3);
        meter.CommitTransaction();

        ulong expected = 3UL * ZkGasSchedule.OpcodeMultipliers.Span[addOpcode];
        Assert.That(meter.BlockZkGasUsed, Is.EqualTo(expected));
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
    }

    [Test]
    public void CommitTransaction_Clears_IsLimitExceeded_After_Transient_Spike()
    {
        // Arrange: drive IsLimitExceeded = true mid-tx, then cancel and commit normally.
        ZkGasMeter meter = new();
        meter.ChargeOpcode(0x20, ZkGasSchedule.BlockZkGasLimit); // triggers limit
        Assert.That(meter.IsLimitExceeded, Is.True);

        meter.CancelTransaction();
        Assert.That(meter.IsLimitExceeded, Is.False);

        // Now commit a small legal tx
        meter.ChargeOpcode(0x01, 1);
        bool committed = meter.CommitTransaction();
        Assert.That(committed, Is.True);
        Assert.That(meter.IsLimitExceeded, Is.False);
    }

    [Test]
    public void ResetTransaction_Clears_TxGas_Without_Affecting_BlockGas()
    {
        ZkGasMeter meter = new();
        meter.ChargeOpcode(0x01, 2);
        meter.CommitTransaction();
        ulong blockAfterFirst = meter.BlockZkGasUsed;

        meter.ChargeOpcode(0x01, 5);
        meter.ResetTransaction();

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
        Assert.That(meter.BlockZkGasUsed, Is.EqualTo(blockAfterFirst));
    }

    [Test]
    public void CancelTransaction_Clears_TxGas_And_Resets_LimitExceeded()
    {
        ZkGasMeter meter = new();
        meter.ChargeOpcode(0x20, ZkGasSchedule.BlockZkGasLimit); // exceed limit
        Assert.That(meter.IsLimitExceeded, Is.True);

        meter.CancelTransaction();

        Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
        Assert.That(meter.IsLimitExceeded, Is.False);
    }

    // ── block-limit enforcement ───────────────────────────────────────────────

    [Test]
    public void ChargeOpcode_Allows_Exactly_Remaining_Block_Budget()
    {
        // opcode 0xf0 (create) has multiplier 1, so rawGas == zkGas
        ZkGasMeter meter = new();
        ulong fill = ZkGasSchedule.BlockZkGasLimit - 1;
        meter.ChargeOpcode(0xf0, fill);
        bool committed = meter.CommitTransaction();
        Assert.That(committed, Is.True);

        bool last = meter.ChargeOpcode(0xf0, 1);
        Assert.That(last, Is.True);
        meter.CommitTransaction();
        Assert.That(meter.BlockZkGasUsed, Is.EqualTo(ZkGasSchedule.BlockZkGasLimit));
    }

    [Test]
    public void ChargeOpcode_Rejects_One_Over_Block_Budget()
    {
        ZkGasMeter meter = new();
        meter.ChargeOpcode(0xf0, ZkGasSchedule.BlockZkGasLimit - 1);
        meter.CommitTransaction();

        bool result = meter.ChargeOpcode(0xf0, 2);
        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void ChargePrecompile_Rejects_When_Charge_Exceeds_Block_Budget()
    {
        ZkGasMeter meter = new();
        // ecrecover multiplier = 47 (recalibrated); feed enough raw gas to exceed the block limit
        bool result = meter.ChargePrecompile(0x01, ZkGasSchedule.BlockZkGasLimit);
        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    // ── overflow protection ───────────────────────────────────────────────────

    [Test]
    public void ChargeOpcode_Treats_Multiplication_Overflow_As_LimitExceeded()
    {
        ZkGasMeter meter = new();
        byte opcode = 0x01; // add, multiplier = 19 (recalibrated)
        ulong overflowRawGas = ulong.MaxValue / ZkGasSchedule.OpcodeMultipliers.Span[opcode] + 1;

        bool result = meter.ChargeOpcode(opcode, overflowRawGas);
        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void ChargePrecompile_Treats_Multiplication_Overflow_As_LimitExceeded()
    {
        ZkGasMeter meter = new();
        byte precompile = 0x01; // ecrecover, multiplier = 47 (recalibrated)
        ulong overflowRawGas = ulong.MaxValue / ZkGasSchedule.PrecompileMultipliers.Span[precompile] + 1;

        bool result = meter.ChargePrecompile(precompile, overflowRawGas);
        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void ChargeOpcode_Rejects_Charge_Whose_Magnitude_Exceeds_Block_Limit()
    {
        // Under the default 100M block limit, a single raw-gas charge with magnitude
        // ulong.MaxValue/2+1 (multiplied by the multiplier-1 CREATE opcode) projects far past
        // the block limit and is rejected by the projectedBlock > _blockZkGasLimit branch in
        // ChargeAmount — not the tx-accumulation overflow path, which has its own dedicated
        // test below.
        ZkGasMeter meter = new();
        ulong halfMax = ulong.MaxValue / 2 + 1;
        bool charged = meter.ChargeOpcode(0xf0, halfMax);
        Assert.That(charged, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void ChargeOpcode_Treats_Tx_Accumulation_Overflow_As_Failure()
    {
        // With block limit pinned at ulong.MaxValue, the block-bound branch in ChargeAmount
        // cannot fire; the only way ChargeAmount returns false is via the
        // nextTx < _txZkGasUsed wraparound check. Two halfMax charges to a multiplier-1
        // opcode put _txZkGasUsed at halfMax then wrap on the second add.
        ZkGasMeter meter = new(blockZkGasLimit: ulong.MaxValue);
        ulong halfMax = ulong.MaxValue / 2 + 1;

        bool first = meter.ChargeOpcode(0xf0, halfMax);
        Assert.That(first, Is.True);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(halfMax));

        bool second = meter.ChargeOpcode(0xf0, halfMax);
        Assert.That(second, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    // ── TX intrinsic ZK gas ───────────────────────────────────────────────────

    [Test]
    public void TxIntrinsicZkGas_Schedule_Constant_Is_243000() =>
        Assert.That(ZkGasSchedule.TxIntrinsicZkGas, Is.EqualTo(243_000UL));

    [Test]
    public void MasayaTxIntrinsicZkGas_Schedule_Constant_Is_Zero() =>
        Assert.That(ZkGasSchedule.MasayaTxIntrinsicZkGas, Is.EqualTo(0UL));

    [Test]
    public void ChargeTxIntrinsic_Adds_Intrinsic_To_InFlight_Tx()
    {
        ZkGasMeter meter = new(txIntrinsicZkGas: ZkGasSchedule.TxIntrinsicZkGas);

        bool result = meter.ChargeTxIntrinsic();

        Assert.That(result, Is.True);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.TxIntrinsicZkGas));

        // Confirm the charge is promoted to block total on commit.
        meter.CommitTransaction();
        Assert.That(meter.BlockZkGasUsed, Is.EqualTo(ZkGasSchedule.TxIntrinsicZkGas));
    }

    [Test]
    public void ChargeTxIntrinsic_IsNoop_WhenIntrinsicIsZero()
    {
        ZkGasMeter meter = new(txIntrinsicZkGas: 0); // Masaya schedule

        bool result = meter.ChargeTxIntrinsic();

        Assert.That(result, Is.True);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
        Assert.That(meter.IsLimitExceeded, Is.False);
    }

    [Test]
    public void ChargeTxIntrinsic_SetsLimitExceeded_WhenRemainingBudgetTooSmall()
    {
        const ulong intrinsic = ZkGasSchedule.TxIntrinsicZkGas;
        // Set block limit one unit below the intrinsic so the charge must fail.
        ZkGasMeter meter = new(blockZkGasLimit: intrinsic - 1, txIntrinsicZkGas: intrinsic);

        bool result = meter.ChargeTxIntrinsic();

        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void CommitTransaction_PreservesLimitExceeded_WhenChargeFailedMidTx()
    {
        ZkGasMeter meter = new(blockZkGasLimit: 1000, txIntrinsicZkGas: 0);

        meter.ChargeOpcode(0xf0, 800); // succeeds: 800 in-flight
        meter.ChargeOpcode(0xf0, 300); // fails: 800+300 > 1000, _txZkGasUsed stays at 800
        Assert.That(meter.IsLimitExceeded, Is.True);

        // Must not commit the undercounted partial total and clear the flag.
        bool committed = meter.CommitTransaction();
        Assert.That(committed, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
        Assert.That(meter.BlockZkGasUsed, Is.EqualTo(0UL));
    }
}
