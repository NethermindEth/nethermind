// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Serialization.Json;
using Nethermind.Taiko.TaikoSpec;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Regression tests for the EIP-2124 fork-id walk on Taiko and the chainspec-driven Unzen ZK gas
/// schedule selection. Transitions added by <see cref="TaikoChainSpecEngineParameters.AddTransitions"/>
/// are folded into the CRC32 fork-id chain by <see cref="Nethermind.Network.ForkInfo"/>, so any
/// extra/missing entry here changes the fork-id NMC announces in eth Status — observed against
/// Taiko Internal devnet bootnodes (alethia-reth) as <c>InvalidForkId IncompatibleOrStale</c>
/// disconnects when Shasta=0 / Unzen=0 were folded unconditionally.
/// </summary>
[TestFixture]
public class TaikoChainSpecEngineParametersTests
{
    [Test]
    public void AddTransitions_skips_genesis_time_Shasta_and_Unzen()
    {
        // Mirrors gavin's nmc-devnet.json: Shasta active at genesis, Unzen at a future ts.
        TaikoChainSpecEngineParameters parameters = new()
        {
            OntakeTransition = 0,
            PacayaTransition = 0,
            ShastaTimestamp = 0,
            UnzenTimestamp = 0x69df3615,
        };

        SortedSet<ulong> blockNumbers = [];
        SortedSet<ulong> timestamps = [];

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(blockNumbers, Is.Empty,
            "Ontake/Pacaya at block 0 must be filtered — EIP-2124 skips activations at or before genesis");
        Assert.That(timestamps, Is.EqualTo(new[] { 0x69df3615ul }),
            "Shasta=0 must be filtered; Unzen at a future ts must be folded");
    }

    [Test]
    public void AddTransitions_includes_post_genesis_block_transitions()
    {
        // Mirrors taiko-alethia.json: Ontake/Pacaya activate post-genesis.
        TaikoChainSpecEngineParameters parameters = new()
        {
            OntakeTransition = 0x836c0,
            PacayaTransition = 0x11CAB0,
            ShastaTimestamp = 0x69CE6BD4,
        };

        SortedSet<ulong> blockNumbers = [];
        SortedSet<ulong> timestamps = [];

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(blockNumbers, Is.EqualTo(new[] { 0x836c0UL, 0x11CAB0UL }));
        Assert.That(timestamps, Is.EqualTo(new[] { 0x69CE6BD4ul }));
    }

    [Test]
    public void AddTransitions_handles_all_null_transitions()
    {
        TaikoChainSpecEngineParameters parameters = new();

        SortedSet<ulong> blockNumbers = [];
        SortedSet<ulong> timestamps = [];

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(blockNumbers, Is.Empty);
        Assert.That(timestamps, Is.Empty);
    }

    [Test]
    public void AddTransitions_filters_genesis_time_Rip7728_and_L1StaticCall()
    {
        TaikoChainSpecEngineParameters parameters = new()
        {
            Rip7728TransitionTimestamp = 0,
            L1StaticCallTransitionTimestamp = 0,
        };

        SortedSet<ulong> blockNumbers = [];
        SortedSet<ulong> timestamps = [];

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(timestamps, Is.Empty,
            "RIP-7728 and L1StaticCall at genesis must be filtered too");
    }

    [Test]
    public void AddTransitions_folds_real_schedule_timestamps_but_filters_placeholder()
    {
        // The MaxValue-1 placeholder (used by taiko-alethia today) must NOT pollute the fork-id
        // chain; real-ts schedules must be folded so a schedule swap is its own fork point.
        TaikoChainSpecEngineParameters parameters = new()
        {
            UnzenTimestamp = 0x1000,
            UnzenZkGasSchedules =
            [
                new TaikoUnzenZkGasSchedule { Timestamp = 0x1000 }, // dedups with UnzenTimestamp
                new TaikoUnzenZkGasSchedule { Timestamp = 0x2000 }, // future schedule swap
                new TaikoUnzenZkGasSchedule { Timestamp = ulong.MaxValue - 1 }, // placeholder
            ],
        };

        SortedSet<ulong> blockNumbers = [];
        SortedSet<ulong> timestamps = [];

        parameters.AddTransitions(blockNumbers, timestamps);

        Assert.That(timestamps, Is.EqualTo(new[] { 0x1000ul, 0x2000ul }),
            "placeholder MaxValue-1 schedule timestamps must not enter the fork-id chain");
    }

    [Test]
    public void Unzen_schedules_deserialize_from_chainspec_with_full_precompile_addresses()
    {
        // The engine.Taiko params block carries the schedule(s) verbatim. Opcode keys are byte
        // indices (hex or decimal); precompile keys are full 20-byte hex addresses so canonical
        // EVM and Taiko-extended precompiles (e.g. L1Sload @ 0x…010001) live in the same table
        // without colliding by low byte.
        const string json = @"{
            ""unzenTimestamp"": ""0x1"",
            ""unzenZkGasSchedules"": [
                {
                    ""timestamp"": ""0x100"",
                    ""opcodeMultipliers"": { ""0x20"": ""0x55"", ""0xf1"": 25 },
                    ""precompileMultipliers"": {
                        ""0x0000000000000000000000000000000000000005"": 1363,
                        ""0x0000000000000000000000000000000000010001"": 200
                    }
                }
            ]
        }";

        TaikoChainSpecEngineParameters parameters =
            new EthereumJsonSerializer().Deserialize<TaikoChainSpecEngineParameters>(json);

        Assert.That(parameters.UnzenZkGasSchedules, Is.Not.Null);
        Assert.That(parameters.UnzenZkGasSchedules!, Has.Count.EqualTo(1));

        TaikoUnzenZkGasSchedule schedule = parameters.UnzenZkGasSchedules[0];
        Assert.That(schedule.Timestamp, Is.EqualTo(0x100ul));
        Assert.That(schedule.OpcodeMultipliers![0x20], Is.EqualTo(85L), "hex opcode value parses");
        Assert.That(schedule.OpcodeMultipliers[0xf1], Is.EqualTo(25L), "decimal opcode value parses");
        Assert.That(schedule.PrecompileMultipliers!["0x0000000000000000000000000000000000000005"], Is.EqualTo(1363L));
        Assert.That(schedule.PrecompileMultipliers["0x0000000000000000000000000000000000010001"], Is.EqualTo(200L),
            "Taiko-extended precompile address coexists with canonical low-byte EVM precompiles");
    }
}
