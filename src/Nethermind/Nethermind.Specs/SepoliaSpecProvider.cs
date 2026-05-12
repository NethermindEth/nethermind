// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class SepoliaSpecProvider : ForkScheduleSpecProvider
{
    public const ulong BeaconChainGenesisTimestampConst = 0x62b07d60;
    public const ulong ShanghaiTimestamp = 0x63fd7d60;
    public const ulong CancunTimestamp = 0x65B97D60;
    public const ulong PragueTimestamp = 0x67C7FD60;
    public const ulong OsakaTimestamp = 0x68edfd60;
    public const ulong BPO1Timestamp = 0x68f6fd60;
    public const ulong BPO2Timestamp = 0x68fffd60;

    private static IReleaseSpec? _prague;

    private static IReleaseSpec Prague => LazyInitializer.EnsureInitialized(ref _prague,
        static () => new Prague { DepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress });

    private SepoliaSpecProvider() : base(
    [
        new(0ul, London.Instance),
        new(ShanghaiTimestamp, Shanghai.Instance),
        new(CancunTimestamp, Cancun.Instance),
        new(PragueTimestamp, Prague),
        new(OsakaTimestamp, Osaka.Instance),
        new(BPO1Timestamp, BPO1.Instance),
        new(BPO2Timestamp, BPO2.Instance),
    ], terminalTotalDifficulty: 17000000000000000) =>
        TransitionActivations =
        [
            (ForkActivation)1735371,
            (1735371, ShanghaiTimestamp),
            (1735371, CancunTimestamp),
            (1735371, PragueTimestamp),
            (1735371, OsakaTimestamp),
            (1735371, BPO1Timestamp),
            (1735371, BPO2Timestamp),
        ];

    public override ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public override ulong NetworkId => BlockchainIds.Sepolia;
    public override ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public string SealEngine => SealEngineType.Clique;

    public static SepoliaSpecProvider Instance { get; } = new();
}
