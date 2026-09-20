// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Cheap copy-on-clone of <see cref="BeaconStateGloas"/>, the post-fork twin of
/// <see cref="BeaconStateClone"/>. The importer freezes a clone under each block root for
/// <see cref="IGloasBlockStateProvider"/> and keeps advancing the original.
/// </summary>
/// <remarks>
/// Same rule as the Fulu clone: an array whose slots the transition writes in place is copied,
/// everything it only ever replaces wholesale is shared. The Gloas-only fields follow the same
/// split - <c>Builders</c>, <c>BuilderPendingPayments</c> and <c>PtcWindow</c> are indexed in
/// place with replaced (never mutated) elements, the payload availability bits are set in place,
/// and the two withdrawal lists and the latest bid are reassigned as a whole.
/// </remarks>
public static class GloasStateClone
{
    /// <summary>
    /// Returns a state that can be mutated by the Gloas state transition without affecting
    /// <paramref name="state"/> (and vice versa).
    /// </summary>
    public static BeaconStateGloas Clone(this BeaconStateGloas state) => new()
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
        BlockRoots = (Hash256[]?)state.BlockRoots?.Clone(),
        StateRoots = (Hash256[]?)state.StateRoots?.Clone(),
        HistoricalRoots = state.HistoricalRoots,
        Eth1Data = state.Eth1Data,
        Eth1DataVotes = state.Eth1DataVotes,
        Eth1DepositIndex = state.Eth1DepositIndex,
        Validators = (Validator[]?)state.Validators?.Clone(),
        Balances = (ulong[]?)state.Balances?.Clone(),
        RandaoMixes = (Hash256[]?)state.RandaoMixes?.Clone(),
        Slashings = (ulong[]?)state.Slashings?.Clone(),
        PreviousEpochParticipation = (byte[]?)state.PreviousEpochParticipation?.Clone(),
        CurrentEpochParticipation = (byte[]?)state.CurrentEpochParticipation?.Clone(),
        JustificationBits = state.JustificationBits is null ? null : new BitArray(state.JustificationBits),
        PreviousJustifiedCheckpoint = state.PreviousJustifiedCheckpoint,
        CurrentJustifiedCheckpoint = state.CurrentJustifiedCheckpoint,
        FinalizedCheckpoint = state.FinalizedCheckpoint,
        InactivityScores = (ulong[]?)state.InactivityScores?.Clone(),
        CurrentSyncCommittee = state.CurrentSyncCommittee,
        NextSyncCommittee = state.NextSyncCommittee,
        LatestBlockHash = state.LatestBlockHash,
        NextWithdrawalIndex = state.NextWithdrawalIndex,
        NextWithdrawalValidatorIndex = state.NextWithdrawalValidatorIndex,
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
        ProposerLookahead = (ulong[]?)state.ProposerLookahead?.Clone(),
        // Builder mutations install a replacement element, like Validator mutations.
        Builders = (Builder[]?)state.Builders?.Clone(),
        NextWithdrawalBuilderIndex = state.NextWithdrawalBuilderIndex,
        // ProcessSlot clears and ApplyParentExecutionPayload sets single bits in place.
        ExecutionPayloadAvailability = state.ExecutionPayloadAvailability is null ? null : new BitArray(state.ExecutionPayloadAvailability),
        // Bid processing, settlement and the epoch rotation write slots in place; a payment is
        // never mutated once written, so the elements can be shared.
        BuilderPendingPayments = (BuilderPendingPayment[]?)state.BuilderPendingPayments?.Clone(),
        BuilderPendingWithdrawals = state.BuilderPendingWithdrawals,
        LatestExecutionPayloadBid = state.LatestExecutionPayloadBid,
        PayloadExpectedWithdrawals = state.PayloadExpectedWithdrawals,
        // ProcessPtcWindow shifts the vector in place; a committee is never mutated once computed.
        PtcWindow = (PayloadTimelinessCommittee[]?)state.PtcWindow?.Clone(),
    };
}
