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
public static partial class EpochProcessing
{
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

    public static partial void ProcessJustificationAndFinalization(BeaconStateFulu state, EpochCache cache);

    /// <summary>
    /// Runs the justification/finalization weighing of <c>process_justification_and_finalization</c>
    /// without mutating <paramref name="state"/>.
    /// </summary>
    /// <remarks>
    /// Fork choice uses this on a block's post-state to compute its unrealized checkpoints (the
    /// spec's <c>compute_pulled_up_tip</c>) without cloning the state.
    /// </remarks>
    public static JustificationAndFinalizationState ComputeJustificationAndFinalization(BeaconStateFulu state, EpochCache cache)
    {
        JustificationAndFinalizationState result = new(state);

        // Initial FFG checkpoint values have a `0x00` stub for `root`.
        // Skip FFG updates in the first two epochs to avoid corner cases that might result in
        // modifying this stub.
        if (state.GetCurrentEpoch() <= Presets.GenesisEpoch + 1)
            return result;

        (ulong previousTargetBalance, ulong currentTargetBalance) = GetTargetBalances(
            state.Validators!, state.PreviousEpochParticipation ?? [], state.CurrentEpochParticipation ?? [],
            state.GetPreviousEpoch(), state.GetCurrentEpoch());
        WeighJustificationAndFinalization(
            result, state.GetTotalActiveBalance(cache), previousTargetBalance, currentTargetBalance,
            state.Slot, state.BlockRoots!);
        return result;
    }

    internal static (ulong Previous, ulong Current) GetTargetBalances(Validator[] validators, byte[] previousParticipation, byte[] currentParticipation, ulong previousEpoch, ulong currentEpoch)
    {
        ulong previousTargetBalance = 0;
        ulong currentTargetBalance = 0;
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
        return (Math.Max(Presets.EffectiveBalanceIncrement, previousTargetBalance), Math.Max(Presets.EffectiveBalanceIncrement, currentTargetBalance));
    }

    /// <summary>Phase0 <c>weigh_justification_and_finalization</c>: justification-bit shift, 2/3 supermajority justification, and the four finalization rules.</summary>
    internal static void WeighJustificationAndFinalization(JustificationAndFinalizationState result, ulong totalActiveBalance, ulong previousTargetBalance, ulong currentTargetBalance, ulong stateSlot, Hash256[] blockRoots)
    {
        ulong currentEpoch = BeaconStateAccessors.ComputeEpochAtSlot(stateSlot);
        ulong previousEpoch = currentEpoch == Presets.GenesisEpoch ? Presets.GenesisEpoch : currentEpoch - 1;
        Checkpoint oldPreviousJustifiedCheckpoint = result.PreviousJustifiedCheckpoint;
        Checkpoint oldCurrentJustifiedCheckpoint = result.CurrentJustifiedCheckpoint;

        // Process justifications.
        result.PreviousJustifiedCheckpoint = result.CurrentJustifiedCheckpoint;
        BitArray bits = result.JustificationBits;
        for (int i = bits.Length - 1; i >= 1; i--)
        {
            bits[i] = bits[i - 1];
        }
        bits[0] = false;
        if (previousTargetBalance * 3 >= totalActiveBalance * 2)
        {
            result.CurrentJustifiedCheckpoint = new Checkpoint
            {
                Epoch = previousEpoch,
                Root = BeaconStateAccessors.GetBlockRootAtSlot(stateSlot, blockRoots, BeaconStateAccessors.ComputeStartSlotAtEpoch(previousEpoch))
            };
            bits[1] = true;
        }
        if (currentTargetBalance * 3 >= totalActiveBalance * 2)
        {
            result.CurrentJustifiedCheckpoint = new Checkpoint
            {
                Epoch = currentEpoch,
                Root = BeaconStateAccessors.GetBlockRootAtSlot(stateSlot, blockRoots, BeaconStateAccessors.ComputeStartSlotAtEpoch(currentEpoch))
            };
            bits[0] = true;
        }

        // Process finalizations.
        // The 2nd/3rd/4th most recent epochs are justified, the 2nd using the 4th as source.
        if (bits[1] && bits[2] && bits[3] && oldPreviousJustifiedCheckpoint.Epoch + 3 == currentEpoch)
            result.FinalizedCheckpoint = oldPreviousJustifiedCheckpoint;
        // The 2nd/3rd most recent epochs are justified, the 2nd using the 3rd as source.
        if (bits[1] && bits[2] && oldPreviousJustifiedCheckpoint.Epoch + 2 == currentEpoch)
            result.FinalizedCheckpoint = oldPreviousJustifiedCheckpoint;
        // The 1st/2nd/3rd most recent epochs are justified, the 1st using the 3rd as source.
        if (bits[0] && bits[1] && bits[2] && oldCurrentJustifiedCheckpoint.Epoch + 2 == currentEpoch)
            result.FinalizedCheckpoint = oldCurrentJustifiedCheckpoint;
        // The 1st/2nd most recent epochs are justified, the 1st using the 2nd as source.
        if (bits[0] && bits[1] && oldCurrentJustifiedCheckpoint.Epoch + 1 == currentEpoch)
            result.FinalizedCheckpoint = oldCurrentJustifiedCheckpoint;
    }

    /// <summary>Altair <c>process_inactivity_updates</c> (EIP-7045 inactivity scores).</summary>
    public static void ProcessInactivityUpdates(BeaconStateFulu state)
    {
        // Skip the genesis epoch as score updates are based on the previous epoch participation.
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ulong previousEpoch = state.GetPreviousEpoch();
        bool isInInactivityLeak = IsInInactivityLeak(state);
        ProcessInactivityUpdates(state.Validators!, state.InactivityScores!, state.PreviousEpochParticipation ?? [], previousEpoch, isInInactivityLeak);
    }

    internal static void ProcessInactivityUpdates(Validator[] validators, ulong[] inactivityScores, byte[] previousParticipation, ulong previousEpoch, bool isInInactivityLeak)
    {
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

    public static partial void ProcessRewardsAndPenalties(BeaconStateFulu state, EpochCache cache);

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
                state.InitiateValidatorExit(i, cache);
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
        ProcessSlashings(state.Validators!, state.Balances!, state.Slashings!, epoch, totalBalance);
    }

    internal static void ProcessSlashings(Validator[] validators, ulong[] balances, ulong[] slashings, ulong epoch, ulong totalBalance)
    {
        ulong totalSlashings = 0;
        foreach (ulong slashing in slashings)
        {
            totalSlashings += slashing;
        }
        ulong adjustedTotalSlashingBalance = Math.Min(totalSlashings * Presets.ProportionalSlashingMultiplierBellatrix, totalBalance);
        ulong penaltyPerEffectiveBalanceIncrement = adjustedTotalSlashingBalance / (totalBalance / Presets.EffectiveBalanceIncrement);
        ulong targetWithdrawableEpoch = epoch + Presets.EpochsPerSlashingsVector / 2;

        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (validator.Slashed && targetWithdrawableEpoch == validator.WithdrawableEpoch)
                DecreaseBalance(ref balances[i], penaltyPerEffectiveBalanceIncrement * (validator.EffectiveBalance / Presets.EffectiveBalanceIncrement));
        }
    }

    public static partial void ProcessEth1DataReset(BeaconStateFulu state);

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

        // Spec process_pending_deposits: validator_pubkeys.index is needed only after the queue gates pass.
        Dictionary<BlsPublicKey, int>? pubkeyToIndex = null;

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
            pubkeyToIndex ??= IndexPubkeys(state.Validators!);
            if (pubkeyToIndex.TryGetValue(deposit.Pubkey, out int validatorIndex))
            {
                Validator validator = state.Validators![validatorIndex];
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

    private static partial Dictionary<BlsPublicKey, int> IndexPubkeys(Validator[] validators);

    private static partial void ApplyPendingDeposit(BeaconStateFulu state, PendingDeposit deposit, Dictionary<BlsPublicKey, int> pubkeyToIndex);

    /// <summary>Electra <c>process_pending_consolidations</c> (EIP-7251): sweep consolidations whose source is withdrawable.</summary>
    public static void ProcessPendingConsolidations(BeaconStateFulu state)
    {
        PendingConsolidation[] pendingConsolidations = state.PendingConsolidations ?? [];
        int nextPendingConsolidation = ProcessPendingConsolidations(state.Validators!, state.Balances!, pendingConsolidations, state.GetCurrentEpoch() + 1);
        state.PendingConsolidations = pendingConsolidations[nextPendingConsolidation..];
    }

    internal static int ProcessPendingConsolidations(Validator[] validators, ulong[] balances, PendingConsolidation[] pendingConsolidations, ulong nextEpoch)
    {
        int nextPendingConsolidation = 0;
        foreach (PendingConsolidation pendingConsolidation in pendingConsolidations)
        {
            Validator sourceValidator = validators[(int)pendingConsolidation.SourceIndex];
            if (sourceValidator.Slashed)
            {
                nextPendingConsolidation++;
                continue;
            }
            if (sourceValidator.WithdrawableEpoch > nextEpoch)
                break;

            // Move the active balance to the target; excess balance is withdrawable.
            ulong sourceEffectiveBalance = Math.Min(balances[(int)pendingConsolidation.SourceIndex], sourceValidator.EffectiveBalance);
            DecreaseBalance(ref balances[(int)pendingConsolidation.SourceIndex], sourceEffectiveBalance);
            balances[(int)pendingConsolidation.TargetIndex] += sourceEffectiveBalance;
            nextPendingConsolidation++;
        }

        return nextPendingConsolidation;
    }

    /// <summary>Electra <c>process_effective_balance_updates</c>: hysteresis against the EIP-7251 per-validator max effective balance.</summary>
    public static void ProcessEffectiveBalanceUpdates(BeaconStateFulu state, EpochCache cache)
    {
        ProcessEffectiveBalanceUpdates(state.Validators!, state.Balances!);
        cache.InvalidateTotalActiveBalance();
    }

    internal static void ProcessEffectiveBalanceUpdates(Validator[] validators, ulong[] balances)
    {
        const ulong hysteresisIncrement = Presets.EffectiveBalanceIncrement / Presets.HysteresisQuotient;
        const ulong downwardThreshold = hysteresisIncrement * Presets.HysteresisDownwardMultiplier;
        const ulong upwardThreshold = hysteresisIncrement * Presets.HysteresisUpwardMultiplier;

        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            ulong balance = balances[i];
            if (balance + downwardThreshold < validator.EffectiveBalance || validator.EffectiveBalance + upwardThreshold < balance)
            {
                Validator updated = validator.Clone();
                updated.EffectiveBalance = Math.Min(balance - balance % Presets.EffectiveBalanceIncrement, validator.GetMaxEffectiveBalance());
                validators[i] = updated;
            }
        }
    }

    public static partial void ProcessSlashingsReset(BeaconStateFulu state);

    public static partial void ProcessRandaoMixesReset(BeaconStateFulu state);

    public static partial void ProcessHistoricalSummariesUpdate(BeaconStateFulu state);

    public static partial void ProcessParticipationFlagUpdates(BeaconStateFulu state);

    public static partial void ProcessSyncCommitteeUpdates(BeaconStateFulu state);

    /// <summary>Altair <c>get_next_sync_committee</c> with Electra balance-weighted sampling.</summary>
    private static SyncCommittee GetNextSyncCommittee(BeaconStateFulu state)
    {
        int[] indices = GetNextSyncCommitteeIndices(state);
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

    /// <summary>Electra <c>get_next_sync_committee_indices</c>: balance-weighted sampling against <c>MAX_EFFECTIVE_BALANCE_ELECTRA</c> with 16-bit random values.</summary>
    private static int[] GetNextSyncCommitteeIndices(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch() + 1;
        int[] activeValidatorIndices = state.GetActiveValidatorIndices(epoch);
        if (activeValidatorIndices.Length == 0)
            throw new BeaconStateException("Cannot select a sync committee from an empty active validator set");
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
            if (effectiveBalance * BeaconStateAccessors.MaxRandomValue >= Presets.MaxEffectiveBalanceElectra * randomValue)
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

    private static void DecreaseBalance(ref ulong balance, ulong delta) =>
        balance -= Math.Min(balance, delta);

    private static partial bool IsEligibleValidator(Validator validator, ulong previousEpoch);

    private static partial bool IsUnslashedParticipant(Validator validator, byte participation, int flagIndex, ulong epoch);

    private static partial bool IsInInactivityLeak(BeaconStateFulu state);

    private static partial Hash256 HashTreeRootOfRoots(Hash256[] roots);
}
