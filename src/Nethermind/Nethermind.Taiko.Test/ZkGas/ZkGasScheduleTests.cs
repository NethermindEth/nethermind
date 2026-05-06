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
    public void Resolve_returns_masaya_limit_for_masaya_chain_id() =>
        Assert.That(
            ZkGasSchedule.ResolveBlockZkGasLimit(ZkGasSchedule.TaikoMasayaChainId),
            Is.EqualTo(ZkGasSchedule.MasayaBlockZkGasLimit));

    [TestCase(ZkGasSchedule.TaikoMainnetChainId)]
    [TestCase(ZkGasSchedule.TaikoDevnetChainId)]
    [TestCase(ZkGasSchedule.TaikoHoodiChainId)]
    [TestCase(0UL)]
    [TestCase(1UL)]
    public void Resolve_returns_default_limit_for_other_chain_ids(ulong chainId) =>
        Assert.That(
            ZkGasSchedule.ResolveBlockZkGasLimit(chainId),
            Is.EqualTo(ZkGasSchedule.BlockZkGasLimit));

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
}
