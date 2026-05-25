// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Taiko.ZkGas;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.ZkGas;

/// <summary>
/// Pins the per-network Unzen ZK gas block limit and the chain ids it keys off.
/// Mirrors <c>test_taiko_genesis_chain_ids_are_pinned</c> /
/// <c>schedule_for_returns_masaya_schedule</c> in alethia-reth's PR
/// <see href="https://github.com/taikoxyz/alethia-reth/pull/170"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ZkGasScheduleTests
{
    [Test]
    public void Chain_ids_are_pinned()
    {
        Assert.That(ZkGasSchedule.TaikoMainnetChainId, Is.EqualTo(167_000UL));
        Assert.That(ZkGasSchedule.TaikoDevnetChainId, Is.EqualTo(167_001UL));
        Assert.That(ZkGasSchedule.TaikoMasayaChainId, Is.EqualTo(167_011UL));
        Assert.That(ZkGasSchedule.TaikoHoodiChainId, Is.EqualTo(167_013UL));
    }

    [Test]
    public void Block_zk_gas_limits_are_pinned()
    {
        Assert.That(ZkGasSchedule.BlockZkGasLimit, Is.EqualTo(100_000_000UL));
        Assert.That(ZkGasSchedule.MasayaBlockZkGasLimit, Is.EqualTo(1_000_000_000UL));
    }

    [Test]
    public void Meter_default_ctor_uses_canonical_block_limit()
    {
        ZkGasMeter meter = new();
        Assert.That(meter.BlockZkGasLimit, Is.EqualTo(ZkGasSchedule.BlockZkGasLimit));
    }

    [Test]
    public void Meter_honors_explicit_masaya_limit()
    {
        ZkGasMeter meter = new(ZkGasSchedule.MasayaBlockZkGasLimit);
        Assert.That(meter.BlockZkGasLimit, Is.EqualTo(ZkGasSchedule.MasayaBlockZkGasLimit));
    }

    // ── per-network multiplier dispatch (taiko-mono#21720 / alethia-reth#187) ─

    [TestCase(ZkGasSchedule.TaikoMainnetChainId, TestName = "Mainnet")]
    [TestCase(ZkGasSchedule.TaikoDevnetChainId, TestName = "Devnet")]
    [TestCase(ZkGasSchedule.TaikoHoodiChainId, TestName = "Hoodi")]
    [TestCase(0UL, TestName = "Unknown chain id falls through to default")]
    public void OpcodeMultipliersFor_returns_recalibrated_default_for_non_masaya_chains(ulong chainId)
    {
        ushort[] resolved = ZkGasSchedule.OpcodeMultipliersFor(chainId);
        Assert.That(resolved[0x20], Is.EqualTo((ushort)31), "keccak256 recalibrated");
        Assert.That(resolved[0xf1], Is.EqualTo((ushort)20), "call recalibrated");
        Assert.That(resolved[0x01], Is.EqualTo((ushort)19), "add recalibrated");
    }

    [TestCase(ZkGasSchedule.TaikoMainnetChainId, TestName = "Mainnet")]
    [TestCase(ZkGasSchedule.TaikoDevnetChainId, TestName = "Devnet")]
    [TestCase(ZkGasSchedule.TaikoHoodiChainId, TestName = "Hoodi")]
    [TestCase(0UL, TestName = "Unknown chain id falls through to default")]
    public void PrecompileMultipliersFor_returns_recalibrated_default_for_non_masaya_chains(ulong chainId)
    {
        ushort[] resolved = ZkGasSchedule.PrecompileMultipliersFor(chainId);
        Assert.That(resolved[0x05], Is.EqualTo((ushort)923), "modexp recalibrated");
        Assert.That(resolved[0x01], Is.EqualTo((ushort)47), "ecrecover recalibrated");
        Assert.That(resolved[0x04], Is.EqualTo((ushort)6), "identity recalibrated");
    }

    [Test]
    public void OpcodeMultipliersFor_Masaya_returns_frozen_table()
    {
        ushort[] resolved = ZkGasSchedule.OpcodeMultipliersFor(ZkGasSchedule.TaikoMasayaChainId);
        Assert.That(resolved[0x20], Is.EqualTo((ushort)85), "keccak256 stays frozen on Masaya");
        Assert.That(resolved[0xf1], Is.EqualTo((ushort)25), "call stays frozen on Masaya");
        Assert.That(resolved[0x01], Is.EqualTo((ushort)12), "add stays frozen on Masaya");
    }

    [Test]
    public void PrecompileMultipliersFor_Masaya_returns_frozen_table()
    {
        ushort[] resolved = ZkGasSchedule.PrecompileMultipliersFor(ZkGasSchedule.TaikoMasayaChainId);
        Assert.That(resolved[0x05], Is.EqualTo((ushort)1363), "modexp stays frozen on Masaya");
        Assert.That(resolved[0x01], Is.EqualTo((ushort)81), "ecrecover stays frozen on Masaya");
        Assert.That(resolved[0x04], Is.EqualTo((ushort)2), "identity stays frozen on Masaya");
    }

    [Test]
    public void Default_and_Masaya_opcode_tables_are_distinct_instances()
    {
        // Reference inequality matters: the meter caches the resolved table once per
        // construction, so an accidental aliasing of the two arrays would silently fold
        // Masaya into the default schedule.
        Assert.That(
            ZkGasSchedule.OpcodeMultipliersFor(ZkGasSchedule.TaikoMasayaChainId),
            Is.Not.SameAs(ZkGasSchedule.OpcodeMultipliersFor(ZkGasSchedule.TaikoDevnetChainId)));
        Assert.That(
            ZkGasSchedule.PrecompileMultipliersFor(ZkGasSchedule.TaikoMasayaChainId),
            Is.Not.SameAs(ZkGasSchedule.PrecompileMultipliersFor(ZkGasSchedule.TaikoDevnetChainId)));
    }

    [Test]
    public void Meter_With_NonMasaya_ChainId_Uses_Recalibrated_Tables()
    {
        // All non-Masaya chain ids resolve to the recalibrated tables. Cover one
        // explicit chain id from each network family to guard against accidental
        // narrowing of the resolver.
        foreach (ulong chainId in new[]
        {
            ZkGasSchedule.TaikoMainnetChainId,
            ZkGasSchedule.TaikoDevnetChainId,
            ZkGasSchedule.TaikoHoodiChainId,
        })
        {
            ZkGasMeter meter = new(chainId: chainId);
            meter.ChargeOpcode(0x20, 1);
            Assert.That(meter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasSchedule.OpcodeMultipliers[0x20]),
                $"chainId {chainId} must use the recalibrated keccak256 multiplier");
        }
    }
}
