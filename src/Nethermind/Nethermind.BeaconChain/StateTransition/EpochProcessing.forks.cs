// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
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
    /// Fuses <c>get_flag_index_deltas</c> and <c>get_inactivity_penalty_deltas</c>: validators are
    /// independent, deltas retain spec order (source, target, head, inactivity), and balance changes
    /// leave effective balances, participation and inactivity scores unchanged.
    /// </remarks>
    public static partial void ProcessRewardsAndPenalties(ForkState state, EpochCache cache)
    {
        // No rewards are applied at the end of the genesis epoch (no attestations to reward yet).
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ApplyRewardDeltas(state, cache);
    }

    internal static partial void ApplyRewardDeltas(ForkState state, EpochCache cache)
    {
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
        state.RandaoMixes![(int)((currentEpoch + 1) % Presets.EpochsPerHistoricalVector)] = state.GetRandaoMix(currentEpoch);
    }

    /// <summary>Capella <c>process_historical_summaries_update</c>.</summary>
    public static partial void ProcessHistoricalSummariesUpdate(ForkState state)
    {
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


    public static partial void ProcessEpoch(ForkState state, EpochCache cache)
    {
        ProcessJustificationAndFinalization(state, cache);
        ProcessInactivityUpdates(state);
        ProcessRewardsAndPenalties(state, cache);
        ProcessRegistryUpdates(state, cache);
        ProcessSlashings(state, cache);
        ProcessEth1DataReset(state);
        ProcessPendingDeposits(state, cache);
        ProcessPendingConsolidations(state);
#if GLOAS
        ProcessBuilderPendingPayments(state, cache);
#endif
        ProcessEffectiveBalanceUpdates(state, cache);
        ProcessSlashingsReset(state);
        ProcessRandaoMixesReset(state);
        ProcessHistoricalSummariesUpdate(state);
        ProcessParticipationFlagUpdates(state);
        ProcessSyncCommitteeUpdates(state);
        ProcessProposerLookahead(state);
#if GLOAS
        ProcessPtcWindow(state);
#endif
    }

    public static partial JustificationAndFinalizationState ComputeJustificationAndFinalization(ForkState state, EpochCache cache)
    {
        JustificationAndFinalizationState result = new(state);

        // Skip FFG updates in the first two epochs to preserve the initial 0x00 checkpoint root stubs.
        if (state.GetCurrentEpoch() <= Presets.GenesisEpoch + 1)
            return result;

#if GLOAS
        (ulong previousTargetBalance, ulong currentTargetBalance) = EpochProcessing.GetTargetBalances(
#else
        (ulong previousTargetBalance, ulong currentTargetBalance) = GetTargetBalances(
#endif
            state.Validators!, state.PreviousEpochParticipation ?? [], state.CurrentEpochParticipation ?? [],
            state.GetPreviousEpoch(), state.GetCurrentEpoch());
#if GLOAS
        EpochProcessing.WeighJustificationAndFinalization(
#else
        WeighJustificationAndFinalization(
#endif
            result, state.GetTotalActiveBalance(cache), previousTargetBalance, currentTargetBalance,
            state.Slot, state.BlockRoots!);
        return result;
    }

    public static partial void ProcessInactivityUpdates(ForkState state)
    {
#if !GLOAS
        // Skip the genesis epoch as score updates are based on the previous epoch participation.
#endif
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ulong previousEpoch = state.GetPreviousEpoch();
        bool isInInactivityLeak = IsInInactivityLeak(state);
#if GLOAS
        EpochProcessing.ProcessInactivityUpdates(state.Validators!, state.InactivityScores!, state.PreviousEpochParticipation ?? [], previousEpoch, isInInactivityLeak);
#else
        ProcessInactivityUpdates(state.Validators!, state.InactivityScores!, state.PreviousEpochParticipation ?? [], previousEpoch, isInInactivityLeak);
#endif
    }

    public static partial void ProcessRegistryUpdates(ForkState state, EpochCache cache)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        ulong activationEpoch = BeaconStateAccessors.ComputeActivationExitEpoch(currentEpoch);
#if GLOAS
        ulong finalizedEpoch = state.FinalizedCheckpoint!.Epoch;
#endif

#if !GLOAS
        // Process activation eligibility, ejections, and activations.
#endif
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (validator.IsEligibleForActivationQueue())
            {
                Validator updated = validator.Clone();
                updated.ActivationEligibilityEpoch = currentEpoch + 1;
                validators[i] = updated;
            }
            else if (validator.IsActiveValidator(currentEpoch) && validator.EffectiveBalance <= Presets.EjectionBalance)
            {
                state.InitiateValidatorExit(i, cache);
            }
#if GLOAS
            else if (validator.ActivationEligibilityEpoch <= finalizedEpoch && validator.ActivationEpoch == Presets.FarFutureEpoch)
#else
            else if (state.IsEligibleForActivation(validator))
#endif
            {
                Validator updated = validator.Clone();
                updated.ActivationEpoch = activationEpoch;
                validators[i] = updated;
            }
        }
    }

    public static partial void ProcessSlashings(ForkState state, EpochCache cache)
    {
        ulong epoch = state.GetCurrentEpoch();
        ulong totalBalance = state.GetTotalActiveBalance(cache);
#if GLOAS
        EpochProcessing.ProcessSlashings(state.Validators!, state.Balances!, state.Slashings!, epoch, totalBalance);
#else
        ProcessSlashings(state.Validators!, state.Balances!, state.Slashings!, epoch, totalBalance);
#endif
    }

    public static partial void ProcessPendingDeposits(ForkState state, EpochCache cache)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
#if GLOAS
        ulong availableForProcessing = state.DepositBalanceToConsume + state.GetActivationChurnLimit(cache);
#else
        ulong availableForProcessing = state.DepositBalanceToConsume + state.GetActivationExitChurnLimit(cache);
#endif
        ulong processedAmount = 0;
        int nextDepositIndex = 0;
        List<PendingDeposit> depositsToPostpone = [];
        bool isChurnLimitReached = false;
        ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(state.FinalizedCheckpoint!.Epoch);

        // Spec process_pending_deposits: validator_pubkeys.index is needed only after the queue gates pass.
        Dictionary<BlsPublicKey, int>? pubkeyToIndex = null;

        PendingDeposit[] pendingDeposits = state.PendingDeposits ?? [];
        foreach (PendingDeposit deposit in pendingDeposits)
        {
#if !GLOAS
            // Do not process deposit requests if the Eth1 bridge deposits are not yet applied.
            if (deposit.Slot > Presets.GenesisSlot && state.Eth1DepositIndex < state.DepositRequestsStartIndex)
                break;
            // Check if the deposit has been finalized, otherwise stop processing.
#endif
            if (deposit.Slot > finalizedSlot)
                break;
#if !GLOAS
            // Check if the number of processed deposits has not reached the limit, otherwise stop processing.
#endif
            if (nextDepositIndex >= Presets.MaxPendingDepositsPerEpoch)
                break;

            bool isValidatorExited = false;
            bool isValidatorWithdrawn = false;
            pubkeyToIndex ??= IndexPubkeys(state.Validators!);
            if (pubkeyToIndex.TryGetValue(deposit.Pubkey, out int validatorIndex))
            {
                Validator validator = state.Validators![validatorIndex];
                isValidatorExited = validator.ExitEpoch < Presets.FarFutureEpoch;
                isValidatorWithdrawn = validator.WithdrawableEpoch < nextEpoch;
            }

            if (isValidatorWithdrawn)
            {
#if !GLOAS
                // Deposited balance will never become active. Increase balance but do not consume churn.
#endif
                ApplyPendingDeposit(state, deposit, pubkeyToIndex);
            }
            else if (isValidatorExited)
            {
#if !GLOAS
                // Validator is exiting, postpone the deposit until after the withdrawable epoch.
#endif
                depositsToPostpone.Add(deposit);
            }
            else
            {
#if !GLOAS
                // Check if the deposit fits in the churn, otherwise do no more deposit processing in this epoch.
#endif
                isChurnLimitReached = processedAmount + deposit.Amount > availableForProcessing;
                if (isChurnLimitReached)
                    break;
#if !GLOAS
                // Consume churn and apply the deposit.
#endif
                processedAmount += deposit.Amount;
                ApplyPendingDeposit(state, deposit, pubkeyToIndex);
            }

#if !GLOAS
            // Regardless of how the deposit was handled, we move on in the queue.
#endif
            nextDepositIndex++;
        }

        state.PendingDeposits = [.. pendingDeposits[nextDepositIndex..], .. depositsToPostpone];
#if !GLOAS

        // Accumulate churn only if the churn limit has been hit.
#endif
        state.DepositBalanceToConsume = isChurnLimitReached ? availableForProcessing - processedAmount : 0;
    }

    public static partial void ProcessPendingConsolidations(ForkState state)
    {
        PendingConsolidation[] pendingConsolidations = state.PendingConsolidations ?? [];
#if GLOAS
        int nextPendingConsolidation = EpochProcessing.ProcessPendingConsolidations(state.Validators!, state.Balances!, pendingConsolidations, state.GetCurrentEpoch() + 1);
#else
        int nextPendingConsolidation = ProcessPendingConsolidations(state.Validators!, state.Balances!, pendingConsolidations, state.GetCurrentEpoch() + 1);
#endif
        state.PendingConsolidations = pendingConsolidations[nextPendingConsolidation..];
    }

    public static partial void ProcessEffectiveBalanceUpdates(ForkState state, EpochCache cache)
    {
#if GLOAS
        EpochProcessing.ProcessEffectiveBalanceUpdates(state.Validators!, state.Balances!);
#else
        ProcessEffectiveBalanceUpdates(state.Validators!, state.Balances!);
#endif
        cache.InvalidateTotalActiveBalance();
    }

    private static partial SyncCommittee GetNextSyncCommittee(ForkState state)
    {
#if GLOAS
        ulong epoch = state.GetCurrentEpoch() + 1;
        Hash256 seed = state.GetSeed(epoch, DomainType.SyncCommittee);
        int[] indices = GloasForkTransition.ComputeBalanceWeightedSelection(
            state.Validators!, state.GetActiveValidatorIndices(epoch), seed.Bytes, Presets.SyncCommitteeSize, shuffleIndices: true);

#else
        int[] indices = GetNextSyncCommitteeIndices(state);
#endif
        BlsPublicKey[] pubkeys = new BlsPublicKey[indices.Length];
        BlsSigner.AggregatedPublicKey aggregate = new();
        Bls.P1Affine publicKey = new(stackalloc long[Bls.P1Affine.Sz]);
        for (int i = 0; i < indices.Length; i++)
        {
            pubkeys[i] = state.Validators![indices[i]].Pubkey;
            // Altair eth_aggregate_pubkeys asserts KeyValidate on every member, rejecting infinity and off-subgroup keys
            if (!BlsSignatureSet.TryKeyValidate(pubkeys[i].Bytes, publicKey))
                throw new BeaconStateException($"Invalid sync committee pubkey for validator {indices[i]}");
            aggregate.Aggregate(publicKey);
        }
        return new SyncCommittee
        {
            Pubkeys = pubkeys,
            AggregatePubkey = new BlsPublicKey(aggregate.PublicKey.Compress()),
        };
    }

    public static partial void ProcessProposerLookahead(ForkState state)
    {
        ulong[] lookahead = state.ProposerLookahead!;
        int slotsPerEpoch = (int)Presets.SlotsPerEpoch;
        Array.Copy(lookahead, slotsPerEpoch, lookahead, 0, lookahead.Length - slotsPerEpoch);
#if GLOAS
        ulong[] lastEpochProposers = GetBeaconProposerIndices(state, state.GetCurrentEpoch() + Presets.MinSeedLookahead + 1);
#else
        ulong[] lastEpochProposers = state.ComputeProposerIndices(state.GetCurrentEpoch() + Presets.MinSeedLookahead + 1);
#endif
        lastEpochProposers.CopyTo(lookahead, lookahead.Length - slotsPerEpoch);
    }
}
