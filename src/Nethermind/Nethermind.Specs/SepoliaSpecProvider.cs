// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class SepoliaSpecProvider : ForkScheduleSpecProvider
{
    public const ulong MergeForkIdBlockNumber = 1735371;
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

    private SepoliaSpecProvider() : this(new ForkSchedule
    {
        [GenesisBlockNumber] = London.Instance,
        [ShanghaiTimestamp] = Shanghai.Instance,
        [CancunTimestamp] = Cancun.Instance,
        [PragueTimestamp] = Prague,
        [OsakaTimestamp] = Osaka.Instance,
        [BPO1Timestamp] = BPO1.Instance,
        [BPO2Timestamp] = BPO2.Instance,
    })
    { }

    private SepoliaSpecProvider(ForkSchedule schedule) : base(schedule,
        terminalTotalDifficulty: 17000000000000000) =>
        TransitionActivations = schedule.ToTransitionActivations(
            postMergeBlock: MergeForkIdBlockNumber,
            incrementBlockPerTimestampFork: false,
            prepend: [(ForkActivation)MergeForkIdBlockNumber]);

    public override ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public override ulong NetworkId => BlockchainIds.Sepolia;
    public override ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public string SealEngine => SealEngineType.Clique;

    public static SepoliaSpecProvider Instance { get; } = new();
}
