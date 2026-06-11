// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// The Electra/Fulu <c>process_epoch</c> sub-transitions, ported spec-shaped from consensus-specs
/// (cross-checked against Lighthouse <c>per_epoch_processing</c>).
/// </summary>
/// <remarks>
/// Each sub-transition is public so the per-handler consensus-spec <c>epoch_processing</c> tests
/// can exercise it in isolation. Within one <see cref="ProcessEpoch"/> the total-active-balance
/// memo in <see cref="EpochCache"/> stays valid until
/// <see cref="ProcessEffectiveBalanceUpdates"/>, which invalidates it: earlier steps only change
/// raw balances or future activation/exit epochs, never the current-epoch active set or effective
/// balances.
/// </remarks>
public static class EpochProcessing
{
    /// <summary>Electra <c>MAX_RANDOM_VALUE</c>: random values are 16-bit from this fork on.</summary>
    private const ulong MaxRandomValue = (1 << 16) - 1;

    public static void ProcessEpoch(BeaconStateFulu state, EpochCache cache)
    {
        ProcessJustificationAndFinalization(state, cache);
        ProcessInactivityUpdates(state);
        ProcessRewardsAndPenalties(state, cache);
        ProcessRegistryUpdates(state, cache);
        ProcessSlashings(state, cache);
        ProcessEth1DataReset(state);
        ProcessPendingDeposits(state, cache);
        ProcessPendingConsolidations(state);
        ProcessEffectiveBalanceUpdates(state, cache);
        ProcessSlashingsReset(state);
        ProcessRandaoMixesReset(state);
        ProcessHistoricalSummariesUpdate(state);
        ProcessParticipationFlagUpdates(state);
        ProcessSyncCommitteeUpdates(state);
        ProcessProposerLookahead(state);
    }

    /// <summary>Altair <c>process_justification_and_finalization</c>.</summary>
    public static void ProcessJustificationAndFinalization(BeaconStateFulu state, EpochCache cache)
    {
        // Initial FFG checkpoint values have a `0x00` stub for `root`.
        // Skip FFG updates in the first two epochs to avoid corner cases that might result in
        // modifying this stub.
        if (state.GetCurrentEpoch() <= Presets.GenesisEpoch + 1)
            return;

        ulong previousEpoch = state.GetPreviousEpoch();
        ulong currentEpoch = state.GetCurrentEpoch();
        ulong previousTargetBalance = 0;
        ulong currentTargetBalance = 0;
        Validator[] validators = state.Validators!;
        byte[] previousParticipation = state.PreviousEpochParticipation ?? [];
        byte[] currentParticipation = state.CurrentEpochParticipation ?? [];
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (validator.Slashed)
                continue;
            if (validator.IsActiveValidator(previousEpoch) && HasFlag(previousParticipation[i], Presets.TimelyTargetFlagIndex))
                previousTargetBalance += validator.EffectiveBalance;
            if (validator.IsActiveValidator(currentEpoch) && HasFlag(currentParticipation[i], Presets.TimelyTargetFlagIndex))
                currentTargetBalance += validator.EffectiveBalance;
        }

        WeighJustificationAndFinalization(
            state,
            state.GetTotalActiveBalance(cache),
            Math.Max(Presets.EffectiveBalanceIncrement, previousTargetBalance),
            Math.Max(Presets.EffectiveBalanceIncrement, currentTargetBalance));
    }

    /// <summary>Phase0 <c>weigh_justification_and_finalization</c>: justification-bit shift, 2/3 supermajority justification, and the four finalization rules.</summary>
    private static void WeighJustificationAndFinalization(BeaconStateFulu state, ulong totalActiveBalance, ulong previousTargetBalance, ulong currentTargetBalance)
    {
        ulong previousEpoch = state.GetPreviousEpoch();
        ulong currentEpoch = state.GetCurrentEpoch();
        Checkpoint oldPreviousJustifiedCheckpoint = state.PreviousJustifiedCheckpoint!;
        Checkpoint oldCurrentJustifiedCheckpoint = state.CurrentJustifiedCheckpoint!;

        // Process justifications.
        state.PreviousJustifiedCheckpoint = state.CurrentJustifiedCheckpoint;
        BitArray bits = state.JustificationBits!;
        for (int i = bits.Length - 1; i >= 1; i--)
        {
            bits[i] = bits[i - 1];
        }
        bits[0] = false;
        if (previousTargetBalance * 3 >= totalActiveBalance * 2)
        {
            state.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = previousEpoch, Root = state.GetBlockRoot(previousEpoch) };
            bits[1] = true;
        }
        if (currentTargetBalance * 3 >= totalActiveBalance * 2)
        {
            state.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = currentEpoch, Root = state.GetBlockRoot(currentEpoch) };
            bits[0] = true;
        }

        // Process finalizations.
        // The 2nd/3rd/4th most recent epochs are justified, the 2nd using the 4th as source.
        if (bits[1] && bits[2] && bits[3] && oldPreviousJustifiedCheckpoint.Epoch + 3 == currentEpoch)
            state.FinalizedCheckpoint = oldPreviousJustifiedCheckpoint;
        // The 2nd/3rd most recent epochs are justified, the 2nd using the 3rd as source.
        if (bits[1] && bits[2] && oldPreviousJustifiedCheckpoint.Epoch + 2 == currentEpoch)
            state.FinalizedCheckpoint = oldPreviousJustifiedCheckpoint;
        // The 1st/2nd/3rd most recent epochs are justified, the 1st using the 3rd as source.
        if (bits[0] && bits[1] && bits[2] && oldCurrentJustifiedCheckpoint.Epoch + 2 == currentEpoch)
            state.FinalizedCheckpoint = oldCurrentJustifiedCheckpoint;
        // The 1st/2nd most recent epochs are justified, the 1st using the 2nd as source.
        if (bits[0] && bits[1] && oldCurrentJustifiedCheckpoint.Epoch + 1 == currentEpoch)
            state.FinalizedCheckpoint = oldCurrentJustifiedCheckpoint;
    }

    /// <summary>Altair <c>process_inactivity_updates</c> (EIP-7045 inactivity scores).</summary>
    public static void ProcessInactivityUpdates(BeaconStateFulu state)
    {
        // Skip the genesis epoch as score updates are based on the previous epoch participation.
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ulong previousEpoch = state.GetPreviousEpoch();
        bool isInInactivityLeak = IsInInactivityLeak(state);
        Validator[] validators = state.Validators!;
        ulong[] inactivityScores = state.InactivityScores!;
        byte[] previousParticipation = state.PreviousEpochParticipation ?? [];
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (!IsEligibleValidator(validator, previousEpoch))
                continue;

            // Increase the inactivity score of inactive validators.
            if (IsUnslashedParticipant(validator, previousParticipation[i], Presets.TimelyTargetFlagIndex, previousEpoch))
                inactivityScores[i] -= Math.Min(1, inactivityScores[i]);
            else
                inactivityScores[i] += Presets.InactivityScoreBias;
            // Decrease the inactivity score of all eligible validators during a leak-free epoch.
            if (!isInInactivityLeak)
                inactivityScores[i] -= Math.Min(Presets.InactivityScoreRecoveryRate, inactivityScores[i]);
        }
    }

    /// <summary>Altair <c>process_rewards_and_penalties</c>: per-flag participation deltas plus inactivity penalties.</summary>
    /// <remarks>
    /// Fuses the spec's per-flag <c>get_flag_index_deltas</c> passes and
    /// <c>get_inactivity_penalty_deltas</c> into one loop. This is equivalent because validators
    /// are independent and per validator the deltas are applied in the spec order
    /// (source, target, head, inactivity), and because the inputs (effective balances,
    /// participation, inactivity scores) are not modified by applying balance deltas.
    /// </remarks>
    public static void ProcessRewardsAndPenalties(BeaconStateFulu state, EpochCache cache)
    {
        // No rewards are applied at the end of the genesis epoch (no attestations to reward yet).
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ulong previousEpoch = state.GetPreviousEpoch();
        ulong totalActiveBalance = state.GetTotalActiveBalance(cache);
        ulong baseRewardPerIncrement = Presets.EffectiveBalanceIncrement * Presets.BaseRewardFactor / IntegerSquareRoot(totalActiveBalance);
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

    /// <summary>Electra <c>process_registry_updates</c> (EIP-7251): eligibility sweep, ejections, and finality-gated activations.</summary>
    public static void ProcessRegistryUpdates(BeaconStateFulu state, EpochCache cache)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        ulong activationEpoch = BeaconStateAccessors.ComputeActivationExitEpoch(currentEpoch);

        // Process activation eligibility, ejections, and activations.
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
                InitiateValidatorExitChecked(state, i, cache);
            }
            else if (state.IsEligibleForActivation(validator))
            {
                Validator updated = validator.Clone();
                updated.ActivationEpoch = activationEpoch;
                validators[i] = updated;
            }
        }
    }

    /// <summary>Electra <c>process_slashings</c>: proportional correlated slashing penalties, quantized per effective-balance increment (EIP-7251).</summary>
    public static void ProcessSlashings(BeaconStateFulu state, EpochCache cache)
    {
        ulong epoch = state.GetCurrentEpoch();
        ulong totalBalance = state.GetTotalActiveBalance(cache);
        ulong totalSlashings = 0;
        foreach (ulong slashing in state.Slashings!)
        {
            totalSlashings += slashing;
        }
        ulong adjustedTotalSlashingBalance = Math.Min(totalSlashings * Presets.ProportionalSlashingMultiplierBellatrix, totalBalance);
        ulong penaltyPerEffectiveBalanceIncrement = adjustedTotalSlashingBalance / (totalBalance / Presets.EffectiveBalanceIncrement);
        ulong targetWithdrawableEpoch = epoch + Presets.EpochsPerSlashingsVector / 2;

        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (validator.Slashed && targetWithdrawableEpoch == validator.WithdrawableEpoch)
                state.DecreaseBalance(i, penaltyPerEffectiveBalanceIncrement * (validator.EffectiveBalance / Presets.EffectiveBalanceIncrement));
        }
    }

    /// <summary>Phase0 <c>process_eth1_data_reset</c>.</summary>
    public static void ProcessEth1DataReset(BeaconStateFulu state)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        // Reset eth1 data votes.
        if (nextEpoch % Presets.EpochsPerEth1VotingPeriod == 0)
            state.Eth1DataVotes = [];
    }

    /// <summary>Electra <c>process_pending_deposits</c> (EIP-7251): churn-limited sweep of the pending deposit queue.</summary>
    public static void ProcessPendingDeposits(BeaconStateFulu state, EpochCache cache)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        ulong availableForProcessing = state.DepositBalanceToConsume + state.GetActivationExitChurnLimit(cache);
        ulong processedAmount = 0;
        int nextDepositIndex = 0;
        List<PendingDeposit> depositsToPostpone = [];
        bool isChurnLimitReached = false;
        ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(state.FinalizedCheckpoint!.Epoch);

        // Spec `validator_pubkeys.index(deposit.pubkey)` lookups, kept in sync as the registry grows.
        Dictionary<BlsPublicKey, int> pubkeyToIndex = [];
        for (int i = 0; i < state.Validators!.Length; i++)
        {
            pubkeyToIndex.TryAdd(state.Validators[i].Pubkey, i);
        }

        PendingDeposit[] pendingDeposits = state.PendingDeposits ?? [];
        foreach (PendingDeposit deposit in pendingDeposits)
        {
            // Do not process deposit requests if the Eth1 bridge deposits are not yet applied.
            if (deposit.Slot > Presets.GenesisSlot && state.Eth1DepositIndex < state.DepositRequestsStartIndex)
                break;
            // Check if the deposit has been finalized, otherwise stop processing.
            if (deposit.Slot > finalizedSlot)
                break;
            // Check if the number of processed deposits has not reached the limit, otherwise stop processing.
            if (nextDepositIndex >= Presets.MaxPendingDepositsPerEpoch)
                break;

            bool isValidatorExited = false;
            bool isValidatorWithdrawn = false;
            if (pubkeyToIndex.TryGetValue(deposit.Pubkey, out int validatorIndex))
            {
                Validator validator = state.Validators[validatorIndex];
                isValidatorExited = validator.ExitEpoch < Presets.FarFutureEpoch;
                isValidatorWithdrawn = validator.WithdrawableEpoch < nextEpoch;
            }

            if (isValidatorWithdrawn)
            {
                // Deposited balance will never become active. Increase balance but do not consume churn.
                ApplyPendingDeposit(state, deposit, pubkeyToIndex);
            }
            else if (isValidatorExited)
            {
                // Validator is exiting, postpone the deposit until after the withdrawable epoch.
                depositsToPostpone.Add(deposit);
            }
            else
            {
                // Check if the deposit fits in the churn, otherwise do no more deposit processing in this epoch.
                isChurnLimitReached = processedAmount + deposit.Amount > availableForProcessing;
                if (isChurnLimitReached)
                    break;
                // Consume churn and apply the deposit.
                processedAmount += deposit.Amount;
                ApplyPendingDeposit(state, deposit, pubkeyToIndex);
            }

            // Regardless of how the deposit was handled, we move on in the queue.
            nextDepositIndex++;
        }

        state.PendingDeposits = [.. pendingDeposits[nextDepositIndex..], .. depositsToPostpone];

        // Accumulate churn only if the churn limit has been hit.
        state.DepositBalanceToConsume = isChurnLimitReached ? availableForProcessing - processedAmount : 0;
    }

    /// <summary>Electra <c>apply_pending_deposit</c>: top up a known validator or, after proof of possession, add a new one.</summary>
    private static void ApplyPendingDeposit(BeaconStateFulu state, PendingDeposit deposit, Dictionary<BlsPublicKey, int> pubkeyToIndex)
    {
        if (pubkeyToIndex.TryGetValue(deposit.Pubkey, out int index))
        {
            state.IncreaseBalance(index, deposit.Amount);
        }
        // Verify the deposit signature (proof of possession) which is not checked by the deposit contract.
        else if (DepositSignatureVerifier.IsValid(deposit.Pubkey, deposit.WithdrawalCredentials!, deposit.Amount, deposit.Signature))
        {
            AddValidatorToRegistry(state, deposit.Pubkey, deposit.WithdrawalCredentials!, deposit.Amount, pubkeyToIndex);
        }
    }

    /// <summary>Electra <c>add_validator_to_registry</c> + <c>get_validator_from_deposit</c>.</summary>
    private static void AddValidatorToRegistry(BeaconStateFulu state, BlsPublicKey pubkey, Hash256 withdrawalCredentials, ulong amount, Dictionary<BlsPublicKey, int> pubkeyToIndex)
    {
        Validator validator = new()
        {
            Pubkey = pubkey,
            WithdrawalCredentials = withdrawalCredentials,
            EffectiveBalance = 0,
            Slashed = false,
            ActivationEligibilityEpoch = Presets.FarFutureEpoch,
            ActivationEpoch = Presets.FarFutureEpoch,
            ExitEpoch = Presets.FarFutureEpoch,
            WithdrawableEpoch = Presets.FarFutureEpoch,
        };
        validator.EffectiveBalance = Math.Min(amount - amount % Presets.EffectiveBalanceIncrement, validator.GetMaxEffectiveBalance());

        pubkeyToIndex.TryAdd(pubkey, state.Validators!.Length);
        state.Validators = [.. state.Validators, validator];
        state.Balances = [.. state.Balances!, amount];
        state.PreviousEpochParticipation = [.. state.PreviousEpochParticipation ?? [], (byte)0];
        state.CurrentEpochParticipation = [.. state.CurrentEpochParticipation ?? [], (byte)0];
        state.InactivityScores = [.. state.InactivityScores ?? [], 0UL];
    }

    /// <summary>Electra <c>process_pending_consolidations</c> (EIP-7251): sweep consolidations whose source is withdrawable.</summary>
    public static void ProcessPendingConsolidations(BeaconStateFulu state)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        int nextPendingConsolidation = 0;
        PendingConsolidation[] pendingConsolidations = state.PendingConsolidations ?? [];
        foreach (PendingConsolidation pendingConsolidation in pendingConsolidations)
        {
            Validator sourceValidator = state.Validators![(int)pendingConsolidation.SourceIndex];
            if (sourceValidator.Slashed)
            {
                nextPendingConsolidation++;
                continue;
            }
            if (sourceValidator.WithdrawableEpoch > nextEpoch)
                break;

            // Move the active balance to the target; excess balance is withdrawable.
            ulong sourceEffectiveBalance = Math.Min(state.Balances![(int)pendingConsolidation.SourceIndex], sourceValidator.EffectiveBalance);
            state.DecreaseBalance((int)pendingConsolidation.SourceIndex, sourceEffectiveBalance);
            state.IncreaseBalance((int)pendingConsolidation.TargetIndex, sourceEffectiveBalance);
            nextPendingConsolidation++;
        }

        state.PendingConsolidations = pendingConsolidations[nextPendingConsolidation..];
    }

    /// <summary>Electra <c>process_effective_balance_updates</c>: hysteresis against the EIP-7251 per-validator max effective balance.</summary>
    public static void ProcessEffectiveBalanceUpdates(BeaconStateFulu state, EpochCache cache)
    {
        const ulong hysteresisIncrement = Presets.EffectiveBalanceIncrement / Presets.HysteresisQuotient;
        const ulong downwardThreshold = hysteresisIncrement * Presets.HysteresisDownwardMultiplier;
        const ulong upwardThreshold = hysteresisIncrement * Presets.HysteresisUpwardMultiplier;

        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            ulong balance = state.Balances![i];
            if (balance + downwardThreshold < validator.EffectiveBalance || validator.EffectiveBalance + upwardThreshold < balance)
            {
                Validator updated = validator.Clone();
                updated.EffectiveBalance = Math.Min(balance - balance % Presets.EffectiveBalanceIncrement, validator.GetMaxEffectiveBalance());
                validators[i] = updated;
            }
        }

        cache.InvalidateTotalActiveBalance();
    }

    /// <summary>Phase0 <c>process_slashings_reset</c>.</summary>
    public static void ProcessSlashingsReset(BeaconStateFulu state) =>
        // Reset the slashings accumulator slot that the next epoch will reuse.
        state.Slashings![(int)((state.GetCurrentEpoch() + 1) % Presets.EpochsPerSlashingsVector)] = 0;

    /// <summary>Phase0 <c>process_randao_mixes_reset</c>.</summary>
    public static void ProcessRandaoMixesReset(BeaconStateFulu state)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        // Seed the next epoch's mix with the current one.
        state.RandaoMixes![(int)((currentEpoch + 1) % Presets.EpochsPerHistoricalVector)] = state.GetRandaoMix(currentEpoch);
    }

    /// <summary>Capella <c>process_historical_summaries_update</c>.</summary>
    public static void ProcessHistoricalSummariesUpdate(BeaconStateFulu state)
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
    public static void ProcessParticipationFlagUpdates(BeaconStateFulu state)
    {
        state.PreviousEpochParticipation = state.CurrentEpochParticipation;
        state.CurrentEpochParticipation = new byte[state.Validators!.Length];
    }

    /// <summary>Altair <c>process_sync_committee_updates</c>: rotate committees at sync-committee period boundaries.</summary>
    public static void ProcessSyncCommitteeUpdates(BeaconStateFulu state)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        if (nextEpoch % Presets.EpochsPerSyncCommitteePeriod == 0)
        {
            state.CurrentSyncCommittee = state.NextSyncCommittee;
            state.NextSyncCommittee = GetNextSyncCommittee(state);
        }
    }

    /// <summary>Altair <c>get_next_sync_committee</c> with Electra balance-weighted sampling.</summary>
    private static SyncCommittee GetNextSyncCommittee(BeaconStateFulu state)
    {
        int[] indices = GetNextSyncCommitteeIndices(state);
        BlsPublicKey[] pubkeys = new BlsPublicKey[indices.Length];
        BlsSigner.AggregatedPublicKey aggregate = new();
        for (int i = 0; i < indices.Length; i++)
        {
            pubkeys[i] = state.Validators![indices[i]].Pubkey;
            if (!aggregate.TryAggregate(pubkeys[i].Bytes, out Bls.ERROR error))
                throw new BeaconStateException($"Invalid sync committee pubkey for validator {indices[i]}: {error}");
        }
        return new SyncCommittee
        {
            Pubkeys = pubkeys,
            AggregatePubkey = new BlsPublicKey(aggregate.PublicKey.Compress()),
        };
    }

    /// <summary>Electra <c>get_next_sync_committee_indices</c>: balance-weighted sampling against <c>MAX_EFFECTIVE_BALANCE_ELECTRA</c> with 16-bit random values.</summary>
    private static int[] GetNextSyncCommitteeIndices(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch() + 1;
        int[] activeValidatorIndices = state.GetActiveValidatorIndices(epoch);
        Hash256 seed = state.GetSeed(epoch, DomainType.SyncCommittee);

        Span<byte> preimage = stackalloc byte[32 + 8];
        Span<byte> randomBytes = stackalloc byte[32];
        seed.Bytes.CopyTo(preimage);

        int[] syncCommitteeIndices = new int[Presets.SyncCommitteeSize];
        int count = 0;
        ulong i = 0;
        while (count < syncCommitteeIndices.Length)
        {
            int shuffledIndex = SwapOrNotShuffle.ComputeShuffledIndex((int)(i % (ulong)activeValidatorIndices.Length), activeValidatorIndices.Length, seed.Bytes);
            int candidateIndex = activeValidatorIndices[shuffledIndex];
            BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], i / 16);
            SHA256.HashData(preimage, randomBytes);
            ulong randomValue = BinaryPrimitives.ReadUInt16LittleEndian(randomBytes[(int)(i % 16 * 2)..]);
            ulong effectiveBalance = state.Validators![candidateIndex].EffectiveBalance;
            if (effectiveBalance * MaxRandomValue >= Presets.MaxEffectiveBalanceElectra * randomValue)
                syncCommitteeIndices[count++] = candidateIndex;
            i++;
        }
        return syncCommitteeIndices;
    }

    /// <summary>Fulu <c>process_proposer_lookahead</c> (EIP-7917): shift out the first epoch and fill in the last.</summary>
    public static void ProcessProposerLookahead(BeaconStateFulu state)
    {
        ulong[] lookahead = state.ProposerLookahead!;
        int slotsPerEpoch = (int)Presets.SlotsPerEpoch;
        Array.Copy(lookahead, slotsPerEpoch, lookahead, 0, lookahead.Length - slotsPerEpoch);
        ulong[] lastEpochProposers = state.ComputeProposerIndices(state.GetCurrentEpoch() + Presets.MinSeedLookahead + 1);
        lastEpochProposers.CopyTo(lookahead, lookahead.Length - slotsPerEpoch);
    }

    /// <summary>
    /// <see cref="BeaconStateMutators.InitiateValidatorExit"/> with overflow-checked
    /// withdrawable-epoch arithmetic: the pyspec's <c>Epoch(...)</c> constructor rejects values
    /// above 2^64 - 1, which the <c>invalid_large_withdrawable_epoch</c> spec test relies on.
    /// </summary>
    private static void InitiateValidatorExitChecked(BeaconStateFulu state, int index, EpochCache cache)
    {
        Validator validator = state.Validators![index];
        if (validator.ExitEpoch != Presets.FarFutureEpoch)
            return;

        ulong exitQueueEpoch = state.ComputeExitEpochAndUpdateChurn(validator.EffectiveBalance, cache);

        Validator updated = validator.Clone();
        updated.ExitEpoch = exitQueueEpoch;
        updated.WithdrawableEpoch = checked(exitQueueEpoch + Presets.MinValidatorWithdrawabilityDelay);
        state.Validators[index] = updated;
    }

    /// <summary>Phase0 <c>get_eligible_validator_indices</c> membership: validators that earn rewards/penalties for the previous epoch.</summary>
    private static bool IsEligibleValidator(Validator validator, ulong previousEpoch) =>
        validator.IsActiveValidator(previousEpoch) || (validator.Slashed && previousEpoch + 1 < validator.WithdrawableEpoch);

    /// <summary>Altair <c>get_unslashed_participating_indices</c> membership for one validator and flag.</summary>
    private static bool IsUnslashedParticipant(Validator validator, byte participation, int flagIndex, ulong epoch) =>
        validator.IsActiveValidator(epoch) && !validator.Slashed && HasFlag(participation, flagIndex);

    private static bool HasFlag(byte participation, int flagIndex) => (participation & (1 << flagIndex)) != 0;

    /// <summary>Altair <c>is_in_inactivity_leak</c>: finality is more than <c>MIN_EPOCHS_TO_INACTIVITY_PENALTY</c> epochs behind.</summary>
    private static bool IsInInactivityLeak(BeaconStateFulu state) =>
        state.GetPreviousEpoch() - state.FinalizedCheckpoint!.Epoch > Presets.MinEpochsToInactivityPenalty;

    /// <summary>Phase0 <c>integer_squareroot</c>.</summary>
    private static ulong IntegerSquareRoot(ulong n)
    {
        if (n == ulong.MaxValue)
            return uint.MaxValue; // The seed below would overflow; the root of 2^64 - 1 is known.
        ulong x = n;
        ulong y = (x + 1) / 2;
        while (y < x)
        {
            x = y;
            y = (x + n / x) / 2;
        }
        return x;
    }

    /// <summary>Computes <c>hash_tree_root</c> of a <c>Vector[Root, SLOTS_PER_HISTORICAL_ROOT]</c>.</summary>
    private static Hash256 HashTreeRootOfRoots(Hash256[] roots)
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
