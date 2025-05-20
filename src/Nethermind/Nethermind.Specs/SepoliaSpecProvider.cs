// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class SepoliaSpecProvider : ISpecProvider
{
    public const ulong BeaconChainGenesisTimestampConst = 0x62b07d60;
    public const ulong ShanghaiTimestamp = 0x63fd7d60;
    public const ulong CancunTimestamp = 0x65B97D60;
    public const ulong PragueTimestamp = 0x67C7FD60;
    public const ulong OsakaTimestamp = 0xFFFFFFFF;

    private static IReleaseSpec? _prague;

    private static IReleaseSpec Prague => LazyInitializer.EnsureInitialized(ref _prague,
        static () => new Prague { DepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress });


    private SepoliaSpecProvider() { }

    public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
        forkActivation switch
        {
            { Timestamp: null } or { Timestamp: < ShanghaiTimestamp } => London.Instance,
            { Timestamp: < CancunTimestamp } => Shanghai.Instance,
            { Timestamp: < PragueTimestamp } => Cancun.Instance,
            { Timestamp: < OsakaTimestamp } => Prague,
            _ => Osaka.Instance
        };

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId => Core.BlockchainIds.Sepolia;
    public ulong ChainId => NetworkId;
    public string SealEngine => SealEngineType.Clique;
    public long? DaoBlockNumber => null;
    public ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public ForkActivation? MergeBlockNumber { get; private set; } = null;
    public ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public UInt256? TerminalTotalDifficulty { get; private set; } = 17000000000000000;
    public IReleaseSpec GenesisSpec => London.Instance;
    public ForkActivation[] TransitionActivations { get; } =
    [
        (ForkActivation)1735371,
        (1735371, ShanghaiTimestamp),
        (1735371, CancunTimestamp),
        (1735371, PragueTimestamp),
    ];

    public static SepoliaSpecProvider Instance { get; } = new();
}
