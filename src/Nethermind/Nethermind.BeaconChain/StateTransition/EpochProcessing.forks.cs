// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.StateTransition;

public static partial class EpochProcessing
{

    /// <summary>Altair <c>process_justification_and_finalization</c>.</summary>
    public static partial void ProcessJustificationAndFinalization(ForkState state, EpochCache cache) =>
        ComputeJustificationAndFinalization(state, cache).ApplyTo(state);

    /// <summary>Altair <c>process_rewards_and_penalties</c>: per-flag participation deltas plus inactivity penalties.</summary>
    /// <remarks>
    /// Fuses the spec's per-flag <c>get_flag_index_deltas</c> passes and
    /// <c>get_inactivity_penalty_deltas</c> into one loop. This is equivalent because validators
    /// are independent and per validator the deltas are applied in the spec order
    /// (source, target, head, inactivity), and because the inputs (effective balances,
    /// participation, inactivity scores) are not modified by applying balance deltas.
    /// </remarks>
    public static partial void ProcessRewardsAndPenalties(ForkState state, EpochCache cache)
    {
        // No rewards are applied at the end of the genesis epoch (no attestations to reward yet).
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ulong previousEpoch = state.GetPreviousEpoch();
        ulong totalActiveBalance = state.GetTotalActiveBalance(cache);
        ulong baseRewardPerIncrement = state.GetBaseRewardPerIncrement(cache);
        ulong activeIncrements = totalActiveBalance / Presets.EffectiveBalanceIncrement;
        bool isInInactivityLeak = IsInInactivityLeak(state);

        Validator[] validators = state.Validators!;
        byte[] previousParticipation = state.PreviousEpochParticipation ?? [];

        // Total balance of the unslashed participants of each flag (spec
        // get_total_balance(state, get_unslashed_participating_indices(...))), in increments.
        Span<ulong> unslashedParticipatingIncrements = stackalloc ulong[Presets.ParticipationFlagWeights.Length];
        for (int flagIndex = 0; flagIndex < unslashedParticipatingIncrements.Length; flagIndex++)
        {
            ulong participatingBalance = 0;
            for (int i = 0; i < validators.Length; i++)
            {
                if (IsUnslashedParticipant(validators[i], previousParticipation[i], flagIndex, previousEpoch))
                    participatingBalance += validators[i].EffectiveBalance;
            }
            unslashedParticipatingIncrements[flagIndex] =
                Math.Max(Presets.EffectiveBalanceIncrement, participatingBalance) / Presets.EffectiveBalanceIncrement;
        }

        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (!IsEligibleValidator(validator, previousEpoch))
                continue;

            ulong baseReward = validator.EffectiveBalance / Presets.EffectiveBalanceIncrement * baseRewardPerIncrement;
            for (int flagIndex = 0; flagIndex < Presets.ParticipationFlagWeights.Length; flagIndex++)
            {
                ulong weight = Presets.ParticipationFlagWeights[flagIndex];
                if (IsUnslashedParticipant(validator, previousParticipation[i], flagIndex, previousEpoch))
                {
                    if (!isInInactivityLeak)
                        state.IncreaseBalance(i, baseReward * weight * unslashedParticipatingIncrements[flagIndex] / (activeIncrements * Presets.WeightDenominator));
                }
                else if (flagIndex != Presets.TimelyHeadFlagIndex)
                {
                    state.DecreaseBalance(i, baseReward * weight / Presets.WeightDenominator);
                }
            }

            // Bellatrix get_inactivity_penalty_deltas.
            if (!IsUnslashedParticipant(validator, previousParticipation[i], Presets.TimelyTargetFlagIndex, previousEpoch))
                state.DecreaseBalance(i, validator.EffectiveBalance * state.InactivityScores![i] / (Presets.InactivityScoreBias * Presets.InactivityPenaltyQuotientBellatrix));
        }
    }

    /// <summary>Phase0 <c>process_eth1_data_reset</c>.</summary>
    public static partial void ProcessEth1DataReset(ForkState state)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        // Reset eth1 data votes.
        if (nextEpoch % Presets.EpochsPerEth1VotingPeriod == 0)
            state.Eth1DataVotes = [];
    }

    private static partial Dictionary<BlsPublicKey, int> IndexPubkeys(Validator[] validators)
    {
        Dictionary<BlsPublicKey, int> pubkeyToIndex = [];
        for (int i = 0; i < validators.Length; i++)
        {
            pubkeyToIndex.TryAdd(validators[i].Pubkey, i);
        }

        return pubkeyToIndex;
    }

    /// <summary>Electra <c>apply_pending_deposit</c>: top up a known validator or, after proof of possession, add a new one.</summary>
    private static partial void ApplyPendingDeposit(ForkState state, PendingDeposit deposit, Dictionary<BlsPublicKey, int> pubkeyToIndex)
    {
        if (pubkeyToIndex.TryGetValue(deposit.Pubkey, out int index))
        {
            state.IncreaseBalance(index, deposit.Amount);
        }
        // Verify the deposit signature (proof of possession) which is not checked by the deposit contract.
        else if (DepositSignatureVerifier.IsValid(state.GenesisValidatorsRoot!, deposit.Pubkey, deposit.WithdrawalCredentials!, deposit.Amount, deposit.Signature))
        {
            pubkeyToIndex.TryAdd(deposit.Pubkey, state.Validators!.Length);
            state.AddValidatorToRegistry(deposit.Pubkey, deposit.WithdrawalCredentials!, deposit.Amount);
        }
    }

    /// <summary>Phase0 <c>process_slashings_reset</c>.</summary>
    public static partial void ProcessSlashingsReset(ForkState state) =>
        // Reset the slashings accumulator slot that the next epoch will reuse.
        state.Slashings![(int)((state.GetCurrentEpoch() + 1) % Presets.EpochsPerSlashingsVector)] = 0;

    /// <summary>Phase0 <c>process_randao_mixes_reset</c>.</summary>
    public static partial void ProcessRandaoMixesReset(ForkState state)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        // Seed the next epoch's mix with the current one.
        state.RandaoMixes![(int)((currentEpoch + 1) % Presets.EpochsPerHistoricalVector)] = state.GetRandaoMix(currentEpoch);
    }

    /// <summary>Capella <c>process_historical_summaries_update</c>.</summary>
    public static partial void ProcessHistoricalSummariesUpdate(ForkState state)
    {
        // Set the historical block root accumulator.
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        if (nextEpoch % (Presets.SlotsPerHistoricalRoot / Presets.SlotsPerEpoch) == 0)
        {
            HistoricalSummary historicalSummary = new()
            {
                BlockSummaryRoot = HashTreeRootOfRoots(state.BlockRoots!),
                StateSummaryRoot = HashTreeRootOfRoots(state.StateRoots!),
            };
            state.HistoricalSummaries = [.. state.HistoricalSummaries ?? [], historicalSummary];
        }
    }

    /// <summary>Altair <c>process_participation_flag_updates</c>: rotate current participation into previous.</summary>
    public static partial void ProcessParticipationFlagUpdates(ForkState state)
    {
        state.PreviousEpochParticipation = state.CurrentEpochParticipation;
        state.CurrentEpochParticipation = new byte[state.Validators!.Length];
    }

    /// <summary>Altair <c>process_sync_committee_updates</c>: rotate committees at sync-committee period boundaries.</summary>
    public static partial void ProcessSyncCommitteeUpdates(ForkState state)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        if (nextEpoch % Presets.EpochsPerSyncCommitteePeriod == 0)
        {
            state.CurrentSyncCommittee = state.NextSyncCommittee;
            state.NextSyncCommittee = GetNextSyncCommittee(state);
        }
    }

    /// <summary>Phase0 <c>get_eligible_validator_indices</c> membership: validators that earn rewards/penalties for the previous epoch.</summary>
    private static partial bool IsEligibleValidator(Validator validator, ulong previousEpoch) =>
        validator.IsActiveValidator(previousEpoch) || (validator.Slashed && previousEpoch + 1 < validator.WithdrawableEpoch);

    /// <summary>Altair <c>get_unslashed_participating_indices</c> membership for one validator and flag.</summary>
    private static partial bool IsUnslashedParticipant(Validator validator, byte participation, int flagIndex, ulong epoch) =>
        validator.IsActiveValidator(epoch) && !validator.Slashed && BeaconStateAccessors.HasParticipationFlag(participation, flagIndex);

    /// <summary>Altair <c>is_in_inactivity_leak</c>: finality is more than <c>MIN_EPOCHS_TO_INACTIVITY_PENALTY</c> epochs behind.</summary>
    private static partial bool IsInInactivityLeak(ForkState state) =>
        state.GetPreviousEpoch() - state.FinalizedCheckpoint!.Epoch > Presets.MinEpochsToInactivityPenalty;

    /// <summary>Computes <c>hash_tree_root</c> of a <c>Vector[Root, SLOTS_PER_HISTORICAL_ROOT]</c>.</summary>
    private static partial Hash256 HashTreeRootOfRoots(Hash256[] roots)
    {
        byte[] chunks = new byte[roots.Length * Hash256.Size];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i].Bytes.CopyTo(chunks.AsSpan(i * Hash256.Size));
        }
        Merkle.Merkleize(out UInt256 root, chunks);
        return new Hash256(root.ToLittleEndian());
    }
}
