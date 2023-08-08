// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class GnosisSpecProvider : ISpecProvider
{
    public const long ConstantinopoleBlockNumber = 1_604_400;
    public const long ConstantinopoleFixBlockNumber = 2_508_800;
    public const long IstanbulBlockNumber = 7_298_030;
    public const long BerlinBlockNumber = 16_101_500;
    public const long LondonBlockNumber = 19_040_000;
    public const ulong BeaconChainGenesisTimestamp = 0x61b10dbc;
    public const ulong ShanghaiTimestamp = 0x64c8edbc;

    private GnosisSpecProvider() { }

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        return forkActivation.BlockNumber switch
        {
            < ConstantinopoleBlockNumber => GenesisSpec,
            < ConstantinopoleFixBlockNumber => Constantinople.Instance,
            < IstanbulBlockNumber => ConstantinopleFix.Instance,
            < BerlinBlockNumber => Istanbul.Instance,
            < LondonBlockNumber => Berlin.Instance,
            _ => forkActivation.Timestamp switch
            {
                null or < ShanghaiTimestamp => London.Instance,
                _ => Shanghai.Instance
            }
        };
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
    public UInt256? TerminalTotalDifficulty { get; private set; } = UInt256.Parse("8626000000000000000000058750000000000000000000");
    public IReleaseSpec GenesisSpec => Byzantium.Instance;
    public long? DaoBlockNumber => null;
    public ulong NetworkId => BlockchainIds.Gnosis;
    public ulong ChainId => BlockchainIds.Gnosis;
    public ForkActivation[] TransitionActivations { get; }

    public static GnosisSpecProvider Instance { get; } = new();
}
