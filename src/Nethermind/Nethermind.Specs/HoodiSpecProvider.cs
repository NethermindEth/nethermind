// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class HoodiSpecProvider : ForkScheduleSpecProvider
{
    public const ulong ShanghaiTimestamp = 0x0;
    public const ulong CancunTimestamp = 0x0;
    public const ulong PragueTimestamp = 0x67e41118;
    public const ulong OsakaTimestamp = 0x69011118;
    public const ulong BPO1Timestamp = 0x690b9118;
    public const ulong BPO2Timestamp = 0x69149118;

    private static IReleaseSpec? _prague;

    private static IReleaseSpec Prague => LazyInitializer.EnsureInitialized(ref _prague,
        static () => new Prague { DepositContractAddress = Eip6110Constants.HoodiDepositContractAddress });

    private HoodiSpecProvider() : this(new ForkSchedule
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

    private HoodiSpecProvider(ForkSchedule schedule) : base(schedule,
        terminalTotalDifficulty: 0,
        mergeBlockNumber: (GenesisBlockNumber, GenesisTimestamp)) =>
        TransitionActivations = schedule.ToTransitionActivations(
            postMergeBlock: GenesisBlockNumber + 1);

    public override ulong TimestampFork => ShanghaiTimestamp;
    public override ulong NetworkId => BlockchainIds.Hoodi;
    public override ulong? BeaconChainGenesisTimestamp => GenesisTimestamp;

    public static readonly HoodiSpecProvider Instance = new();
}
