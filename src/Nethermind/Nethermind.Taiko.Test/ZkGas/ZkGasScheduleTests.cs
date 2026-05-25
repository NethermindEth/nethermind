// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        ReadOnlySpan<ushort> resolved = ZkGasSchedule.OpcodeMultipliersFor(chainId).Span;
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
        ReadOnlySpan<ushort> resolved = ZkGasSchedule.PrecompileMultipliersFor(chainId).Span;
        Assert.That(resolved[0x05], Is.EqualTo((ushort)923), "modexp recalibrated");
        Assert.That(resolved[0x01], Is.EqualTo((ushort)47), "ecrecover recalibrated");
        Assert.That(resolved[0x04], Is.EqualTo((ushort)6), "identity recalibrated");
    }

    [Test]
    public void OpcodeMultipliersFor_Masaya_returns_frozen_table()
    {
        ReadOnlySpan<ushort> resolved = ZkGasSchedule.OpcodeMultipliersFor(ZkGasSchedule.TaikoMasayaChainId).Span;
        Assert.That(resolved[0x20], Is.EqualTo((ushort)85), "keccak256 stays frozen on Masaya");
        Assert.That(resolved[0xf1], Is.EqualTo((ushort)25), "call stays frozen on Masaya");
        Assert.That(resolved[0x01], Is.EqualTo((ushort)12), "add stays frozen on Masaya");
    }

    [Test]
    public void PrecompileMultipliersFor_Masaya_returns_frozen_table()
    {
        ReadOnlySpan<ushort> resolved = ZkGasSchedule.PrecompileMultipliersFor(ZkGasSchedule.TaikoMasayaChainId).Span;
        Assert.That(resolved[0x05], Is.EqualTo((ushort)1363), "modexp stays frozen on Masaya");
        Assert.That(resolved[0x01], Is.EqualTo((ushort)81), "ecrecover stays frozen on Masaya");
        Assert.That(resolved[0x04], Is.EqualTo((ushort)2), "identity stays frozen on Masaya");
    }

    [Test]
    public void Default_and_Masaya_tables_diverge_on_a_recalibrated_entry()
    {
        // Behavioural inequality matters: if the resolver were ever bugged to return the
        // default table for Masaya, callers would silently bill keccak256 at 31 instead of 85
        // and Masaya consensus would diverge. Spot-check a known-recalibrated entry on each
        // table to guard against that drift.
        ReadOnlySpan<ushort> defaultOpcodes = ZkGasSchedule.OpcodeMultipliersFor(ZkGasSchedule.TaikoDevnetChainId).Span;
        ReadOnlySpan<ushort> masayaOpcodes = ZkGasSchedule.OpcodeMultipliersFor(ZkGasSchedule.TaikoMasayaChainId).Span;
        Assert.That(masayaOpcodes[0x20], Is.Not.EqualTo(defaultOpcodes[0x20]),
            "keccak256 must differ between default (31) and Masaya (85)");

        ReadOnlySpan<ushort> defaultPrecompiles = ZkGasSchedule.PrecompileMultipliersFor(ZkGasSchedule.TaikoDevnetChainId).Span;
        ReadOnlySpan<ushort> masayaPrecompiles = ZkGasSchedule.PrecompileMultipliersFor(ZkGasSchedule.TaikoMasayaChainId).Span;
        Assert.That(masayaPrecompiles[0x05], Is.Not.EqualTo(defaultPrecompiles[0x05]),
            "modexp must differ between default (923) and Masaya (1363)");
    }

    [TestCase(ZkGasSchedule.TaikoMainnetChainId, TestName = "Mainnet")]
    [TestCase(ZkGasSchedule.TaikoDevnetChainId, TestName = "Devnet")]
    [TestCase(ZkGasSchedule.TaikoHoodiChainId, TestName = "Hoodi")]
    public void Meter_With_NonMasaya_ChainId_Uses_Recalibrated_Tables(ulong chainId)
    {
        ZkGasMeter meter = new(chainId: chainId);
        meter.ChargeOpcode(0x20, 1);
        Assert.That(meter.TxZkGasUsed, Is.EqualTo((ulong)ZkGasSchedule.OpcodeMultipliers[0x20]),
            $"chainId {chainId} must use the recalibrated keccak256 multiplier");
    }
}
