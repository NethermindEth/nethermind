// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public const ulong AmsterdamTimestamp = 0x6ac4fd60;

    /// <remarks>Glamsterdam is live, so the consensus layer serves only <see cref="HistoryRetentionConstants.HistoryPruneEpochs"/>.</remarks>
    private static ReleaseSpec WithSepoliaParameters(ReleaseSpec spec)
    {
        spec.MinHistoryRetentionEpochs = HistoryRetentionConstants.HistoryPruneEpochs;
        return spec;
    }

    private SepoliaSpecProvider() : this(new ForkSchedule
    {
        [GenesisBlockNumber] = WithSepoliaParameters(new London()),
        [ShanghaiTimestamp] = WithSepoliaParameters(new Shanghai()),
        [CancunTimestamp] = WithSepoliaParameters(new Cancun()),
        [PragueTimestamp] = WithSepoliaParameters(new Prague { DepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress }),
        [OsakaTimestamp] = WithSepoliaParameters(new Osaka { DepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress }),
        [BPO1Timestamp] = WithSepoliaParameters(new BPO1 { DepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress }),
        [BPO2Timestamp] = WithSepoliaParameters(new BPO2 { DepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress }),
        [AmsterdamTimestamp] = WithSepoliaParameters(new Amsterdam { DepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress }),
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

    public static SepoliaSpecProvider Instance => field ??= new();
}
