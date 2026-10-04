// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
/// Gloas <c>process_epoch</c> over <see cref="BeaconStateGloas"/>, ported from the same pinned
/// consensus-specs commit as <see cref="GloasBlockProcessing"/> (see that type's remarks).
/// </summary>
/// <remarks>
/// Relative to the Fulu <see cref="EpochProcessing"/> the pinned spec changes four things:
/// <c>process_builder_pending_payments</c> (new, between pending consolidations and effective
/// balance updates) rotates the builder payment window, <c>process_ptc_window</c> (new, last)
/// rotates the payload timeliness committees, <c>process_pending_deposits</c> consumes the EIP-8061
/// activation-only churn and drops the retired Eth1-bridge gate, and proposer selection excludes
/// slashed validators (EIP-8045). Common validator loops are shared with the Fulu pipeline through
/// array-based kernels. The total-active-balance
/// memo in <see cref="EpochCache"/> stays valid until <see cref="ProcessEffectiveBalanceUpdates"/>
/// invalidates it, exactly as in the Fulu pipeline.
/// </remarks>
public static partial class GloasEpochProcessing
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

    public static partial void ProcessJustificationAndFinalization(BeaconStateGloas state, EpochCache cache);

    /// <summary>
    /// Runs the justification/finalization weighing of <c>process_justification_and_finalization</c>
    /// without mutating <paramref name="state"/>; the Gloas twin of <see cref="EpochProcessing.ComputeJustificationAndFinalization"/>.
    /// </summary>
    /// <remarks>
    /// Fork choice uses this on a Gloas block's post-state to compute its unrealized checkpoints (the
    /// spec's <c>compute_pulled_up_tip</c>) without cloning the state.
    /// </remarks>
    public static JustificationAndFinalizationState ComputeJustificationAndFinalization(BeaconStateGloas state, EpochCache cache)
    {
        JustificationAndFinalizationState result = new(state);

        // Skip FFG updates in the first two epochs so the 0x00 root stubs are never touched.
        if (state.GetCurrentEpoch() <= Presets.GenesisEpoch + 1)
            return result;

        (ulong previousTargetBalance, ulong currentTargetBalance) = EpochProcessing.GetTargetBalances(
            state.Validators!, state.PreviousEpochParticipation ?? [], state.CurrentEpochParticipation ?? [],
            state.GetPreviousEpoch(), state.GetCurrentEpoch());
        EpochProcessing.WeighJustificationAndFinalization(
            result, state.GetTotalActiveBalance(cache), previousTargetBalance, currentTargetBalance,
            state.Slot, state.BlockRoots!);
        return result;
    }

    /// <summary>Altair <c>process_inactivity_updates</c>, unmodified in Gloas.</summary>
    public static void ProcessInactivityUpdates(BeaconStateGloas state)
    {
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ulong previousEpoch = state.GetPreviousEpoch();
        bool isInInactivityLeak = IsInInactivityLeak(state);
        EpochProcessing.ProcessInactivityUpdates(state.Validators!, state.InactivityScores!, state.PreviousEpochParticipation ?? [], previousEpoch, isInInactivityLeak);
    }

    public static partial void ProcessRewardsAndPenalties(BeaconStateGloas state, EpochCache cache);

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
        EpochProcessing.ProcessSlashings(state.Validators!, state.Balances!, state.Slashings!, epoch, totalBalance);
    }

    public static partial void ProcessEth1DataReset(BeaconStateGloas state);

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

        // Spec process_pending_deposits: validator_pubkeys.index is needed only after the queue gates pass.
        Dictionary<BlsPublicKey, int>? pubkeyToIndex = null;

        PendingDeposit[] pendingDeposits = state.PendingDeposits ?? [];
        foreach (PendingDeposit deposit in pendingDeposits)
        {
            if (deposit.Slot > finalizedSlot)
                break;
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

    private static partial Dictionary<BlsPublicKey, int> IndexPubkeys(Validator[] validators);

    private static partial void ApplyPendingDeposit(BeaconStateGloas state, PendingDeposit deposit, Dictionary<BlsPublicKey, int> pubkeyToIndex);

    /// <summary>Electra <c>process_pending_consolidations</c>, unmodified in Gloas.</summary>
    public static void ProcessPendingConsolidations(BeaconStateGloas state)
    {
        PendingConsolidation[] pendingConsolidations = state.PendingConsolidations ?? [];
        int nextPendingConsolidation = EpochProcessing.ProcessPendingConsolidations(state.Validators!, state.Balances!, pendingConsolidations, state.GetCurrentEpoch() + 1);
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
        EpochProcessing.ProcessEffectiveBalanceUpdates(state.Validators!, state.Balances!);
        cache.InvalidateTotalActiveBalance();
    }

    public static partial void ProcessSlashingsReset(BeaconStateGloas state);

    public static partial void ProcessRandaoMixesReset(BeaconStateGloas state);

    public static partial void ProcessHistoricalSummariesUpdate(BeaconStateGloas state);

    public static partial void ProcessParticipationFlagUpdates(BeaconStateGloas state);

    public static partial void ProcessSyncCommitteeUpdates(BeaconStateGloas state);

    private static SyncCommittee GetNextSyncCommittee(BeaconStateGloas state)
    {
        ulong epoch = state.GetCurrentEpoch() + 1;
        Hash256 seed = state.GetSeed(epoch, DomainType.SyncCommittee);
        int[] indices = GloasForkTransition.ComputeBalanceWeightedSelection(
            state.Validators!, state.GetActiveValidatorIndices(epoch), seed.Bytes, Presets.SyncCommitteeSize, shuffleIndices: true);

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

    private static partial bool IsEligibleValidator(Validator validator, ulong previousEpoch);

    private static partial bool IsUnslashedParticipant(Validator validator, byte participation, int flagIndex, ulong epoch);

    private static partial bool IsInInactivityLeak(BeaconStateGloas state);

    private static partial Hash256 HashTreeRootOfRoots(Hash256[] roots);
}
