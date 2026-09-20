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
/// Gloas <c>process_epoch</c> over <see cref="BeaconStateGloas"/>, ported from the same pinned
/// consensus-specs commit as <see cref="GloasBlockProcessing"/> (see that type's remarks).
/// </summary>
/// <remarks>
/// Relative to the Fulu <see cref="EpochProcessing"/> the pinned spec changes four things:
/// <c>process_builder_pending_payments</c> (new, between pending consolidations and effective
/// balance updates) rotates the builder payment window, <c>process_ptc_window</c> (new, last)
/// rotates the payload timeliness committees, <c>process_pending_deposits</c> consumes the EIP-8061
/// activation-only churn and drops the retired Eth1-bridge gate, and proposer selection excludes
/// slashed validators (EIP-8045). Every other step is the Fulu step re-typed; it is duplicated
/// rather than shared for the reason <see cref="GloasStateAccessors"/> gives. The total-active-balance
/// memo in <see cref="EpochCache"/> stays valid until <see cref="ProcessEffectiveBalanceUpdates"/>
/// invalidates it, exactly as in the Fulu pipeline.
/// </remarks>
public static class GloasEpochProcessing
{
    public static void ProcessEpoch(BeaconStateGloas state, EpochCache cache)
    {
        ProcessJustificationAndFinalization(state, cache);
        ProcessInactivityUpdates(state);
        ProcessRewardsAndPenalties(state, cache);
        ProcessRegistryUpdates(state, cache);
        ProcessSlashings(state, cache);
        ProcessEth1DataReset(state);
        ProcessPendingDeposits(state, cache);
        ProcessPendingConsolidations(state);
        ProcessBuilderPendingPayments(state, cache);
        ProcessEffectiveBalanceUpdates(state, cache);
        ProcessSlashingsReset(state);
        ProcessRandaoMixesReset(state);
        ProcessHistoricalSummariesUpdate(state);
        ProcessParticipationFlagUpdates(state);
        ProcessSyncCommitteeUpdates(state);
        ProcessProposerLookahead(state);
        ProcessPtcWindow(state);
    }

    /// <summary>Altair <c>process_justification_and_finalization</c>, unmodified in Gloas.</summary>
    public static void ProcessJustificationAndFinalization(BeaconStateGloas state, EpochCache cache)
    {
        // Skip FFG updates in the first two epochs so the 0x00 root stubs are never touched.
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
            if (validator.IsActiveValidator(previousEpoch) && BeaconStateAccessors.HasParticipationFlag(previousParticipation[i], Presets.TimelyTargetFlagIndex))
                previousTargetBalance += validator.EffectiveBalance;
            if (validator.IsActiveValidator(currentEpoch) && BeaconStateAccessors.HasParticipationFlag(currentParticipation[i], Presets.TimelyTargetFlagIndex))
                currentTargetBalance += validator.EffectiveBalance;
        }

        WeighJustificationAndFinalization(
            state,
            state.GetTotalActiveBalance(cache),
            Math.Max(Presets.EffectiveBalanceIncrement, previousTargetBalance),
            Math.Max(Presets.EffectiveBalanceIncrement, currentTargetBalance));
    }

    private static void WeighJustificationAndFinalization(BeaconStateGloas state, ulong totalActiveBalance, ulong previousTargetBalance, ulong currentTargetBalance)
    {
        ulong previousEpoch = state.GetPreviousEpoch();
        ulong currentEpoch = state.GetCurrentEpoch();
        Checkpoint oldPreviousJustifiedCheckpoint = state.PreviousJustifiedCheckpoint!;
        Checkpoint oldCurrentJustifiedCheckpoint = state.CurrentJustifiedCheckpoint!;

        // A fresh BitArray: the upgrade shares the bits with the Fulu pre-state, which must stay intact.
        BitArray bits = new(state.JustificationBits!);
        state.PreviousJustifiedCheckpoint = state.CurrentJustifiedCheckpoint;
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
        state.JustificationBits = bits;

        if (bits[1] && bits[2] && bits[3] && oldPreviousJustifiedCheckpoint.Epoch + 3 == currentEpoch)
            state.FinalizedCheckpoint = oldPreviousJustifiedCheckpoint;
        if (bits[1] && bits[2] && oldPreviousJustifiedCheckpoint.Epoch + 2 == currentEpoch)
            state.FinalizedCheckpoint = oldPreviousJustifiedCheckpoint;
        if (bits[0] && bits[1] && bits[2] && oldCurrentJustifiedCheckpoint.Epoch + 2 == currentEpoch)
            state.FinalizedCheckpoint = oldCurrentJustifiedCheckpoint;
        if (bits[0] && bits[1] && oldCurrentJustifiedCheckpoint.Epoch + 1 == currentEpoch)
            state.FinalizedCheckpoint = oldCurrentJustifiedCheckpoint;
    }

    /// <summary>Altair <c>process_inactivity_updates</c>, unmodified in Gloas.</summary>
    public static void ProcessInactivityUpdates(BeaconStateGloas state)
    {
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

            if (IsUnslashedParticipant(validator, previousParticipation[i], Presets.TimelyTargetFlagIndex, previousEpoch))
                inactivityScores[i] -= Math.Min(1, inactivityScores[i]);
            else
                inactivityScores[i] += Presets.InactivityScoreBias;
            if (!isInInactivityLeak)
                inactivityScores[i] -= Math.Min(Presets.InactivityScoreRecoveryRate, inactivityScores[i]);
        }
    }

    /// <summary>Altair <c>process_rewards_and_penalties</c>, unmodified in Gloas; fused per validator like the Fulu port (see <see cref="EpochProcessing.ProcessRewardsAndPenalties"/>).</summary>
    public static void ProcessRewardsAndPenalties(BeaconStateGloas state, EpochCache cache)
    {
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ulong previousEpoch = state.GetPreviousEpoch();
        ulong totalActiveBalance = state.GetTotalActiveBalance(cache);
        ulong baseRewardPerIncrement = state.GetBaseRewardPerIncrement(cache);
        ulong activeIncrements = totalActiveBalance / Presets.EffectiveBalanceIncrement;
        bool isInInactivityLeak = IsInInactivityLeak(state);

        Validator[] validators = state.Validators!;
        byte[] previousParticipation = state.PreviousEpochParticipation ?? [];

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

            if (!IsUnslashedParticipant(validator, previousParticipation[i], Presets.TimelyTargetFlagIndex, previousEpoch))
                state.DecreaseBalance(i, validator.EffectiveBalance * state.InactivityScores![i] / (Presets.InactivityScoreBias * Presets.InactivityPenaltyQuotientBellatrix));
        }
    }

    /// <summary>Electra <c>process_registry_updates</c>, unmodified in Gloas.</summary>
    public static void ProcessRegistryUpdates(BeaconStateGloas state, EpochCache cache)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        ulong activationEpoch = BeaconStateAccessors.ComputeActivationExitEpoch(currentEpoch);
        ulong finalizedEpoch = state.FinalizedCheckpoint!.Epoch;

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
            else if (validator.ActivationEligibilityEpoch <= finalizedEpoch && validator.ActivationEpoch == Presets.FarFutureEpoch)
            {
                Validator updated = validator.Clone();
                updated.ActivationEpoch = activationEpoch;
                validators[i] = updated;
            }
        }
    }

    /// <summary>Electra <c>process_slashings</c>, unmodified in Gloas.</summary>
    public static void ProcessSlashings(BeaconStateGloas state, EpochCache cache)
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
    public static void ProcessEth1DataReset(BeaconStateGloas state)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        if (nextEpoch % Presets.EpochsPerEth1VotingPeriod == 0)
            state.Eth1DataVotes = [];
    }

    /// <summary>
    /// Gloas <c>process_pending_deposits</c> (modified, EIP-8061): the queue draws on the
    /// activation-only churn, and the Electra gate on unapplied Eth1-bridge deposits is gone.
    /// </summary>
    public static void ProcessPendingDeposits(BeaconStateGloas state, EpochCache cache)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        ulong availableForProcessing = state.DepositBalanceToConsume + state.GetActivationChurnLimit(cache);
        ulong processedAmount = 0;
        int nextDepositIndex = 0;
        List<PendingDeposit> depositsToPostpone = [];
        bool isChurnLimitReached = false;
        ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(state.FinalizedCheckpoint!.Epoch);

        Dictionary<BlsPublicKey, int> pubkeyToIndex = [];
        for (int i = 0; i < state.Validators!.Length; i++)
        {
            pubkeyToIndex.TryAdd(state.Validators[i].Pubkey, i);
        }

        PendingDeposit[] pendingDeposits = state.PendingDeposits ?? [];
        foreach (PendingDeposit deposit in pendingDeposits)
        {
            if (deposit.Slot > finalizedSlot)
                break;
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
                ApplyPendingDeposit(state, deposit, pubkeyToIndex);
            }
            else if (isValidatorExited)
            {
                depositsToPostpone.Add(deposit);
            }
            else
            {
                isChurnLimitReached = processedAmount + deposit.Amount > availableForProcessing;
                if (isChurnLimitReached)
                    break;
                processedAmount += deposit.Amount;
                ApplyPendingDeposit(state, deposit, pubkeyToIndex);
            }

            nextDepositIndex++;
        }

        state.PendingDeposits = [.. pendingDeposits[nextDepositIndex..], .. depositsToPostpone];
        state.DepositBalanceToConsume = isChurnLimitReached ? availableForProcessing - processedAmount : 0;
    }

    /// <summary>Electra <c>apply_pending_deposit</c>, unmodified in Gloas.</summary>
    private static void ApplyPendingDeposit(BeaconStateGloas state, PendingDeposit deposit, Dictionary<BlsPublicKey, int> pubkeyToIndex)
    {
        if (pubkeyToIndex.TryGetValue(deposit.Pubkey, out int index))
        {
            state.IncreaseBalance(index, deposit.Amount);
        }
        else if (DepositSignatureVerifier.IsValid(deposit.Pubkey, deposit.WithdrawalCredentials!, deposit.Amount, deposit.Signature))
        {
            pubkeyToIndex.TryAdd(deposit.Pubkey, state.Validators!.Length);
            state.AddValidatorToRegistry(deposit.Pubkey, deposit.WithdrawalCredentials!, deposit.Amount);
        }
    }

    /// <summary>Electra <c>process_pending_consolidations</c>, unmodified in Gloas.</summary>
    public static void ProcessPendingConsolidations(BeaconStateGloas state)
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

            ulong sourceEffectiveBalance = Math.Min(state.Balances![(int)pendingConsolidation.SourceIndex], sourceValidator.EffectiveBalance);
            state.DecreaseBalance((int)pendingConsolidation.SourceIndex, sourceEffectiveBalance);
            state.IncreaseBalance((int)pendingConsolidation.TargetIndex, sourceEffectiveBalance);
            nextPendingConsolidation++;
        }

        state.PendingConsolidations = pendingConsolidations[nextPendingConsolidation..];
    }

    /// <summary>
    /// Gloas <c>process_builder_pending_payments</c> (new): the previous epoch's payments that reached
    /// the PTC quorum are queued for withdrawal, the current epoch's half becomes the previous half,
    /// and a zeroed half takes its place. This rotation is what lets the per-slot payment address
    /// (<c>SLOTS_PER_EPOCH + slot % SLOTS_PER_EPOCH</c>) be reused every epoch.
    /// </summary>
    public static void ProcessBuilderPendingPayments(BeaconStateGloas state, EpochCache cache)
    {
        ulong quorum = state.GetBuilderPaymentQuorumThreshold(cache);
        BuilderPendingPayment[] payments = state.BuilderPendingPayments!;
        int slotsPerEpoch = (int)Presets.SlotsPerEpoch;

        List<BuilderPendingWithdrawal> queued = [.. state.BuilderPendingWithdrawals ?? []];
        for (int i = 0; i < slotsPerEpoch; i++)
        {
            if (payments[i].Weight >= quorum)
                queued.Add(payments[i].Withdrawal!);
        }
        state.BuilderPendingWithdrawals = [.. queued];

        // The vector is written in place, so a state that shares the array (the Fulu pre-state does
        // not: the upgrade builds a fresh one) would observe the rotation; matches the spec's slicing.
        Array.Copy(payments, slotsPerEpoch, payments, 0, slotsPerEpoch);
        for (int i = slotsPerEpoch; i < payments.Length; i++)
        {
            payments[i] = new BuilderPendingPayment { Withdrawal = new BuilderPendingWithdrawal() };
        }
    }

    /// <summary>Electra <c>process_effective_balance_updates</c>, unmodified in Gloas.</summary>
    public static void ProcessEffectiveBalanceUpdates(BeaconStateGloas state, EpochCache cache)
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
    public static void ProcessSlashingsReset(BeaconStateGloas state) =>
        state.Slashings![(int)((state.GetCurrentEpoch() + 1) % Presets.EpochsPerSlashingsVector)] = 0;

    /// <summary>Phase0 <c>process_randao_mixes_reset</c>.</summary>
    public static void ProcessRandaoMixesReset(BeaconStateGloas state)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        state.RandaoMixes![(int)((currentEpoch + 1) % Presets.EpochsPerHistoricalVector)] = state.GetRandaoMix(currentEpoch);
    }

    /// <summary>Capella <c>process_historical_summaries_update</c>.</summary>
    public static void ProcessHistoricalSummariesUpdate(BeaconStateGloas state)
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

    /// <summary>Altair <c>process_participation_flag_updates</c>.</summary>
    public static void ProcessParticipationFlagUpdates(BeaconStateGloas state)
    {
        state.PreviousEpochParticipation = state.CurrentEpochParticipation;
        state.CurrentEpochParticipation = new byte[state.Validators!.Length];
    }

    /// <summary>Altair <c>process_sync_committee_updates</c> with the Gloas <c>get_next_sync_committee_indices</c> (balance-weighted selection over the shuffled active set).</summary>
    public static void ProcessSyncCommitteeUpdates(BeaconStateGloas state)
    {
        ulong nextEpoch = state.GetCurrentEpoch() + 1;
        if (nextEpoch % Presets.EpochsPerSyncCommitteePeriod == 0)
        {
            state.CurrentSyncCommittee = state.NextSyncCommittee;
            state.NextSyncCommittee = GetNextSyncCommittee(state);
        }
    }

    private static SyncCommittee GetNextSyncCommittee(BeaconStateGloas state)
    {
        ulong epoch = state.GetCurrentEpoch() + 1;
        Hash256 seed = state.GetSeed(epoch, DomainType.SyncCommittee);
        int[] indices = GloasForkTransition.ComputeBalanceWeightedSelection(
            state.Validators!, state.GetActiveValidatorIndices(epoch), seed.Bytes, Presets.SyncCommitteeSize, shuffleIndices: true);

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

    /// <summary>Fulu <c>process_proposer_lookahead</c> (EIP-7917) over the Gloas <see cref="GetBeaconProposerIndices"/>.</summary>
    public static void ProcessProposerLookahead(BeaconStateGloas state)
    {
        ulong[] lookahead = state.ProposerLookahead!;
        int slotsPerEpoch = (int)Presets.SlotsPerEpoch;
        Array.Copy(lookahead, slotsPerEpoch, lookahead, 0, lookahead.Length - slotsPerEpoch);
        ulong[] lastEpochProposers = GetBeaconProposerIndices(state, state.GetCurrentEpoch() + Presets.MinSeedLookahead + 1);
        lastEpochProposers.CopyTo(lookahead, lookahead.Length - slotsPerEpoch);
    }

    /// <summary>
    /// Gloas <c>get_beacon_proposer_indices</c> (modified, EIP-8045): slashed validators leave the
    /// candidate pool, then <c>compute_proposer_indices</c> draws one balance-weighted proposer per
    /// slot from the shuffled pool under a per-slot seed.
    /// </summary>
    public static ulong[] GetBeaconProposerIndices(BeaconStateGloas state, ulong epoch)
    {
        Validator[] validators = state.Validators!;
        List<int> indices = [];
        foreach (int i in state.GetActiveValidatorIndices(epoch))
        {
            if (!validators[i].Slashed)
                indices.Add(i);
        }
        Hash256 epochSeed = state.GetSeed(epoch, DomainType.BeaconProposer);

        Span<byte> preimage = stackalloc byte[32 + 8];
        Span<byte> slotSeed = stackalloc byte[32];
        epochSeed.Bytes.CopyTo(preimage);

        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        ulong[] proposerIndices = new ulong[Presets.SlotsPerEpoch];
        for (int i = 0; i < proposerIndices.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], startSlot + (ulong)i);
            SHA256.HashData(preimage, slotSeed);
            proposerIndices[i] = (ulong)GloasForkTransition.ComputeBalanceWeightedSelection(validators, indices, slotSeed, size: 1, shuffleIndices: true)[0];
        }
        return proposerIndices;
    }

    /// <summary>
    /// Gloas <c>process_ptc_window</c> (new): shift the window one epoch and compute the committees of
    /// the epoch <c>MIN_SEED_LOOKAHEAD + 1</c> ahead, whose seed this epoch's RANDAO just fixed.
    /// </summary>
    /// <remarks>
    /// Builds the shuffling directly rather than through <see cref="EpochCache.GetCommitteeCache(BeaconStateGloas, ulong)"/>:
    /// that LRU keys on the shuffling decision root, which for the epoch being filled is the block at
    /// this very slot and so is not yet in <c>block_roots</c>.
    /// </remarks>
    public static void ProcessPtcWindow(BeaconStateGloas state)
    {
        PayloadTimelinessCommittee[] window = state.PtcWindow!;
        int slotsPerEpoch = (int)Presets.SlotsPerEpoch;
        Array.Copy(window, slotsPerEpoch, window, 0, window.Length - slotsPerEpoch);

        ulong nextEpoch = state.GetCurrentEpoch() + Presets.MinSeedLookahead + 1;
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(nextEpoch);
        CommitteeCache committees = CommitteeCache.Build(state, nextEpoch);
        for (int i = 0; i < slotsPerEpoch; i++)
        {
            window[window.Length - slotsPerEpoch + i] = ComputePtc(state, committees, startSlot + (ulong)i);
        }
    }

    /// <summary>Spec <c>compute_ptc</c> over a Gloas state; the upgrade-time twin reads the Fulu pre-state (see <see cref="GloasForkTransition.InitializePtcWindow"/>).</summary>
    public static PayloadTimelinessCommittee ComputePtc(BeaconStateGloas state, CommitteeCache committees, ulong slot)
    {
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(slot);
        Hash256 domainSeed = state.GetSeed(epoch, DomainType.PtcAttester);
        Span<byte> preimage = stackalloc byte[32 + 8];
        domainSeed.Bytes.CopyTo(preimage);
        BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], slot);
        byte[] seed = SHA256.HashData(preimage);

        List<int> indices = [];
        for (int i = 0; i < committees.CommitteesPerSlot; i++)
            indices.AddRange(committees.GetBeaconCommittee(slot, i));

        int[] selected = GloasForkTransition.ComputeBalanceWeightedSelection(state.Validators!, indices, seed, (int)Presets.PtcSize, shuffleIndices: false);
        ulong[] ptcIndices = new ulong[selected.Length];
        for (int i = 0; i < selected.Length; i++)
            ptcIndices[i] = (ulong)selected[i];

        return new PayloadTimelinessCommittee
        {
            Indices = ptcIndices,
        };
    }

    private static bool IsEligibleValidator(Validator validator, ulong previousEpoch) =>
        validator.IsActiveValidator(previousEpoch) || (validator.Slashed && previousEpoch + 1 < validator.WithdrawableEpoch);

    private static bool IsUnslashedParticipant(Validator validator, byte participation, int flagIndex, ulong epoch) =>
        validator.IsActiveValidator(epoch) && !validator.Slashed && BeaconStateAccessors.HasParticipationFlag(participation, flagIndex);

    private static bool IsInInactivityLeak(BeaconStateGloas state) =>
        state.GetPreviousEpoch() - state.FinalizedCheckpoint!.Epoch > Presets.MinEpochsToInactivityPenalty;

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
