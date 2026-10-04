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
#if GLOAS
using AccessorState = Nethermind.BeaconChain.Types.BeaconStateGloas;
using LegacyState = Nethermind.BeaconChain.Types.BeaconStateGloas;
using AttestationType = Nethermind.BeaconChain.Types.AttestationGloas;
using IndexedAttestationType = Nethermind.BeaconChain.Types.IndexedAttestationGloas;
#else
using AccessorState = Nethermind.BeaconChain.Types.BeaconStateFulu;
using LegacyState = Nethermind.BeaconChain.Types.BeaconStateElectra;
using AttestationType = Nethermind.BeaconChain.Types.Attestation;
using IndexedAttestationType = Nethermind.BeaconChain.Types.IndexedAttestation;
#endif

namespace Nethermind.BeaconChain.StateTransition;

#if GLOAS
public static partial class GloasStateAccessors
#else
public static partial class BeaconStateAccessors
#endif
{
    public static partial ulong GetCurrentEpoch(this LegacyState state) => BeaconStateAccessors.ComputeEpochAtSlot(state.Slot);

    public static partial ulong GetPreviousEpoch(this AccessorState state)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        return currentEpoch == Presets.GenesisEpoch ? Presets.GenesisEpoch : currentEpoch - 1;
    }

    /// <summary>Returns the block root at the start slot of a recent <paramref name="epoch"/> (spec <c>get_block_root</c>).</summary>
    public static partial Hash256 GetBlockRoot(this AccessorState state, ulong epoch) =>
        state.GetBlockRootAtSlot(BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch));

    /// <summary>Returns the block root at a recent <paramref name="slot"/> (spec <c>get_block_root_at_slot</c>).</summary>
    /// <exception cref="BeaconStateException">The slot is not within the last <c>SLOTS_PER_HISTORICAL_ROOT</c> slots.</exception>
    public static partial Hash256 GetBlockRootAtSlot(this AccessorState state, ulong slot) =>
        BeaconStateAccessors.GetBlockRootAtSlot(state.Slot, state.BlockRoots!, slot);

    /// <summary>Spec <c>get_randao_mix</c>: returns the randao mix recorded for <paramref name="epoch"/>.</summary>
    /// <remarks>
    /// <c>get_randao_mix</c> is only ever called (directly, or via <see cref="GetSeed"/>) with an
    /// epoch within <see cref="Presets.EpochsPerHistoricalVector"/> of the state's current epoch;
    /// outside that window the vector slot has been overwritten by (or never yet holds) a different
    /// epoch's mix, so refuse rather than silently return it.
    /// </remarks>
    /// <exception cref="BeaconStateException">The epoch is outside the historical-vector window.</exception>
    public static partial Hash256 GetRandaoMix(this LegacyState state, ulong epoch)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        ulong age = currentEpoch - epoch; // unsigned wraparound rejects a later epoch, except one so near 2^64 that the wrapped age falls inside the window
        if (age >= Presets.EpochsPerHistoricalVector)
            throw new BeaconStateException(
                $"Randao mix for epoch {epoch} is not available: state is at epoch {currentEpoch}, " +
                $"window covers the {Presets.EpochsPerHistoricalVector} epochs up to and including it");
        return state.RandaoMixes![(int)(epoch % Presets.EpochsPerHistoricalVector)];
    }

    /// <summary>Returns the shuffling seed for <paramref name="epoch"/> and the given domain type.</summary>
    public static partial Hash256 GetSeed(this LegacyState state, ulong epoch, ReadOnlySpan<byte> domainType)
    {
        // Unsigned wraparound (not "+ EpochsPerHistoricalVector"): GetRandaoMix now bounds-checks the
        // raw epoch against the state's current epoch, so inflating it by a vector length here would
        // push a perfectly valid call outside that window instead of just fixing up the mod index.
        Hash256 mix = state.GetRandaoMix(epoch - Presets.MinSeedLookahead - 1);
        Span<byte> preimage = stackalloc byte[4 + 8 + 32];
        domainType.CopyTo(preimage);
        BinaryPrimitives.WriteUInt64LittleEndian(preimage[4..], epoch);
        mix.Bytes.CopyTo(preimage[12..]);
        return new Hash256(SHA256.HashData(preimage));
    }

    /// <summary>Returns the total amount queued in <c>pending_partial_withdrawals</c> for a validator.</summary>
    public static partial ulong GetPendingBalanceToWithdraw(this AccessorState state, int validatorIndex)
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
    public static partial int[] GetActiveValidatorIndices(this LegacyState state, ulong epoch)
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
    public static partial ulong GetTotalBalance(this AccessorState state, IEnumerable<int> indices)
    {
        ulong total = 0;
        foreach (int index in indices)
        {
            total += state.Validators![index].EffectiveBalance;
        }
        return Math.Max(Presets.EffectiveBalanceIncrement, total);
    }

    /// <summary>Returns the total effective balance of active validators, memoized per epoch in <paramref name="cache"/>.</summary>
    public static partial ulong GetTotalActiveBalance(this AccessorState state, EpochCache cache) =>
        cache.GetTotalActiveBalance(state);

    /// <summary>Spec <c>get_base_reward_per_increment</c> (Altair).</summary>
    public static partial ulong GetBaseRewardPerIncrement(this AccessorState state, EpochCache cache) =>
        Presets.EffectiveBalanceIncrement * Presets.BaseRewardFactor / BeaconStateAccessors.IntegerSquareRoot(state.GetTotalActiveBalance(cache));

    /// <summary>Returns the signing domain for the given domain type at <paramref name="epoch"/> (current epoch when null).</summary>
    /// <remarks>
    /// Uses the state's fork and genesis validators root. Deposits are the exception: their domain
    /// is fork-agnostic, so use
    /// <c>Domains.ComputeDomain(DomainType.Deposit, BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot).GenesisForkVersion, Hash256.Zero)</c> directly.
    /// </remarks>
    public static partial Hash256 GetDomain(this AccessorState state, ReadOnlySpan<byte> domainType, ulong? epoch)
    {
        ulong atEpoch = epoch ?? state.GetCurrentEpoch();
        Fork fork = state.Fork!;
        byte[] forkVersion = atEpoch < fork.Epoch ? fork.PreviousVersion! : fork.CurrentVersion!;
        return Domains.ComputeDomain(domainType, forkVersion, state.GenesisValidatorsRoot!);
    }

    /// <summary>Returns the proposer index at the state's current slot (Fulu <c>get_beacon_proposer_index</c>, EIP-7917).</summary>
    public static partial ulong GetBeaconProposerIndex(this AccessorState state) =>
        state.ProposerLookahead![(int)(state.Slot % Presets.SlotsPerEpoch)];

    /// <summary>
    /// Returns the root of the last block that could influence the attester shuffling for
    /// <paramref name="epoch"/> (the block at the last slot of <c>epoch - MIN_SEED_LOOKAHEAD - 1</c>,
    /// which finalizes the RANDAO mix used by <see cref="GetSeed"/>). Used as a fork-safe
    /// committee-cache key.
    /// </summary>
    /// <remarks>Returns <see cref="Hash256.Zero"/> near genesis where no decision block exists.</remarks>
    public static partial Hash256 GetShufflingDecisionRoot(this AccessorState state, ulong epoch)
    {
        ulong decisionSlot = epoch >= Presets.MinSeedLookahead
            ? BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch - Presets.MinSeedLookahead)
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
    public static partial ulong[] GetAttestingIndices(this AccessorState state, AttestationType attestation, CommitteeCache committees)
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
    public static partial IndexedAttestationType GetIndexedAttestation(this AccessorState state, AttestationType attestation, CommitteeCache committees) =>
        new()
        {
            AttestingIndices = state.GetAttestingIndices(attestation, committees),
            Data = attestation.Data,
            Signature = attestation.Signature,
        };

}
