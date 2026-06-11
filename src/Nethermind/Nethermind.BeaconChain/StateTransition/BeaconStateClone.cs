// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>Cheap copy-on-clone of <see cref="BeaconStateFulu"/> for forking a state lineage.</summary>
/// <remarks>
/// Per field the copy-vs-share decision follows how the state transition writes it: arrays whose
/// slots are written in place (balances, slashings, participation, inactivity scores, the
/// validator/root/mix arrays, the proposer lookahead) are copied, while everything the transition
/// only ever replaces wholesale (checkpoints, sync committees, the payload header, the pending
/// queues and other container lists) is shared. Shared objects are immutable by convention —
/// e.g. <see cref="Validator"/> mutations install a <see cref="BeaconStateMutators.Clone"/>d copy
/// — so the clone and the source can both be advanced independently.
/// </remarks>
public static class BeaconStateClone
{
    /// <summary>
    /// Returns a state that can be mutated by the state transition without affecting
    /// <paramref name="state"/> (and vice versa).
    /// </summary>
    public static BeaconStateFulu Clone(this BeaconStateFulu state) => new()
    {
        GenesisTime = state.GenesisTime,
        GenesisValidatorsRoot = state.GenesisValidatorsRoot,
        Slot = state.Slot,
        Fork = state.Fork,
        // ProcessSlot writes LatestBlockHeader.StateRoot in place, so the header object itself
        // must not be shared.
        LatestBlockHeader = state.LatestBlockHeader is null ? null : new BeaconBlockHeader
        {
            Slot = state.LatestBlockHeader.Slot,
            ProposerIndex = state.LatestBlockHeader.ProposerIndex,
            ParentRoot = state.LatestBlockHeader.ParentRoot,
            StateRoot = state.LatestBlockHeader.StateRoot,
            BodyRoot = state.LatestBlockHeader.BodyRoot,
        },
        // ProcessSlot assigns elements of BlockRoots/StateRoots in place; share the Hash256s.
        BlockRoots = (Hash256[]?)state.BlockRoots?.Clone(),
        StateRoots = (Hash256[]?)state.StateRoots?.Clone(),
        // Frozen since Capella; nothing in the transition writes it.
        HistoricalRoots = state.HistoricalRoots,
        // ProcessEth1Data replaces the Eth1Data instance and reassigns the votes array wholesale.
        Eth1Data = state.Eth1Data,
        Eth1DataVotes = state.Eth1DataVotes,
        Eth1DepositIndex = state.Eth1DepositIndex,
        // Mutators replace Validator elements in place; the elements are immutable by convention.
        Validators = (Validator[]?)state.Validators?.Clone(),
        Balances = (ulong[]?)state.Balances?.Clone(),
        // ProcessRandao assigns a mix element in place.
        RandaoMixes = (Hash256[]?)state.RandaoMixes?.Clone(),
        // SlashValidator and the slashings reset write slots in place.
        Slashings = (ulong[]?)state.Slashings?.Clone(),
        // ProcessAttestation ORs participation flags into the bytes in place.
        PreviousEpochParticipation = (byte[]?)state.PreviousEpochParticipation?.Clone(),
        CurrentEpochParticipation = (byte[]?)state.CurrentEpochParticipation?.Clone(),
        // Epoch processing replaces the BitArray rather than mutating it, but copying 4 bits is
        // cheaper than relying on that.
        JustificationBits = state.JustificationBits is null ? null : new BitArray(state.JustificationBits),
        PreviousJustifiedCheckpoint = state.PreviousJustifiedCheckpoint,
        CurrentJustifiedCheckpoint = state.CurrentJustifiedCheckpoint,
        FinalizedCheckpoint = state.FinalizedCheckpoint,
        // ProcessInactivityUpdates writes scores in place.
        InactivityScores = (ulong[]?)state.InactivityScores?.Clone(),
        // Replaced wholesale at sync-committee period boundaries.
        CurrentSyncCommittee = state.CurrentSyncCommittee,
        NextSyncCommittee = state.NextSyncCommittee,
        // Replaced wholesale by ProcessExecutionPayload.
        LatestExecutionPayloadHeader = state.LatestExecutionPayloadHeader,
        NextWithdrawalIndex = state.NextWithdrawalIndex,
        NextWithdrawalValidatorIndex = state.NextWithdrawalValidatorIndex,
        // The pending queues and historical summaries are only ever reassigned ([.. xs, y] or a
        // slice); their elements are never mutated.
        HistoricalSummaries = state.HistoricalSummaries,
        DepositRequestsStartIndex = state.DepositRequestsStartIndex,
        DepositBalanceToConsume = state.DepositBalanceToConsume,
        ExitBalanceToConsume = state.ExitBalanceToConsume,
        EarliestExitEpoch = state.EarliestExitEpoch,
        ConsolidationBalanceToConsume = state.ConsolidationBalanceToConsume,
        EarliestConsolidationEpoch = state.EarliestConsolidationEpoch,
        PendingDeposits = state.PendingDeposits,
        PendingPartialWithdrawals = state.PendingPartialWithdrawals,
        PendingConsolidations = state.PendingConsolidations,
        // ProcessProposerLookahead shifts the vector in place.
        ProposerLookahead = (ulong[]?)state.ProposerLookahead?.Clone(),
    };
}
