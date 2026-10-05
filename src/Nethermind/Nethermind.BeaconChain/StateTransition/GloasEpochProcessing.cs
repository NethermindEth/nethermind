// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Gloas <c>process_epoch</c> over <see cref="BeaconStateGloas"/>, ported from the same pinned
/// consensus-specs commit as <see cref="GloasBlockProcessing"/> (see that type's remarks).
/// </summary>
/// <remarks>
/// Compared with Fulu, <c>process_builder_pending_payments</c> rotates payments between pending
/// consolidations and effective balance updates; <c>process_ptc_window</c> rotates committees last.
/// Deposits use activation-only churn without the Eth1-bridge gate (EIP-8061); proposer selection
/// excludes slashed validators (EIP-8045). Array-based validator kernels are shared with Fulu.
/// As in Fulu, <see cref="EpochCache"/>'s total-active-balance memo remains valid until
/// <see cref="ProcessEffectiveBalanceUpdates"/> invalidates it.
/// </remarks>
public static partial class GloasEpochProcessing
{
    public static partial void ProcessEpoch(BeaconStateGloas state, EpochCache cache);
    public static partial void ProcessJustificationAndFinalization(BeaconStateGloas state, EpochCache cache);

    /// <summary>
    /// Runs the justification/finalization weighing of <c>process_justification_and_finalization</c>
    /// without mutating <paramref name="state"/>; the Gloas twin of <see cref="EpochProcessing.ComputeJustificationAndFinalization"/>.
    /// </summary>
    /// <remarks>
    /// Fork choice uses this on a Gloas block's post-state to compute its unrealized checkpoints (the
    /// spec's <c>compute_pulled_up_tip</c>) without cloning the state.
    /// </remarks>
    public static partial JustificationAndFinalizationState ComputeJustificationAndFinalization(BeaconStateGloas state, EpochCache cache);

    /// <summary>Altair <c>process_inactivity_updates</c>, unmodified in Gloas.</summary>
    public static partial void ProcessInactivityUpdates(BeaconStateGloas state);
    public static partial void ProcessRewardsAndPenalties(BeaconStateGloas state, EpochCache cache);

    /// <summary>Electra <c>process_registry_updates</c>, unmodified in Gloas.</summary>
    public static partial void ProcessRegistryUpdates(BeaconStateGloas state, EpochCache cache);

    /// <summary>Electra <c>process_slashings</c>, unmodified in Gloas.</summary>
    public static partial void ProcessSlashings(BeaconStateGloas state, EpochCache cache);
    public static partial void ProcessEth1DataReset(BeaconStateGloas state);

    /// <summary>
    /// Gloas <c>process_pending_deposits</c> (modified, EIP-8061): the queue draws on the
    /// activation-only churn, and the Electra gate on unapplied Eth1-bridge deposits is gone.
    /// </summary>
    public static partial void ProcessPendingDeposits(BeaconStateGloas state, EpochCache cache);
    private static partial Dictionary<BlsPublicKey, int> IndexPubkeys(Validator[] validators);
    private static partial void ApplyPendingDeposit(BeaconStateGloas state, PendingDeposit deposit, Dictionary<BlsPublicKey, int> pubkeyToIndex);

    /// <summary>Electra <c>process_pending_consolidations</c>, unmodified in Gloas.</summary>
    public static partial void ProcessPendingConsolidations(BeaconStateGloas state);

    /// <summary>
    /// Gloas <c>process_builder_pending_payments</c>: queue previous-epoch payments meeting the PTC quorum,
    /// rotate the current half into the previous half, and zero the new current half.
    /// Rotation permits reuse of <c>SLOTS_PER_EPOCH + slot % SLOTS_PER_EPOCH</c> each epoch.
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

        // Matches spec slicing in place: shared arrays observe the rotation; the Fulu upgrade creates a fresh array.
        Array.Copy(payments, slotsPerEpoch, payments, 0, slotsPerEpoch);
        for (int i = slotsPerEpoch; i < payments.Length; i++)
        {
            payments[i] = new BuilderPendingPayment { Withdrawal = new BuilderPendingWithdrawal() };
        }
    }

    /// <summary>Electra <c>process_effective_balance_updates</c>, unmodified in Gloas.</summary>
    public static partial void ProcessEffectiveBalanceUpdates(BeaconStateGloas state, EpochCache cache);
    public static partial void ProcessSlashingsReset(BeaconStateGloas state);
    public static partial void ProcessRandaoMixesReset(BeaconStateGloas state);
    public static partial void ProcessHistoricalSummariesUpdate(BeaconStateGloas state);
    public static partial void ProcessParticipationFlagUpdates(BeaconStateGloas state);
    public static partial void ProcessSyncCommitteeUpdates(BeaconStateGloas state);
    private static partial SyncCommittee GetNextSyncCommittee(BeaconStateGloas state);

    /// <summary>Fulu <c>process_proposer_lookahead</c> (EIP-7917) over the Gloas <see cref="GetBeaconProposerIndices"/>.</summary>
    public static partial void ProcessProposerLookahead(BeaconStateGloas state);

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
    /// Bypasses <see cref="EpochCache.GetCommitteeCache(BeaconStateGloas, ulong)"/>: its LRU key is this
    /// slot's shuffling decision root, which is not yet in <c>block_roots</c>.
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
    public static PayloadTimelinessCommittee ComputePtc(BeaconStateGloas state, CommitteeCache committees, ulong slot) =>
        GloasForkTransition.ComputePtcFromSeed(state.GetSeed(BeaconStateAccessors.ComputeEpochAtSlot(slot), DomainType.PtcAttester), state.Validators!, committees, slot);

    private static partial bool IsEligibleValidator(Validator validator, ulong previousEpoch);
    private static partial bool IsUnslashedParticipant(Validator validator, byte participation, int flagIndex, ulong epoch);
    private static partial bool IsInInactivityLeak(BeaconStateGloas state);
    private static partial Hash256 HashTreeRootOfRoots(Hash256[] roots);
}
