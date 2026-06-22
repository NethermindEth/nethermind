// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Taiko.ZkGas;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.ZkGas;

/// <summary>
/// Pins the Unzen ZK gas defaults and covers <see cref="ZkGasSchedule.BuildOpcodeTable"/> and
/// <see cref="ZkGasSchedule.BuildPrecompileTable"/>, the resolvers that turn sparse chainspec
/// entries into runtime multiplier tables. See <see cref="ZkGasTestSchedules"/> for the
/// test-side mirror of the schedule values shipped in <c>Chains/taiko-alethia.json</c>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ZkGasScheduleTests
{
    [Test]
    public void Block_zk_gas_limit_is_pinned() =>
        Assert.That(ZkGasSchedule.BlockZkGasLimit, Is.EqualTo(100_000_000UL));

    [Test]
    public void Meter_default_ctor_uses_canonical_block_limit()
    {
        ZkGasMeter meter = new();
        Assert.That(meter.BlockZkGasLimit, Is.EqualTo(ZkGasSchedule.BlockZkGasLimit));
    }

    [Test]
    public void Meter_honors_explicit_block_limit()
    {
        const ulong customLimit = 1_000_000_000UL;
        ZkGasMeter meter = new(customLimit);
        Assert.That(meter.BlockZkGasLimit, Is.EqualTo(customLimit));
    }

    // ── opcode table resolution ──────────────────────────────────────────────

    [TestCase(true, TestName = "null entries")]
    [TestCase(false, TestName = "empty entries")]
    public void BuildOpcodeTable_returns_all_failsafe_when_no_entries(bool useNull)
    {
        Dictionary<long, long>? entries = useNull ? null : [];
        ReadOnlyMemory<ushort> resolved = ZkGasSchedule.BuildOpcodeTable(entries);

        Assert.That(resolved.Length, Is.EqualTo(256));
        for (int i = 0; i < 256; i++)
        {
            Assert.That(resolved.Span[i], Is.EqualTo(ZkGasSchedule.FailsafeMultiplier),
                $"index 0x{i:x2} must default to fail-safe");
        }
    }

    [Test]
    public void BuildOpcodeTable_applies_listed_entries_and_failsafe_fills_the_rest()
    {
        Dictionary<long, long> entries = new() { [0x20] = 85, [0xf1] = 25 };
        ReadOnlyMemory<ushort> resolved = ZkGasSchedule.BuildOpcodeTable(entries);

        Assert.That(resolved.Span[0x20], Is.EqualTo((ushort)85), "listed entry is applied");
        Assert.That(resolved.Span[0xf1], Is.EqualTo((ushort)25), "listed entry is applied");
        Assert.That(resolved.Span[0x01], Is.EqualTo(ZkGasSchedule.FailsafeMultiplier), "unlisted entry is fail-safe");
    }

    [TestCase(-1L, TestName = "negative opcode index")]
    [TestCase(256L, TestName = "opcode index past 0xff")]
    public void BuildOpcodeTable_rejects_out_of_range_index(long index)
    {
        Dictionary<long, long> entries = new() { [index] = 1 };
        Assert.That(() => ZkGasSchedule.BuildOpcodeTable(entries),
            Throws.TypeOf<InvalidConfigurationException>());
    }

    [TestCase(-1L, TestName = "negative opcode multiplier")]
    [TestCase(65536L, TestName = "opcode multiplier past ushort.MaxValue")]
    public void BuildOpcodeTable_rejects_out_of_range_multiplier(long multiplier)
    {
        Dictionary<long, long> entries = new() { [0x01] = multiplier };
        Assert.That(() => ZkGasSchedule.BuildOpcodeTable(entries),
            Throws.TypeOf<InvalidConfigurationException>());
    }

    // ── precompile table resolution ──────────────────────────────────────────

    [TestCase(true, TestName = "null entries")]
    [TestCase(false, TestName = "empty entries")]
    public void BuildPrecompileTable_returns_empty_when_no_entries(bool useNull)
    {
        // Empty dictionary; meter does TryGet so absence resolves to FailsafeMultiplier at the
        // call site — that's the consensus-affecting behavior we mirror from alethia-reth.
        Dictionary<string, long>? entries = useNull ? null : [];
        FrozenDictionary<AddressAsKey, ushort> resolved = ZkGasSchedule.BuildPrecompileTable(entries);

        Assert.That(resolved, Is.Empty);
    }

    [Test]
    public void BuildPrecompileTable_keys_full_address_and_distinguishes_high_range()
    {
        // L1Sload sits at 0x…010001 — low byte 0x01 would collide with ecrecover under a
        // low-byte-indexed table. Full-address keying separates them.
        Dictionary<string, long> entries = new()
        {
            ["0x0000000000000000000000000000000000000001"] = 47,    // ecrecover
            ["0x0000000000000000000000000000000000010001"] = 200,   // L1Sload
        };

        FrozenDictionary<AddressAsKey, ushort> resolved = ZkGasSchedule.BuildPrecompileTable(entries);

        Assert.That(resolved[Address.FromNumber(0x01)], Is.EqualTo((ushort)47));
        Assert.That(resolved[Address.FromNumber(0x10001)], Is.EqualTo((ushort)200));
    }

    [TestCase("not-an-address")]
    [TestCase("0xZZZZ")]
    public void BuildPrecompileTable_rejects_malformed_address(string addressHex)
    {
        Dictionary<string, long> entries = new() { [addressHex] = 1 };
        Assert.That(() => ZkGasSchedule.BuildPrecompileTable(entries),
            Throws.TypeOf<InvalidConfigurationException>());
    }

    [TestCase(-1L)]
    [TestCase(65536L)]
    public void BuildPrecompileTable_rejects_out_of_range_multiplier(long multiplier)
    {
        Dictionary<string, long> entries =
            new() { ["0x0000000000000000000000000000000000000001"] = multiplier };
        Assert.That(() => ZkGasSchedule.BuildPrecompileTable(entries),
            Throws.TypeOf<InvalidConfigurationException>());
    }

    [Test]
    public void BuildPrecompileTable_round_trips_the_alethia_recalibrated_addresses()
    {
        // Sanity check that the test-side mirror is consistent with what BuildPrecompileTable
        // produces from the same hex-address entries.
        Dictionary<string, long> entries = new()
        {
            ["0x0000000000000000000000000000000000000001"] = 47,
            ["0x0000000000000000000000000000000000000005"] = 154,
            ["0x0000000000000000000000000000000000000011"] = 208,
        };

        FrozenDictionary<AddressAsKey, ushort> resolved = ZkGasSchedule.BuildPrecompileTable(entries);

        Assert.That(resolved[Address.FromNumber(0x01)], Is.EqualTo(ZkGasTestSchedules.PrecompileMultiplier(0x01)));
        Assert.That(resolved[Address.FromNumber(0x05)], Is.EqualTo(ZkGasTestSchedules.PrecompileMultiplier(0x05)));
        Assert.That(resolved[Address.FromNumber(0x11)], Is.EqualTo(ZkGasTestSchedules.PrecompileMultiplier(0x11)));
    }
}
