// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>A stored beacon state decoded for the API, with the fields the state endpoints read from any fork that has them.</summary>
/// <remarks>
/// An Electra or Fulu state is a <see cref="BeaconStateElectra"/> (Fulu only adds <c>proposer_lookahead</c>); a Gloas state is
/// the separate <see cref="BeaconStateGloas"/> (consensus-specs v1.7.0-beta.2 gloas/beacon-chain.md). Exactly one of
/// <see cref="Electra"/> and <see cref="Gloas"/> is set.
/// </remarks>
internal sealed class ApiState
{
    private ApiState(BeaconFork fork, BeaconStateElectra? electra, BeaconStateGloas? gloas)
    {
        Fork = fork;
        Electra = electra;
        Gloas = gloas;
    }

    public static ApiState Of(BeaconFork fork, BeaconStateElectra state) => new(fork, state, null);

    public static ApiState Of(BeaconStateGloas state) => new(BeaconFork.Gloas, null, state);

    /// <summary>The fork whose layout the state was decoded with.</summary>
    public BeaconFork Fork { get; }

    /// <summary>The state when <see cref="Fork"/> is Electra or Fulu.</summary>
    public BeaconStateElectra? Electra { get; }

    /// <summary>The state when <see cref="Fork"/> is Gloas.</summary>
    public BeaconStateGloas? Gloas { get; }

    public ulong Slot => Electra?.Slot ?? Gloas!.Slot;

    public Fork ForkVersion => Electra is { } e ? e.Fork! : Gloas!.Fork!;

    public BeaconBlockHeader LatestBlockHeader => Electra is { } e ? e.LatestBlockHeader! : Gloas!.LatestBlockHeader!;

    public Checkpoint PreviousJustifiedCheckpoint => Electra is { } e ? e.PreviousJustifiedCheckpoint! : Gloas!.PreviousJustifiedCheckpoint!;

    public Checkpoint CurrentJustifiedCheckpoint => Electra is { } e ? e.CurrentJustifiedCheckpoint! : Gloas!.CurrentJustifiedCheckpoint!;

    public Checkpoint FinalizedCheckpoint => Electra is { } e ? e.FinalizedCheckpoint! : Gloas!.FinalizedCheckpoint!;

    public Validator[] Validators => Electra is { } e ? e.Validators! : Gloas!.Validators!;

    public ulong[] Balances => Electra is { } e ? e.Balances! : Gloas!.Balances!;

    public SyncCommittee CurrentSyncCommittee => Electra is { } e ? e.CurrentSyncCommittee! : Gloas!.CurrentSyncCommittee!;

    public SyncCommittee NextSyncCommittee => Electra is { } e ? e.NextSyncCommittee! : Gloas!.NextSyncCommittee!;

    public PendingDeposit[] PendingDeposits => Electra is { } e ? e.PendingDeposits! : Gloas!.PendingDeposits!;

    public PendingPartialWithdrawal[] PendingPartialWithdrawals => Electra is { } e ? e.PendingPartialWithdrawals! : Gloas!.PendingPartialWithdrawals!;

    public PendingConsolidation[] PendingConsolidations => Electra is { } e ? e.PendingConsolidations! : Gloas!.PendingConsolidations!;

    /// <summary>EIP-7917 <c>proposer_lookahead</c>; <c>null</c> for an Electra state, which predates it.</summary>
    public ulong[]? ProposerLookahead => Electra is { } e ? (e as BeaconStateFulu)?.ProposerLookahead : Gloas!.ProposerLookahead;

    /// <summary>Spec <c>get_randao_mix</c> on the state's own layout.</summary>
    /// <exception cref="BeaconStateException">The epoch is outside the historical-vector window.</exception>
    public Hash256 GetRandaoMix(ulong epoch) => Electra is { } e ? e.GetRandaoMix(epoch) : Gloas!.GetRandaoMix(epoch);

    /// <summary>The beacon committees of <paramref name="epoch"/> on the state's own layout.</summary>
    /// <exception cref="BeaconStateException">No validator is active at <paramref name="epoch"/>, or its seed is outside the randao window.</exception>
    public CommitteeCache BuildCommittees(ulong epoch) => Electra is { } e ? CommitteeCache.Build(e, epoch) : CommitteeCache.Build(Gloas!, epoch);
}
