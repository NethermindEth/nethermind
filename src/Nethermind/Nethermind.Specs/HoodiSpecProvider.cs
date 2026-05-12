// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class HoodiSpecProvider : ForkScheduleSpecProvider
{
    public const ulong GenesisTimestamp = 0x0;
    public const ulong ShanghaiTimestamp = 0x0;
    public const ulong CancunTimestamp = 0x0;
    public const ulong PragueTimestamp = 0x67e41118;
    public const ulong OsakaTimestamp = 0x69011118;
    public const ulong BPO1Timestamp = 0x690b9118;
    public const ulong BPO2Timestamp = 0x69149118;

    private static IReleaseSpec? _prague;

    private static IReleaseSpec Prague => LazyInitializer.EnsureInitialized(ref _prague,
        static () => new Prague { DepositContractAddress = Eip6110Constants.HoodiDepositContractAddress });

    private HoodiSpecProvider() : base(
    [
        new(0ul, London.Instance),
        new(ShanghaiTimestamp, Shanghai.Instance),
        new(CancunTimestamp, Cancun.Instance),
        new(PragueTimestamp, Prague),
        new(OsakaTimestamp, Osaka.Instance),
        new(BPO1Timestamp, BPO1.Instance),
        new(BPO2Timestamp, BPO2.Instance),
    ], terminalTotalDifficulty: 0, mergeBlockNumber: (0, GenesisTimestamp)) =>
        TransitionActivations =
        [
            (1, ShanghaiTimestamp),
            (2, CancunTimestamp),
            (3, PragueTimestamp),
            (4, OsakaTimestamp),
            (5, BPO1Timestamp),
            (6, BPO2Timestamp),
        ];

    public override ulong TimestampFork => ShanghaiTimestamp;
    public override ulong NetworkId => BlockchainIds.Hoodi;
    public override ulong? BeaconChainGenesisTimestamp => GenesisTimestamp;

    public static readonly HoodiSpecProvider Instance = new();
}
