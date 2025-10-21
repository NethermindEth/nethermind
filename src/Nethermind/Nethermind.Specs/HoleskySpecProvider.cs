// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class HoleskySpecProvider : ISpecProvider
{
    public const ulong GenesisTimestamp = 0x65156994;
    public const ulong ShanghaiTimestamp = 0x6516eac0;
    public const ulong CancunTimestamp = 0x65C36AC0;
    public const ulong PragueTimestamp = 0x67BCEAC0;
    public const ulong OsakaTimestamp = 0x68dceac0;
    public const ulong BPO1Timestamp = 0x68e46ac0;
    public const ulong BPO2Timestamp = 0x68ed6ac0;

    private static IReleaseSpec? _prague;

    private static IReleaseSpec Prague => LazyInitializer.EnsureInitialized(ref _prague,
        static () => new Prague { DepositContractAddress = Eip6110Constants.HoleskyDepositContractAddress });

    private HoleskySpecProvider() { }

    IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation)
    {
        return forkActivation.Timestamp switch
        {
            null or < ShanghaiTimestamp => GenesisSpec,
            < CancunTimestamp => Shanghai.Instance,
            < PragueTimestamp => Cancun.Instance,
            < OsakaTimestamp => Prague,
            < BPO1Timestamp => Osaka.Instance,
            < BPO2Timestamp => BPO1.Instance,
            _ => BPO2.Instance
        };
    }

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId => BlockchainIds.Holesky;
    public ulong ChainId => NetworkId;
    public long? DaoBlockNumber => null;
    public ulong? BeaconChainGenesisTimestamp => GenesisTimestamp;
    public ForkActivation? MergeBlockNumber { get; private set; } = (0, GenesisTimestamp);
    public ulong TimestampFork => ShanghaiTimestamp;
    public UInt256? TerminalTotalDifficulty { get; private set; } = 0;
    public IReleaseSpec GenesisSpec { get; } = London.Instance;
    public ForkActivation[] TransitionActivations { get; } =
    [
        (1, ShanghaiTimestamp),
        (2, CancunTimestamp),
        (3, PragueTimestamp),
        (4, OsakaTimestamp),
        (5, BPO1Timestamp),
        (6, BPO2Timestamp),
    ];

    public static readonly HoleskySpecProvider Instance = new();
}
