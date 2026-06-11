// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Read-only consensus-specs accessors and predicates over <see cref="BeaconStateFulu"/>.
/// </summary>
/// <remarks>
/// Ports the Electra/Fulu <c>get_*</c>/<c>is_*</c>/<c>compute_*</c> helpers from consensus-specs
/// (semantics cross-checked against Lighthouse <c>consensus/types</c> and
/// <c>consensus/state_processing</c>). Fork and genesis-validators-root are always read from the
/// state, never from a chain-spec singleton. Spec assertions throw <see cref="BeaconStateException"/>.
/// </remarks>
public static class BeaconStateAccessors
{
    /// <summary>Electra <c>MAX_RANDOM_VALUE</c>: random values are 16-bit from this fork on.</summary>
    private const ulong MaxRandomValue = (1 << 16) - 1;

    public static ulong ComputeEpochAtSlot(ulong slot) => slot / Presets.SlotsPerEpoch;

    public static ulong ComputeStartSlotAtEpoch(ulong epoch) => epoch * Presets.SlotsPerEpoch;

    /// <summary>Returns the epoch at which an activation or exit triggered in <paramref name="epoch"/> takes effect.</summary>
    public static ulong ComputeActivationExitEpoch(ulong epoch) => epoch + 1 + Presets.MaxSeedLookahead;

    public static ulong GetCurrentEpoch(this BeaconStateFulu state) => ComputeEpochAtSlot(state.Slot);

    public static ulong GetPreviousEpoch(this BeaconStateFulu state)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        return currentEpoch == Presets.GenesisEpoch ? Presets.GenesisEpoch : currentEpoch - 1;
    }

    /// <summary>Returns the block root at the start slot of a recent <paramref name="epoch"/> (spec <c>get_block_root</c>).</summary>
    public static Hash256 GetBlockRoot(this BeaconStateFulu state, ulong epoch) =>
        state.GetBlockRootAtSlot(ComputeStartSlotAtEpoch(epoch));

    /// <summary>Returns the block root at a recent <paramref name="slot"/> (spec <c>get_block_root_at_slot</c>).</summary>
    /// <exception cref="BeaconStateException">The slot is not within the last <c>SLOTS_PER_HISTORICAL_ROOT</c> slots.</exception>
    public static Hash256 GetBlockRootAtSlot(this BeaconStateFulu state, ulong slot)
    {
        if (!(slot < state.Slot && state.Slot <= slot + Presets.SlotsPerHistoricalRoot))
            throw new BeaconStateException($"Block root for slot {slot} is not available at state slot {state.Slot}");
        return state.BlockRoots![(int)(slot % Presets.SlotsPerHistoricalRoot)];
    }

    public static Hash256 GetRandaoMix(this BeaconStateFulu state, ulong epoch) =>
        state.RandaoMixes![(int)(epoch % Presets.EpochsPerHistoricalVector)];

    /// <summary>Returns the shuffling seed for <paramref name="epoch"/> and the given domain type.</summary>
    public static Hash256 GetSeed(this BeaconStateFulu state, ulong epoch, ReadOnlySpan<byte> domainType)
    {
        Hash256 mix = state.GetRandaoMix(epoch + Presets.EpochsPerHistoricalVector - Presets.MinSeedLookahead - 1);
        Span<byte> preimage = stackalloc byte[4 + 8 + 32];
        domainType.CopyTo(preimage);
        BinaryPrimitives.WriteUInt64LittleEndian(preimage[4..], epoch);
        mix.Bytes.CopyTo(preimage[12..]);
        return new Hash256(SHA256.HashData(preimage));
    }

    public static bool IsActiveValidator(this Validator validator, ulong epoch) =>
        validator.ActivationEpoch <= epoch && epoch < validator.ExitEpoch;

    public static bool IsSlashableValidator(this Validator validator, ulong epoch) =>
        !validator.Slashed && validator.ActivationEpoch <= epoch && epoch < validator.WithdrawableEpoch;

    /// <summary>Returns whether the validator is eligible to join the activation queue (EIP-7251 rules).</summary>
    public static bool IsEligibleForActivationQueue(this Validator validator) =>
        validator.ActivationEligibilityEpoch == Presets.FarFutureEpoch
        && validator.EffectiveBalance >= Presets.MinActivationBalance;

    public static bool IsEligibleForActivation(this BeaconStateFulu state, Validator validator) =>
        validator.ActivationEligibilityEpoch <= state.FinalizedCheckpoint!.Epoch
        && validator.ActivationEpoch == Presets.FarFutureEpoch;

    /// <summary>Returns whether two attestation votes are slashable (double or surround vote).</summary>
    public static bool IsSlashableAttestationData(AttestationData data1, AttestationData data2) =>
        (!AttestationDataEquals(data1, data2) && data1.Target!.Epoch == data2.Target!.Epoch)
        || (data1.Source!.Epoch < data2.Source!.Epoch && data2.Target!.Epoch < data1.Target!.Epoch);

    private static bool AttestationDataEquals(AttestationData a, AttestationData b) =>
        a.Slot == b.Slot
        && a.Index == b.Index
        && a.BeaconBlockRoot == b.BeaconBlockRoot
        && a.Source!.Epoch == b.Source!.Epoch && a.Source.Root == b.Source.Root
        && a.Target!.Epoch == b.Target!.Epoch && a.Target.Root == b.Target.Root;

    public static bool HasCompoundingWithdrawalCredential(this Validator validator) =>
        validator.WithdrawalCredentials!.Bytes[0] == Presets.CompoundingWithdrawalPrefix;

    public static bool HasEth1WithdrawalCredential(this Validator validator) =>
        validator.WithdrawalCredentials!.Bytes[0] == Presets.EthWithdrawalPrefix;

    /// <summary>Returns whether the validator has a 0x01 or 0x02 prefixed withdrawal credential.</summary>
    public static bool HasExecutionWithdrawalCredential(this Validator validator) =>
        validator.HasCompoundingWithdrawalCredential() || validator.HasEth1WithdrawalCredential();

    /// <summary>Returns the EIP-7251 max effective balance: 2048 ETH for compounding credentials, 32 ETH otherwise.</summary>
    public static ulong GetMaxEffectiveBalance(this Validator validator) =>
        validator.HasCompoundingWithdrawalCredential() ? Presets.MaxEffectiveBalanceElectra : Presets.MinActivationBalance;

    public static bool IsFullyWithdrawableValidator(this Validator validator, ulong balance, ulong epoch) =>
        validator.HasExecutionWithdrawalCredential() && validator.WithdrawableEpoch <= epoch && balance > 0;

    public static bool IsPartiallyWithdrawableValidator(this Validator validator, ulong balance)
    {
        ulong maxEffectiveBalance = validator.GetMaxEffectiveBalance();
        return validator.HasExecutionWithdrawalCredential()
            && validator.EffectiveBalance == maxEffectiveBalance
            && balance > maxEffectiveBalance;
    }

    /// <summary>Returns the total amount queued in <c>pending_partial_withdrawals</c> for a validator.</summary>
    public static ulong GetPendingBalanceToWithdraw(this BeaconStateFulu state, int validatorIndex)
    {
        ulong pendingBalance = 0;
        foreach (PendingPartialWithdrawal withdrawal in state.PendingPartialWithdrawals!)
        {
            if (withdrawal.ValidatorIndex == (ulong)validatorIndex)
                pendingBalance += withdrawal.Amount;
        }
        return pendingBalance;
    }

    /// <summary>Returns the indices of validators active in <paramref name="epoch"/>, in ascending order.</summary>
    public static int[] GetActiveValidatorIndices(this BeaconStateFulu state, ulong epoch)
    {
        Validator[] validators = state.Validators!;
        List<int> active = new(validators.Length);
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].IsActiveValidator(epoch))
                active.Add(i);
        }
        return active.ToArray();
    }

    /// <summary>Returns <c>max(EFFECTIVE_BALANCE_INCREMENT, sum of effective balances)</c> of the given validators.</summary>
    public static ulong GetTotalBalance(this BeaconStateFulu state, IEnumerable<int> indices)
    {
        ulong total = 0;
        foreach (int index in indices)
        {
            total += state.Validators![index].EffectiveBalance;
        }
        return Math.Max(Presets.EffectiveBalanceIncrement, total);
    }

    /// <summary>Returns the total effective balance of active validators, memoized per epoch in <paramref name="cache"/>.</summary>
    public static ulong GetTotalActiveBalance(this BeaconStateFulu state, EpochCache cache) =>
        cache.GetTotalActiveBalance(state);

    /// <summary>Returns the pre-Electra validator-count churn limit (spec <c>get_validator_churn_limit</c>).</summary>
    /// <remarks>Not used by the Electra transition itself; superseded by <see cref="GetBalanceChurnLimit"/>.</remarks>
    public static ulong GetValidatorChurnLimit(this BeaconStateFulu state, EpochCache cache)
    {
        ulong activeValidatorCount = (ulong)cache.GetCommitteeCache(state, state.GetCurrentEpoch()).ActiveValidatorCount;
        return Math.Max(Presets.MinPerEpochChurnLimit, activeValidatorCount / Presets.ChurnLimitQuotient);
    }

    /// <summary>Returns the pre-Electra activation churn limit (spec <c>get_validator_activation_churn_limit</c>).</summary>
    public static ulong GetValidatorActivationChurnLimit(this BeaconStateFulu state, EpochCache cache) =>
        Math.Min(Presets.MaxPerEpochActivationChurnLimit, state.GetValidatorChurnLimit(cache));

    /// <summary>Returns the EIP-7251 balance churn limit for the current epoch, in Gwei.</summary>
    public static ulong GetBalanceChurnLimit(this BeaconStateFulu state, EpochCache cache)
    {
        ulong churn = Math.Max(Presets.MinPerEpochChurnLimitElectra, state.GetTotalActiveBalance(cache) / Presets.ChurnLimitQuotient);
        return churn - churn % Presets.EffectiveBalanceIncrement;
    }

    /// <summary>Returns the EIP-7251 churn limit dedicated to activations and exits, in Gwei.</summary>
    public static ulong GetActivationExitChurnLimit(this BeaconStateFulu state, EpochCache cache) =>
        Math.Min(Presets.MaxPerEpochActivationExitChurnLimit, state.GetBalanceChurnLimit(cache));

    /// <summary>Returns the EIP-7251 churn limit dedicated to consolidations, in Gwei.</summary>
    public static ulong GetConsolidationChurnLimit(this BeaconStateFulu state, EpochCache cache) =>
        state.GetBalanceChurnLimit(cache) - state.GetActivationExitChurnLimit(cache);

    /// <summary>Returns the signing domain for the given domain type at <paramref name="epoch"/> (current epoch when null).</summary>
    /// <remarks>
    /// Uses the state's fork and genesis validators root. Deposits are the exception: their domain
    /// is fork-agnostic, so use
    /// <c>Domains.ComputeDomain(DomainType.Deposit, Presets.GenesisForkVersion, Hash256.Zero)</c> directly.
    /// </remarks>
    public static Hash256 GetDomain(this BeaconStateFulu state, ReadOnlySpan<byte> domainType, ulong? epoch = null)
    {
        ulong atEpoch = epoch ?? state.GetCurrentEpoch();
        Fork fork = state.Fork!;
        byte[] forkVersion = atEpoch < fork.Epoch ? fork.PreviousVersion! : fork.CurrentVersion!;
        return Domains.ComputeDomain(domainType, forkVersion, state.GenesisValidatorsRoot!);
    }

    /// <summary>Returns the proposer index at the state's current slot (Fulu <c>get_beacon_proposer_index</c>, EIP-7917).</summary>
    public static ulong GetBeaconProposerIndex(this BeaconStateFulu state) =>
        state.ProposerLookahead![(int)(state.Slot % Presets.SlotsPerEpoch)];

    /// <summary>Returns the proposer index at <paramref name="slot"/> from the EIP-7917 lookahead.</summary>
    /// <remarks>The lookahead covers the current and next epoch; other slots throw.</remarks>
    public static ulong GetBeaconProposerIndex(this BeaconStateFulu state, ulong slot)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        ulong epoch = ComputeEpochAtSlot(slot);
        if (epoch != currentEpoch && epoch != currentEpoch + 1)
            throw new BeaconStateException($"Proposer lookahead does not cover slot {slot} at state slot {state.Slot}");
        int offset = epoch == currentEpoch ? 0 : (int)Presets.SlotsPerEpoch;
        return state.ProposerLookahead![offset + (int)(slot % Presets.SlotsPerEpoch)];
    }

    /// <summary>
    /// Samples a proposer from <paramref name="indices"/> using Electra balance-weighted selection
    /// (spec <c>compute_proposer_index</c>, weighted by <c>MAX_EFFECTIVE_BALANCE_ELECTRA</c> with
    /// 16-bit random values).
    /// </summary>
    public static int ComputeProposerIndex(this BeaconStateFulu state, ReadOnlySpan<int> indices, ReadOnlySpan<byte> seed)
    {
        if (indices.IsEmpty)
            throw new BeaconStateException("Cannot compute a proposer from an empty validator set");

        Span<byte> preimage = stackalloc byte[32 + 8];
        Span<byte> randomBytes = stackalloc byte[32];
        seed[..32].CopyTo(preimage);

        ulong i = 0;
        while (true)
        {
            int shuffledIndex = SwapOrNotShuffle.ComputeShuffledIndex((int)(i % (ulong)indices.Length), indices.Length, seed);
            int candidateIndex = indices[shuffledIndex];
            BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], i / 16);
            SHA256.HashData(preimage, randomBytes);
            ulong randomValue = BinaryPrimitives.ReadUInt16LittleEndian(randomBytes[(int)(i % 16 * 2)..]);
            ulong effectiveBalance = state.Validators![candidateIndex].EffectiveBalance;
            if (effectiveBalance * MaxRandomValue >= Presets.MaxEffectiveBalanceElectra * randomValue)
                return candidateIndex;
            i++;
        }
    }

    /// <summary>
    /// Computes the proposer index for every slot of <paramref name="epoch"/> (spec
    /// <c>compute_proposer_indices</c>) — used to fill the EIP-7917 lookahead at epoch transitions.
    /// </summary>
    public static ulong[] ComputeProposerIndices(this BeaconStateFulu state, ulong epoch)
    {
        Hash256 epochSeed = state.GetSeed(epoch, DomainType.BeaconProposer);
        int[] indices = state.GetActiveValidatorIndices(epoch);

        Span<byte> preimage = stackalloc byte[32 + 8];
        epochSeed.Bytes.CopyTo(preimage);

        ulong startSlot = ComputeStartSlotAtEpoch(epoch);
        ulong[] proposerIndices = new ulong[Presets.SlotsPerEpoch];
        for (int i = 0; i < proposerIndices.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], startSlot + (ulong)i);
            proposerIndices[i] = (ulong)state.ComputeProposerIndex(indices, SHA256.HashData(preimage));
        }
        return proposerIndices;
    }

    /// <summary>
    /// Returns the root of the last block that could influence the attester shuffling for
    /// <paramref name="epoch"/> (the block at the last slot of <c>epoch - MIN_SEED_LOOKAHEAD - 1</c>,
    /// which finalizes the RANDAO mix used by <see cref="GetSeed"/>). Used as a fork-safe
    /// committee-cache key.
    /// </summary>
    /// <remarks>Returns <see cref="Hash256.Zero"/> near genesis where no decision block exists.</remarks>
    public static Hash256 GetShufflingDecisionRoot(this BeaconStateFulu state, ulong epoch)
    {
        ulong decisionSlot = epoch >= Presets.MinSeedLookahead
            ? ComputeStartSlotAtEpoch(epoch - Presets.MinSeedLookahead)
            : 0;
        if (decisionSlot > 0)
            decisionSlot--;
        return state.Slot == 0 ? Hash256.Zero : state.GetBlockRootAtSlot(decisionSlot);
    }

    /// <summary>
    /// Returns the validator indices attesting in an Electra (EIP-7549) aggregate, in ascending
    /// order, validating the committee/aggregation bit structure as in <c>process_attestation</c>.
    /// </summary>
    /// <param name="committees">Committee cache built for the epoch of <c>attestation.Data.Slot</c>.</param>
    /// <exception cref="BeaconStateException">The attestation's bitfields are inconsistent with the committees.</exception>
    public static ulong[] GetAttestingIndices(this BeaconStateFulu state, Attestation attestation, CommitteeCache committees)
    {
        ulong slot = attestation.Data!.Slot;
        BitArray committeeBits = attestation.CommitteeBits!;
        BitArray aggregationBits = attestation.AggregationBits!;

        List<ulong> attestingIndices = [];
        int committeeOffset = 0;
        for (int committeeIndex = 0; committeeIndex < committeeBits.Length; committeeIndex++)
        {
            if (!committeeBits[committeeIndex])
                continue;
            if (committeeIndex >= committees.CommitteesPerSlot)
                throw new BeaconStateException($"Committee index {committeeIndex} out of range ({committees.CommitteesPerSlot} committees per slot)");

            ReadOnlySpan<int> committee = committees.GetBeaconCommittee(slot, committeeIndex);
            int attestersInCommittee = 0;
            for (int i = 0; i < committee.Length; i++)
            {
                int bitIndex = committeeOffset + i;
                if (bitIndex < aggregationBits.Length && aggregationBits[bitIndex])
                {
                    attestingIndices.Add((ulong)committee[i]);
                    attestersInCommittee++;
                }
            }
            if (attestersInCommittee == 0)
                throw new BeaconStateException($"Committee {committeeIndex} has no attesters set");
            committeeOffset += committee.Length;
        }

        if (committeeOffset != aggregationBits.Length)
            throw new BeaconStateException($"Aggregation bits length {aggregationBits.Length} does not match participant count {committeeOffset}");

        attestingIndices.Sort();
        return attestingIndices.ToArray();
    }

    /// <summary>Converts an Electra attestation to its indexed, signature-verifiable form.</summary>
    /// <param name="committees">Committee cache built for the epoch of <c>attestation.Data.Slot</c>.</param>
    public static IndexedAttestation GetIndexedAttestation(this BeaconStateFulu state, Attestation attestation, CommitteeCache committees) =>
        new()
        {
            AttestingIndices = state.GetAttestingIndices(attestation, committees),
            Data = attestation.Data,
            Signature = attestation.Signature,
        };
}
