// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Exceptions;
using Nethermind.Taiko.ZkGas;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.ZkGas;

/// <summary>
/// Pins the Unzen ZK gas block limits and chain ids, and covers
/// <see cref="ZkGasSchedule.BuildOverriddenTable"/> — the chainspec-driven multiplier resolution
/// that replaces the former chain-id table dispatch. A network that finalized Unzen under a
/// different schedule (e.g. Masaya) pins it in its chainspec, not in code.
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

    // ── chainspec-driven multiplier resolution (taiko-mono#21720 / alethia-reth#187) ─

    [TestCase(true, TestName = "null override")]
    [TestCase(false, TestName = "empty override")]
    public void BuildOverriddenTable_returns_defaults_when_no_override(bool useNull)
    {
        Dictionary<long, long>? overrides = useNull ? null : [];
        ReadOnlyMemory<ushort> resolved = ZkGasSchedule.BuildOverriddenTable(overrides, ZkGasSchedule.OpcodeMultipliers);
        Assert.That(resolved.Span.SequenceEqual(ZkGasSchedule.OpcodeMultipliers.Span), Is.True);
    }

    [Test]
    public void BuildOverriddenTable_applies_listed_entries_and_failsafe_fills_the_rest()
    {
        // A Masaya-style pin: list only the entries the network uses; everything unlisted is fail-safe.
        Dictionary<long, long> frozen = new() { [0x20] = 85, [0xf1] = 25 };
        ReadOnlyMemory<ushort> resolved = ZkGasSchedule.BuildOverriddenTable(frozen, ZkGasSchedule.OpcodeMultipliers);

        Assert.That(resolved.Span[0x20], Is.EqualTo((ushort)85), "listed entry is applied");
        Assert.That(resolved.Span[0xf1], Is.EqualTo((ushort)25), "listed entry is applied");
        Assert.That(resolved.Span[0x01], Is.EqualTo(ZkGasSchedule.FailsafeMultiplier), "unlisted entry is fail-safe");
        Assert.That(resolved.Span[0x20], Is.Not.EqualTo(ZkGasSchedule.OpcodeMultipliers.Span[0x20]),
            "override diverges from the recalibrated default it replaces");
    }

    [TestCase(-1L, TestName = "negative index")]
    [TestCase(256L, TestName = "index past 0xff")]
    public void BuildOverriddenTable_rejects_out_of_range_index(long index)
    {
        Dictionary<long, long> overrides = new() { [index] = 1 };
        Assert.That(() => ZkGasSchedule.BuildOverriddenTable(overrides, ZkGasSchedule.OpcodeMultipliers),
            Throws.TypeOf<InvalidConfigurationException>());
    }

    [TestCase(-1L, TestName = "negative multiplier")]
    [TestCase(65536L, TestName = "multiplier past ushort.MaxValue")]
    public void BuildOverriddenTable_rejects_out_of_range_multiplier(long multiplier)
    {
        Dictionary<long, long> overrides = new() { [0x01] = multiplier };
        Assert.That(() => ZkGasSchedule.BuildOverriddenTable(overrides, ZkGasSchedule.OpcodeMultipliers),
            Throws.TypeOf<InvalidConfigurationException>());
    }
}
