// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Taiko.ZkGas;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.ZkGas;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ZkGasMeterTests
{
    // Constructs a meter populated with the alethia recalibrated tables — the canonical "Unzen
    // active" configuration most tests want. Mirrors what the spec provider hands the meter at
    // runtime.
    private static ZkGasMeter MeterWithAlethiaTables(
        ulong blockZkGasLimit = ZkGasSchedule.BlockZkGasLimit,
        ulong txIntrinsicZkGas = ZkGasSchedule.TxIntrinsicZkGas) =>
        new(blockZkGasLimit, txIntrinsicZkGas,
            ZkGasTestSchedules.OpcodeMultipliers,
            ZkGasTestSchedules.PrecompileMultipliers);

    // ── schedule constants ────────────────────────────────────────────────────

    [Test]
    public void BlockLimit_Matches_Spec() =>
        Assert.That(ZkGasSchedule.BlockZkGasLimit, Is.EqualTo(100_000_000UL));

    [Test]
    public void OpcodeMultipliers_Spot_Check_Spec_Values()
    {
        Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0x20], Is.EqualTo((ushort)31)); // keccak256
        Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0xf1], Is.EqualTo((ushort)20)); // call
        Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0xfe], Is.EqualTo((ushort)0));  // invalid
        Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0xac], Is.EqualTo(ushort.MaxValue)); // unlisted -> failsafe
    }

    [Test]
    public void PrecompileMultipliers_Spot_Check_Spec_Values()
    {
        Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x05), Is.EqualTo((ushort)923)); // modexp
        Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x01), Is.EqualTo((ushort)47));  // ecrecover
        Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x04), Is.EqualTo((ushort)6));   // identity
        Assert.That(ZkGasTestSchedules.PrecompileMultipliers[Address.FromNumber(0x100)], Is.EqualTo((ushort)163), // p256verify (RIP-7212)
            "p256verify lives at 0x100 — outside the canonical 0x..XX range");
        Assert.That(ZkGasTestSchedules.PrecompileMultipliers.ContainsKey(Address.FromNumber(0x14)), Is.False,
            "0x14 is not listed — meter charges fail-safe");
    }

    [Test]
    public void Meter_charges_using_supplied_override_table()
    {
        // A meter built with an explicit opcode table charges against that table, not the
        // recalibrated alethia default. This is what lets a network such as Masaya pin its own
        // frozen schedule purely from chainspec, with no chain-id branching in code.
        ushort[] frozenOpcodes = new ushort[256];
        frozenOpcodes.AsSpan().Fill(ZkGasSchedule.FailsafeMultiplier);
        frozenOpcodes[0x20] = 85; // pre-recalibration keccak256 (alethia default is 31)

        ZkGasMeter overrideMeter = new(opcodeMultipliers: frozenOpcodes);
        ZkGasMeter defaultMeter = MeterWithAlethiaTables();

        overrideMeter.ChargeOpcode(0x20, 1);
        defaultMeter.ChargeOpcode(0x20, 1);

        Assert.That(overrideMeter.TxZkGasUsed, Is.EqualTo(85UL), "override table is used");
        Assert.That(defaultMeter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasTestSchedules.OpcodeMultipliers.Span[0x20]),
            "alethia meter uses the recalibrated table");
        Assert.That(overrideMeter.TxZkGasUsed, Is.Not.EqualTo(defaultMeter.TxZkGasUsed));
    }

    [Test]
    public void Meter_with_no_tables_falls_back_to_failsafe()
    {
        // No tables supplied → meter operates in fail-safe mode: every charge multiplies by
        // ushort.MaxValue. A 1-raw-gas charge fits under the block limit but uses the failsafe
        // multiplier, so the tx total reflects that. This is the pre-Unzen path where the tracer
        // runs but the block processor discards its totals.
        ZkGasMeter meter = new();

        bool ok = meter.ChargeOpcode(0x01, 1);
        Assert.That(ok, Is.True);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasSchedule.FailsafeMultiplier),
            "failsafe multiplier is applied to every unlisted entry");

        Address ecrecover = Address.FromNumber(0x01);
        meter.CancelTransaction();
        bool okPrecompile = meter.ChargePrecompile(ecrecover, 1);
        Assert.That(okPrecompile, Is.True);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasSchedule.FailsafeMultiplier));
    }

    [Test]
    public void Precompile_collision_resolved_by_full_address()
    {
        // Taiko's L1Sload sits at 0x…010001; the low byte 0x01 collides with ecrecover. Keying
        // the table by full address lets both coexist with distinct multipliers.
        Address ecrecover = Address.FromNumber(0x01);
        Address l1Sload = Address.FromNumber(0x10001);

        FrozenDictionary<AddressAsKey, ushort> dict =
            new System.Collections.Generic.Dictionary<AddressAsKey, ushort>
            {
                [ecrecover] = 47,
                [l1Sload] = 200,
            }.ToFrozenDictionary();
        ZkGasMeter meter = new(precompileMultipliers: dict);

        meter.ChargePrecompile(ecrecover, 1);
        ulong ecrecoverCharge = meter.TxZkGasUsed;
        meter.CommitTransaction();

        meter.ChargePrecompile(l1Sload, 1);
        ulong l1SloadCharge = meter.TxZkGasUsed;

        Assert.That(ecrecoverCharge, Is.EqualTo(47UL));
        Assert.That(l1SloadCharge, Is.EqualTo(200UL));
    }

    [Test]
    public void P256Verify_charged_on_default_schedule_failsafe_when_frozen()
    {
        // RIP-7212 / p256verify lives at 0x100 and is active wherever Unzen extends Osaka.
        // The default Alethia schedule lists it at 163; a frozen schedule (e.g. Masaya) that
        // pre-dates this entry must keep charging the fail-safe so its finalized blocks stay
        // consensus-valid against their committed ZK-gas totals.
        Address p256Verify = Address.FromNumber(0x100);
        const ulong gasUsed = 6_900; // SecP256r1Precompile.BaseGasCost under EIP-7951

        ZkGasMeter defaultMeter = MeterWithAlethiaTables();
        defaultMeter.ChargePrecompile(p256Verify, gasUsed);
        Assert.That(defaultMeter.TxZkGasUsed, Is.EqualTo(gasUsed * 163UL),
            "default Alethia schedule charges 163 × gas_used for p256verify");

        FrozenDictionary<AddressAsKey, ushort> frozenPrecompiles =
            new System.Collections.Generic.Dictionary<AddressAsKey, ushort>
            {
                [Address.FromNumber(0x01)] = 81, // pre-recalibration ecrecover, sanity entry
            }.ToFrozenDictionary();
        ZkGasMeter frozenMeter = new(precompileMultipliers: frozenPrecompiles);
        frozenMeter.ChargePrecompile(p256Verify, 1);
        Assert.That(frozenMeter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasSchedule.FailsafeMultiplier),
            "schedule lacking a 0x100 row charges fail-safe — keeps existing Masaya blocks valid");
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
        ZkGasMeter meter = MeterWithAlethiaTables();
        byte addOpcode = 0x01; // multiplier = 19 (recalibrated)
        meter.ChargeOpcode(addOpcode, 3);
        meter.CommitTransaction();

        ulong expected = 3UL * ZkGasTestSchedules.OpcodeMultipliers.Span[addOpcode];
        Assert.That(meter.BlockZkGasUsed, Is.EqualTo(expected));
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
    }

    [Test]
    public void CommitTransaction_Clears_IsLimitExceeded_After_Transient_Spike()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        meter.ChargeOpcode(0x20, ZkGasSchedule.BlockZkGasLimit); // triggers limit
        Assert.That(meter.IsLimitExceeded, Is.True);

        meter.CancelTransaction();
        Assert.That(meter.IsLimitExceeded, Is.False);

        meter.ChargeOpcode(0x01, 1);
        bool committed = meter.CommitTransaction();
        Assert.That(committed, Is.True);
        Assert.That(meter.IsLimitExceeded, Is.False);
    }

    [Test]
    public void ResetTransaction_Clears_TxGas_Without_Affecting_BlockGas()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
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
        ZkGasMeter meter = MeterWithAlethiaTables();
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
        ZkGasMeter meter = MeterWithAlethiaTables();
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
        ZkGasMeter meter = MeterWithAlethiaTables();
        meter.ChargeOpcode(0xf0, ZkGasSchedule.BlockZkGasLimit - 1);
        meter.CommitTransaction();

        bool result = meter.ChargeOpcode(0xf0, 2);
        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void ChargePrecompile_Rejects_When_Charge_Exceeds_Block_Budget()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        // ecrecover multiplier = 47 (recalibrated); feed enough raw gas to exceed the block limit
        bool result = meter.ChargePrecompile(Address.FromNumber(0x01), ZkGasSchedule.BlockZkGasLimit);
        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    // ── overflow protection ───────────────────────────────────────────────────

    [Test]
    public void ChargeOpcode_Treats_Multiplication_Overflow_As_LimitExceeded()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        byte opcode = 0x01; // add, multiplier = 19 (recalibrated)
        ulong overflowRawGas = ulong.MaxValue / ZkGasTestSchedules.OpcodeMultipliers.Span[opcode] + 1;

        bool result = meter.ChargeOpcode(opcode, overflowRawGas);
        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void ChargePrecompile_Treats_Multiplication_Overflow_As_LimitExceeded()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        Address ecrecover = Address.FromNumber(0x01);
        ulong overflowRawGas = ulong.MaxValue / ZkGasTestSchedules.PrecompileMultiplier(0x01) + 1;

        bool result = meter.ChargePrecompile(ecrecover, overflowRawGas);
        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void ChargeOpcode_Rejects_Charge_Whose_Magnitude_Exceeds_Block_Limit()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
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
        ZkGasMeter meter = MeterWithAlethiaTables(blockZkGasLimit: ulong.MaxValue);
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
        ZkGasMeter meter = MeterWithAlethiaTables(txIntrinsicZkGas: ZkGasSchedule.TxIntrinsicZkGas);

        bool result = meter.ChargeTxIntrinsic();

        Assert.That(result, Is.True);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(ZkGasSchedule.TxIntrinsicZkGas));

        meter.CommitTransaction();
        Assert.That(meter.BlockZkGasUsed, Is.EqualTo(ZkGasSchedule.TxIntrinsicZkGas));
    }

    [Test]
    public void ChargeTxIntrinsic_IsNoop_WhenIntrinsicIsZero()
    {
        ZkGasMeter meter = MeterWithAlethiaTables(txIntrinsicZkGas: 0); // Masaya schedule

        bool result = meter.ChargeTxIntrinsic();

        Assert.That(result, Is.True);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
        Assert.That(meter.IsLimitExceeded, Is.False);
    }

    [Test]
    public void ChargeTxIntrinsic_SetsLimitExceeded_WhenRemainingBudgetTooSmall()
    {
        const ulong intrinsic = ZkGasSchedule.TxIntrinsicZkGas;
        ZkGasMeter meter = MeterWithAlethiaTables(blockZkGasLimit: intrinsic - 1, txIntrinsicZkGas: intrinsic);

        bool result = meter.ChargeTxIntrinsic();

        Assert.That(result, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
    }

    [Test]
    public void CommitTransaction_PreservesLimitExceeded_WhenChargeFailedMidTx()
    {
        ZkGasMeter meter = MeterWithAlethiaTables(blockZkGasLimit: 1000, txIntrinsicZkGas: 0);

        meter.ChargeOpcode(0xf0, 800);
        meter.ChargeOpcode(0xf0, 300); // fails: 800+300 > 1000
        Assert.That(meter.IsLimitExceeded, Is.True);

        bool committed = meter.CommitTransaction();
        Assert.That(committed, Is.False);
        Assert.That(meter.IsLimitExceeded, Is.True);
        Assert.That(meter.BlockZkGasUsed, Is.EqualTo(0UL));
    }
}
