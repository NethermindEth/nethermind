// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class MordenSpecProvider : ForkScheduleSpecProvider
{
    public const ulong HomesteadBlockNumber = 494_000;
    public const ulong SpuriousDragonBlockNumber = 1_885_000;

    private MordenSpecProvider() : this(new ForkSchedule
    {
        [GenesisBlockNumber] = Frontier.Instance,
        [HomesteadBlockNumber] = Homestead.Instance,
        [SpuriousDragonBlockNumber] = SpuriousDragon.Instance,
    })
    { }

    private MordenSpecProvider(ForkSchedule schedule) : base(schedule) =>
        TransitionActivations = schedule.ToTransitionActivations();

    public override ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public override ulong NetworkId => BlockchainIds.Morden;
    public override ulong? BeaconChainGenesisTimestamp => null;

    public static MordenSpecProvider Instance { get; } = new();
}
