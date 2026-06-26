// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Taiko.TaikoSpec;
using Nethermind.Taiko.Test.ZkGas;
using Nethermind.Taiko.ZkGas;
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

    // Hoodi fork schedule mirrored from alethia-reth TAIKO_HOODI_HARDFORKS: Unzen set to
    // 2026-06-18 13:00:00 UTC by alethia-reth PR #210.
    private const ulong HoodiShastaTimestamp = 1_770_296_400;
    private const ulong HoodiUnzenTimestamp = 1_781_787_600;

    [Test]
    public void Hoodi_chainspec_schedules_Unzen_at_the_upstream_fork_time()
    {
        (_, TaikoChainSpecEngineParameters parameters) = LoadHoodiChainSpec();

        Assert.That(parameters.UnzenTimestamp, Is.EqualTo(HoodiUnzenTimestamp));

        SortedSet<ulong> blockNumbers = [];
        SortedSet<ulong> timestamps = [];
        parameters.AddTransitions(blockNumbers, timestamps);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockNumbers, Is.Empty,
                "Ontake/Pacaya at genesis must stay out of the fork-id walk");
            Assert.That(timestamps, Is.EqualTo(new[] { HoodiShastaTimestamp, HoodiUnzenTimestamp }),
                "fork-id walk must mirror alethia-reth TAIKO_HOODI_HARDFORKS: Shasta, then Unzen");
        }
    }

    [Test]
    public void Hoodi_Unzen_flips_osaka_semantics_and_zk_gas_schedule_atomically()
    {
        (ChainSpec chainSpec, TaikoChainSpecEngineParameters parameters) = LoadHoodiChainSpec();
        TaikoChainSpecBasedSpecProvider provider = new(chainSpec, parameters, LimboLogs.Instance);

        ITaikoReleaseSpec shasta = (ITaikoReleaseSpec)provider.GetSpec((0, HoodiUnzenTimestamp - 1));
        ITaikoReleaseSpec unzen = (ITaikoReleaseSpec)provider.GetSpec((0, HoodiUnzenTimestamp));

        // Taiko executes Unzen with Osaka semantics: alethia-reth pins Cancun, Prague, and Osaka
        // to the Unzen activation (extend_with_shared_hardforks), so every flag must flip at the
        // same instant for NMC to stay in consensus with alethia-reth peers.
        (string Name, Func<ITaikoReleaseSpec, bool> Flag)[] unzenImpliedForks =
        [
            (nameof(ITaikoReleaseSpec.IsUnzenEnabled), static s => s.IsUnzenEnabled),
            (nameof(ITaikoReleaseSpec.IsEip1153Enabled), static s => s.IsEip1153Enabled),
            (nameof(ITaikoReleaseSpec.IsEip4788Enabled), static s => s.IsEip4788Enabled),
            (nameof(ITaikoReleaseSpec.IsEip4844Enabled), static s => s.IsEip4844Enabled),
            (nameof(ITaikoReleaseSpec.IsEip5656Enabled), static s => s.IsEip5656Enabled),
            (nameof(ITaikoReleaseSpec.IsEip6780Enabled), static s => s.IsEip6780Enabled),
            (nameof(ITaikoReleaseSpec.IsEip2537Enabled), static s => s.IsEip2537Enabled),
            (nameof(ITaikoReleaseSpec.IsEip2935Enabled), static s => s.IsEip2935Enabled),
            (nameof(ITaikoReleaseSpec.IsEip6110Enabled), static s => s.IsEip6110Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7002Enabled), static s => s.IsEip7002Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7251Enabled), static s => s.IsEip7251Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7623Enabled), static s => s.IsEip7623Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7702Enabled), static s => s.IsEip7702Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7594Enabled), static s => s.IsEip7594Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7823Enabled), static s => s.IsEip7823Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7825Enabled), static s => s.IsEip7825Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7883Enabled), static s => s.IsEip7883Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7918Enabled), static s => s.IsEip7918Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7934Enabled), static s => s.IsEip7934Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7939Enabled), static s => s.IsEip7939Enabled),
            (nameof(ITaikoReleaseSpec.IsEip7951Enabled), static s => s.IsEip7951Enabled),
        ];

        using (Assert.EnterMultipleScope())
        {
            foreach ((string name, Func<ITaikoReleaseSpec, bool> flag) in unzenImpliedForks)
            {
                Assert.That(flag(shasta), Is.False, $"{name} one second before Unzen");
                Assert.That(flag(unzen), Is.True, $"{name} at Unzen");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unzen.UnzenBlockZkGasLimit, Is.EqualTo(ZkGasSchedule.BlockZkGasLimit));
            Assert.That(unzen.UnzenTxIntrinsicZkGas, Is.EqualTo(ZkGasSchedule.TxIntrinsicZkGas));
            Assert.That(unzen.UnzenOpcodeZkGasMultipliers.Span.SequenceEqual(ZkGasTestSchedules.OpcodeMultipliers.Span),
                Is.True, "opcode multipliers must match the recalibrated table mirrored in ZkGasTestSchedules");
            Assert.That(unzen.UnzenPrecompileZkGasMultipliers.Count,
                Is.EqualTo(ZkGasTestSchedules.PrecompileMultipliers.Count));
            foreach ((AddressAsKey address, ushort multiplier) in ZkGasTestSchedules.PrecompileMultipliers)
            {
                Assert.That(unzen.UnzenPrecompileZkGasMultipliers.TryGetValue(address, out ushort actual) && actual == multiplier,
                    Is.True, $"precompile {address.Value} multiplier");
            }
        }
    }

    private static (ChainSpec ChainSpec, TaikoChainSpecEngineParameters Parameters) LoadHoodiChainSpec()
    {
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/taiko-hoodi.json");
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);
        TaikoChainSpecEngineParameters parameters =
            chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<TaikoChainSpecEngineParameters>();
        return (chainSpec, parameters);
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
            new EthereumJsonSerializer().Deserialize<TaikoChainSpecEngineParameters>(json)!;

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
