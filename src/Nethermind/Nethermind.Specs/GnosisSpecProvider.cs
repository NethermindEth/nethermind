// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Specs;

public class GnosisSpecProvider : IForkAwareSpecProvider
{
    public const long ConstantinopleBlockNumber = 1_604_400;
    public const long ConstantinopleFixBlockNumber = 2_508_800;
    public const long IstanbulBlockNumber = 7_298_030;
    public const long PosdaoTransitionBlockNumber = 9_186_425; // does not alter EVM specs, fork-id boundary only
    public const long BerlinBlockNumber = 16_101_500;
    public const long LondonBlockNumber = 19_040_000;
    public const ulong BeaconChainGenesisTimestampConst = 0x61b10dbc;
    public const ulong ShanghaiTimestamp = 0x64c8edbc;
    public const ulong CancunTimestamp = 0x65ef4dbc;
    public const ulong PragueTimestamp = 0x68122dbc;
    public const ulong BalancerTimestamp = 0x69496dbc; // does not alter specs
    public const ulong OsakaTimestamp = 0x69de2dbc;
    public static readonly Address FeeCollector = new("0x6BBe78ee9e474842Dbd4AB4987b3CeFE88426A92");

    private GnosisSpecProvider() { }

    // Lazy because LondonGnosis.Apply → GnosisSpecProvider.FeeCollector → GnosisSpecProvider static
    // init → LondonGnosis.Instance (still building → null) is a circular init if Chiado or anything
    // else touches a Gnosis fork instance before GnosisSpecProvider is first directly accessed.
    private static readonly Lazy<ForkSpec[]> _forkSchedule = new(static () =>
    [
        new(0L, Byzantium.Instance),
        new(ConstantinopleBlockNumber, Constantinople.Instance),
        new(ConstantinopleFixBlockNumber, ConstantinopleFix.Instance),
        new(IstanbulBlockNumber, Istanbul.Instance),
        new(BerlinBlockNumber, Berlin.Instance),
        new(LondonBlockNumber, LondonGnosis.Instance),
        new(ShanghaiTimestamp, ShanghaiGnosis.Instance),
        new(CancunTimestamp, CancunGnosis.Instance),
        new(PragueTimestamp, PragueGnosis.Instance),
        new(OsakaTimestamp, OsakaGnosis.Instance),
    ]);

    private static ForkSpec[] ForkSchedule => _forkSchedule.Value;

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        if (forkActivation.Timestamp is ulong ts)
        {
            for (int i = ForkSchedule.Length - 1; i >= 0; i--)
            {
                if (ForkSchedule[i].Timestamp is ulong forkTs && ts >= forkTs)
                    return ForkSchedule[i].Spec;
            }
        }

        for (int i = ForkSchedule.Length - 1; i >= 0; i--)
        {
            if (ForkSchedule[i].Block is long forkBlock && forkActivation.BlockNumber >= forkBlock)
                return ForkSchedule[i].Spec;
        }

        return ForkSchedule[0].Spec;
    }

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;

        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ForkActivation? MergeBlockNumber { get; private set; }
    public ulong TimestampFork => ShanghaiTimestamp;
    // 8626000000000000000000058750000000000000000000
    public UInt256? TerminalTotalDifficulty { get; private set; } = new UInt256(15847367919172845568ul, 12460455203863319017ul, 25349535ul);
    public IReleaseSpec GenesisSpec => Byzantium.Instance;
    public long? DaoBlockNumber => null;
    public ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public ulong NetworkId => BlockchainIds.Gnosis;
    public ulong ChainId => BlockchainIds.Gnosis;
    public string SealEngine => SealEngineType.AuRa;
    public ForkActivation[] TransitionActivations { get; } =
    [
        (ForkActivation)ConstantinopleBlockNumber,
        (ForkActivation)ConstantinopleFixBlockNumber,
        (ForkActivation)IstanbulBlockNumber,
        (ForkActivation)PosdaoTransitionBlockNumber,
        (ForkActivation)BerlinBlockNumber,
        (ForkActivation)LondonBlockNumber,
        (LondonBlockNumber, ShanghaiTimestamp),
        (LondonBlockNumber, CancunTimestamp),
        (LondonBlockNumber, PragueTimestamp),
        (LondonBlockNumber, BalancerTimestamp),
        (LondonBlockNumber, OsakaTimestamp),
    ];

    private static readonly Lazy<FrozenDictionary<string, IReleaseSpec>> _forks =
        new(static () => ForkSchedule.ToFrozenDictionary(static x => x.Spec.Name, static x => x.Spec, StringComparer.OrdinalIgnoreCase));

    public static FrozenDictionary<string, IReleaseSpec> Forks => _forks.Value;

    private static readonly Lazy<string[]> _availableForks =
        new(static () => [.. Forks.Keys.Order()]);

    public IEnumerable<string> AvailableForks => _availableForks.Value;
    public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec) => Forks.TryGetValue(forkName, out spec);

    public static GnosisSpecProvider Instance { get; } = new();
}
