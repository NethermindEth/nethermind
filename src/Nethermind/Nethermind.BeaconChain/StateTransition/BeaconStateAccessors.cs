// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
public static partial class BeaconStateAccessors
{
    /// <summary>Electra <c>MAX_RANDOM_VALUE</c>: random values are 16-bit from this fork on.</summary>
    internal const ulong MaxRandomValue = (1 << 16) - 1;

    public static ulong ComputeEpochAtSlot(ulong slot) => slot / Presets.SlotsPerEpoch;

    /// <summary>The spec's <c>is_aggregator</c> (validator.md) for a committee of <paramref name="committeeSize"/> members.</summary>
    internal static bool IsAggregator(int committeeSize, BlsSignature selectionProof)
    {
        ulong modulo = Math.Max(1, (ulong)committeeSize / Presets.TargetAggregatorsPerCommittee);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(selectionProof.Bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash) % modulo == 0;
    }

    public static ulong ComputeStartSlotAtEpoch(ulong epoch) => epoch * Presets.SlotsPerEpoch;

    /// <summary>Returns the epoch at which an activation or exit triggered in <paramref name="epoch"/> takes effect.</summary>
    public static ulong ComputeActivationExitEpoch(ulong epoch) => epoch + 1 + Presets.MaxSeedLookahead;

    public static partial ulong GetCurrentEpoch(this BeaconStateElectra state);

    public static partial ulong GetPreviousEpoch(this BeaconStateFulu state);

    public static partial Hash256 GetBlockRoot(this BeaconStateFulu state, ulong epoch);

    public static partial Hash256 GetBlockRootAtSlot(this BeaconStateFulu state, ulong slot);

    internal static Hash256 GetBlockRootAtSlot(ulong stateSlot, Hash256[] blockRoots, ulong slot)
    {
        if (!(slot < stateSlot && stateSlot <= slot + Presets.SlotsPerHistoricalRoot))
            throw new BeaconStateException($"Block root for slot {slot} is not available at state slot {stateSlot}");
        return blockRoots[(int)(slot % Presets.SlotsPerHistoricalRoot)];
    }

    public static partial Hash256 GetRandaoMix(this BeaconStateElectra state, ulong epoch);

    public static partial Hash256 GetSeed(this BeaconStateElectra state, ulong epoch, ReadOnlySpan<byte> domainType);

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

    public static partial ulong GetPendingBalanceToWithdraw(this BeaconStateFulu state, int validatorIndex);

    public static partial int[] GetActiveValidatorIndices(this BeaconStateElectra state, ulong epoch);

    public static partial ulong GetTotalBalance(this BeaconStateFulu state, IEnumerable<int> indices);

    public static partial ulong GetTotalActiveBalance(this BeaconStateFulu state, EpochCache cache);

    public static partial ulong GetBaseRewardPerIncrement(this BeaconStateFulu state, EpochCache cache);

    /// <summary>Spec <c>integer_squareroot</c> (with the Deneb special case for <c>2^64 - 1</c>).</summary>
    public static ulong IntegerSquareRoot(ulong n)
    {
        if (n == ulong.MaxValue)
            return uint.MaxValue; // The Newton step below would overflow; the root of 2^64 - 1 is known.
        ulong x = n;
        ulong y = (x + 1) / 2;
        while (y < x)
        {
            x = y;
            y = (x + n / x) / 2;
        }
        return x;
    }

    /// <summary>Spec <c>has_flag</c>: whether a participation byte has the given flag index set.</summary>
    public static bool HasParticipationFlag(byte participation, int flagIndex) =>
        (participation & (1 << flagIndex)) != 0;

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

    public static partial Hash256 GetDomain(this BeaconStateFulu state, ReadOnlySpan<byte> domainType, ulong? epoch = null);

    public static partial ulong GetBeaconProposerIndex(this BeaconStateFulu state);

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

    public static partial Hash256 GetShufflingDecisionRoot(this BeaconStateFulu state, ulong epoch);

    public static partial ulong[] GetAttestingIndices(this BeaconStateFulu state, Attestation attestation, CommitteeCache committees);

    public static partial IndexedAttestation GetIndexedAttestation(this BeaconStateFulu state, Attestation attestation, CommitteeCache committees);
}
