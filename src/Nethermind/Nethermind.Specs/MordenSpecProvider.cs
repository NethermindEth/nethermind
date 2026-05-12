// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class MordenSpecProvider : ForkScheduleSpecProvider
{
    public const long HomesteadBlockNumber = 494_000;
    public const long SpuriousDragonBlockNumber = 1_885_000;

    private MordenSpecProvider() : base(
    [
        new(0L, Frontier.Instance),
        new(HomesteadBlockNumber, Homestead.Instance),
        new(SpuriousDragonBlockNumber, SpuriousDragon.Instance),
    ]) =>
        TransitionActivations =
        [
            (ForkActivation)HomesteadBlockNumber,
            (ForkActivation)SpuriousDragonBlockNumber,
        ];

    public override ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public override ulong NetworkId => BlockchainIds.Morden;
    public override ulong? BeaconChainGenesisTimestamp => null;

    public static MordenSpecProvider Instance { get; } = new();
}
