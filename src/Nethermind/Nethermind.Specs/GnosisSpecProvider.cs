// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Specs;

public class GnosisSpecProvider : ISpecProvider
{
    public const ulong ConstantinopleBlockNumber = 1_604_400;
    public const ulong ConstantinopleFixBlockNumber = 2_508_800;
    public const ulong IstanbulBlockNumber = 7_298_030;
    public const ulong BerlinBlockNumber = 16_101_500;
    public const ulong LondonBlockNumber = 19_040_000;
    public const ulong BeaconChainGenesisTimestampConst = 0x61b10dbc;
    public const ulong ShanghaiTimestamp = 0x64c8edbc;
    public const ulong CancunTimestamp = 0x65ef4dbc;
    public const ulong PragueTimestamp = 0x68122dbc;
    public const ulong BalancerTimestamp = 0x69496dbc; // does not alter specs
    public static readonly Address FeeCollector = new("0x6BBe78ee9e474842Dbd4AB4987b3CeFE88426A92");

    private GnosisSpecProvider() { }

    IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation)
    {
        return forkActivation.BlockNumber switch
        {
            < ConstantinopleBlockNumber => GenesisSpec,
            < ConstantinopleFixBlockNumber => Constantinople.Instance,
            < IstanbulBlockNumber => ConstantinopleFix.Instance,
            < BerlinBlockNumber => Istanbul.Instance,
            < LondonBlockNumber => Berlin.Instance,
            _ => forkActivation.Timestamp switch
            {
                null or < ShanghaiTimestamp => LondonGnosis.Instance,
                < CancunTimestamp => ShanghaiGnosis.Instance,
                < PragueTimestamp => CancunGnosis.Instance,
                _ => PragueGnosis.Instance
            }
        };
    }

    public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber.Value;

        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ForkActivation? MergeBlockNumber { get; private set; }
    public ulong TimestampFork => ShanghaiTimestamp;
    // 8626000000000000000000058750000000000000000000
    public UInt256? TerminalTotalDifficulty { get; private set; } = new UInt256(15847367919172845568ul, 12460455203863319017ul, 25349535ul);
    public IReleaseSpec GenesisSpec => Byzantium.Instance;
    public ulong? DaoBlockNumber => null;
    public ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public ulong NetworkId => BlockchainIds.Gnosis;
    public ulong ChainId => BlockchainIds.Gnosis;
    public string SealEngine => SealEngineType.AuRa;
    public ForkActivation[] TransitionActivations { get; }

    public static GnosisSpecProvider Instance { get; } = new();
}
