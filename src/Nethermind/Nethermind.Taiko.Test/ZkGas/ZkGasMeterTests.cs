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
    // Constructs a meter with the alethia schedule tables wired in — the configuration most tests
    // want. Mirrors what the spec provider hands the meter at runtime.
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0x20], Is.EqualTo((ushort)31)); // keccak256
            Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0xf1], Is.EqualTo((ushort)20)); // call
            Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0xfe], Is.EqualTo((ushort)0));  // invalid
            Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0x1e], Is.EqualTo((ushort)14)); // clz (EIP-7939)
            Assert.That(ZkGasTestSchedules.OpcodeMultipliers.Span[0xac], Is.EqualTo(ushort.MaxValue)); // unlisted -> failsafe
        }
    }

    [Test]
    public void PrecompileMultipliers_Spot_Check_Spec_Values()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x05), Is.EqualTo((ushort)154)); // modexp — EIP-7883 6× gas-cost slope
            Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x01), Is.EqualTo((ushort)47));  // ecrecover
            Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x04), Is.EqualTo((ushort)6));   // identity
            Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x0d), Is.EqualTo((ushort)230)); // bls12_g2add (EIP-2537)
            Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x10), Is.EqualTo((ushort)246)); // bls12_map_fp_to_g1
            Assert.That(ZkGasTestSchedules.PrecompileMultiplier(0x11), Is.EqualTo((ushort)208)); // bls12_map_fp2_to_g2 (last canonical EIP-2537 slot)
            Assert.That(ZkGasTestSchedules.PrecompileMultipliers[Address.FromNumber(0x100)], Is.EqualTo((ushort)163), // p256verify (RIP-7212)
                "p256verify lives at 0x100 — outside the canonical 0x..XX range");
            Assert.That(ZkGasTestSchedules.PrecompileMultipliers.ContainsKey(Address.FromNumber(0x12)), Is.False,
                "0x12 sat on the pre-final EIP-2537 draft and is not in the canonical Osaka set — meter charges fail-safe");
            Assert.That(ZkGasTestSchedules.PrecompileMultipliers.ContainsKey(Address.FromNumber(0x14)), Is.False,
                "0x14 is not listed — meter charges fail-safe");
        }
    }

    [Test]
    public void Meter_charges_using_supplied_override_table()
    {
        // A meter built with an explicit opcode table charges against that table, ignoring any
        // default — the contract that lets a chainspec pin its own schedule.
        ushort[] customOpcodes = new ushort[256];
        customOpcodes.AsSpan().Fill(ZkGasSchedule.FailsafeMultiplier);
        customOpcodes[0x20] = 85; // arbitrary non-default keccak256 value (alethia schedule lists 31)

        ZkGasMeter customMeter = new(opcodeMultipliers: customOpcodes);
        ZkGasMeter alethiaMeter = MeterWithAlethiaTables();

        customMeter.ChargeOpcode(0x20, 1);
        alethiaMeter.ChargeOpcode(0x20, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(customMeter.TxZkGasUsed, Is.EqualTo(85UL), "supplied table is used");
            Assert.That(alethiaMeter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasTestSchedules.OpcodeMultipliers.Span[0x20]),
                "alethia meter uses the alethia table");
            Assert.That(customMeter.TxZkGasUsed, Is.Not.EqualTo(alethiaMeter.TxZkGasUsed));
        }
    }

    [Test]
    public void Meter_with_no_tables_falls_back_to_failsafe()
    {
        // No tables supplied → every charge multiplies by ushort.MaxValue. This is the pre-Unzen
        // path: the tracer still runs, but the block processor discards its totals.
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ecrecoverCharge, Is.EqualTo(47UL));
            Assert.That(l1SloadCharge, Is.EqualTo(200UL));
        }
    }

    [Test]
    public void Clz_charges_14_on_default_schedule()
    {
        // EIP-7939 added CLZ at opcode 0x1e in Osaka, which Unzen extends. The default Alethia
        // schedule charges it at multiplier 14.
        const byte clz = 0x1e;
        const ulong rawGas = 5; // CLZ base gas cost under EIP-7939

        ZkGasMeter meter = MeterWithAlethiaTables();
        meter.ChargeOpcode(clz, rawGas);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(rawGas * 14UL));
    }

    [Test]
    public void Clz_charges_failsafe_when_entry_missing()
    {
        // A schedule that omits 0x1e charges fail-safe — the meter never silently zeroes missing
        // entries.
        ushort[] opcodes = new ushort[256];
        opcodes.AsSpan().Fill(ZkGasSchedule.FailsafeMultiplier);
        opcodes[0x20] = 31; // arbitrary entry so the meter isn't entirely failsafe

        ZkGasMeter meter = new(opcodeMultipliers: opcodes);
        meter.ChargeOpcode(0x1e, 1);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasSchedule.FailsafeMultiplier));
    }

    [Test]
    public void P256Verify_charges_163_on_default_schedule()
    {
        // RIP-7212 / p256verify lives at 0x100 and is active wherever Unzen extends Osaka.
        // The default Alethia schedule lists it at multiplier 163.
        Address p256Verify = Address.FromNumber(0x100);
        const ulong gasUsed = 6_900; // SecP256r1Precompile.BaseGasCost under EIP-7951

        ZkGasMeter meter = MeterWithAlethiaTables();
        meter.ChargePrecompile(p256Verify, gasUsed);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo(gasUsed * 163UL));
    }

    [Test]
    public void P256Verify_charges_failsafe_when_entry_missing()
    {
        // A schedule that omits 0x100 charges fail-safe — the meter never silently zeroes missing
        // entries.
        Address p256Verify = Address.FromNumber(0x100);

        FrozenDictionary<AddressAsKey, ushort> precompiles =
            new System.Collections.Generic.Dictionary<AddressAsKey, ushort>
            {
                [Address.FromNumber(0x01)] = 81, // arbitrary entry; test probes 0x100, not 0x01
            }.ToFrozenDictionary();
        ZkGasMeter meter = new(precompileMultipliers: precompiles);
        meter.ChargePrecompile(p256Verify, 1);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasSchedule.FailsafeMultiplier));
    }

    [Test]
    public void SpawnEstimates_Match_Spec()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ZkGasSchedule.SpawnEstimateCall, Is.EqualTo(12_500UL));
            Assert.That(ZkGasSchedule.SpawnEstimateCallCode, Is.EqualTo(12_500UL));
            Assert.That(ZkGasSchedule.SpawnEstimateDelegateCall, Is.EqualTo(3_500UL));
            Assert.That(ZkGasSchedule.SpawnEstimateStaticCall, Is.EqualTo(3_500UL));
            Assert.That(ZkGasSchedule.SpawnEstimateCreate, Is.EqualTo(37_000UL));
            Assert.That(ZkGasSchedule.SpawnEstimateCreate2, Is.EqualTo(44_500UL));
        }
    }

    // ── commit / reset ────────────────────────────────────────────────────────

    [Test]
    public void CommitTransaction_Promotes_TxGas_Into_BlockGas()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        byte addOpcode = 0x01; // ADD, multiplier = 19
        meter.ChargeOpcode(addOpcode, 3);
        meter.CommitTransaction();

        ulong expected = 3UL * ZkGasTestSchedules.OpcodeMultipliers.Span[addOpcode];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(meter.BlockZkGasUsed, Is.EqualTo(expected));
            Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
            Assert.That(meter.BlockZkGasUsed, Is.EqualTo(blockAfterFirst));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(meter.IsLimitExceeded, Is.True);
        }
    }

    [Test]
    public void ChargePrecompile_Rejects_When_Charge_Exceeds_Block_Budget()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        // ecrecover multiplier = 47; feed enough raw gas to exceed the block limit
        bool result = meter.ChargePrecompile(Address.FromNumber(0x01), ZkGasSchedule.BlockZkGasLimit);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(meter.IsLimitExceeded, Is.True);
        }
    }

    // ── overflow protection ───────────────────────────────────────────────────

    [Test]
    public void ChargeOpcode_Treats_Multiplication_Overflow_As_LimitExceeded()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        byte opcode = 0x01; // ADD, multiplier = 19
        ulong overflowRawGas = ulong.MaxValue / ZkGasTestSchedules.OpcodeMultipliers.Span[opcode] + 1;

        bool result = meter.ChargeOpcode(opcode, overflowRawGas);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(meter.IsLimitExceeded, Is.True);
        }
    }

    [Test]
    public void ChargePrecompile_Treats_Multiplication_Overflow_As_LimitExceeded()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        Address ecrecover = Address.FromNumber(0x01);
        ulong overflowRawGas = ulong.MaxValue / ZkGasTestSchedules.PrecompileMultiplier(0x01) + 1;

        bool result = meter.ChargePrecompile(ecrecover, overflowRawGas);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(meter.IsLimitExceeded, Is.True);
        }
    }

    [Test]
    public void ChargeOpcode_Rejects_Charge_Whose_Magnitude_Exceeds_Block_Limit()
    {
        ZkGasMeter meter = MeterWithAlethiaTables();
        ulong halfMax = ulong.MaxValue / 2 + 1;
        bool charged = meter.ChargeOpcode(0xf0, halfMax);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(charged, Is.False);
            Assert.That(meter.IsLimitExceeded, Is.True);
        }
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
        ZkGasMeter meter = MeterWithAlethiaTables(txIntrinsicZkGas: 0); // schedule with zero intrinsic

        bool result = meter.ChargeTxIntrinsic();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(meter.TxZkGasUsed, Is.EqualTo(0UL));
            Assert.That(meter.IsLimitExceeded, Is.False);
        }
    }

    [Test]
    public void ChargeTxIntrinsic_SetsLimitExceeded_WhenRemainingBudgetTooSmall()
    {
        const ulong intrinsic = ZkGasSchedule.TxIntrinsicZkGas;
        ZkGasMeter meter = MeterWithAlethiaTables(blockZkGasLimit: intrinsic - 1, txIntrinsicZkGas: intrinsic);

        bool result = meter.ChargeTxIntrinsic();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(meter.IsLimitExceeded, Is.True);
        }
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
