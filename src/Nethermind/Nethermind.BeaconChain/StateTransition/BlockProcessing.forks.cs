// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
#if !GLOAS
using System.Collections;
#endif
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.StateTransition;

public static partial class BlockProcessing
{

    private static partial ulong TotalWithdrawn(List<Withdrawal> withdrawals, ulong validatorIndex)
    {
        ulong total = 0;
        foreach (Withdrawal withdrawal in withdrawals)
        {
            if (withdrawal.ValidatorIndex == validatorIndex)
                total += withdrawal.Amount;
        }
        return total;
    }

    private static partial bool HeaderEquals(BeaconBlockHeader a, BeaconBlockHeader b) =>
        a.Slot == b.Slot
        && a.ProposerIndex == b.ProposerIndex
        && a.ParentRoot == b.ParentRoot
        && a.StateRoot == b.StateRoot
        && a.BodyRoot == b.BodyRoot;

    /// <summary>Spec <c>is_valid_switch_to_compounding_request</c>.</summary>
    private static partial bool IsValidSwitchToCompoundingRequest(ForkState state, ConsolidationRequest request)
    {
        // Switching to compounding requires source and target to be the same validator.
        if (request.SourcePubkey != request.TargetPubkey)
            return false;
        if (FindValidatorIndex(state, request.SourcePubkey) is not int sourceIndex)
            return false;

        Validator sourceValidator = state.Validators![sourceIndex];
        if (!sourceValidator.WithdrawalCredentials!.Bytes[12..].SequenceEqual(request.SourceAddress!.Bytes))
            return false;
        if (!sourceValidator.HasEth1WithdrawalCredential())
            return false;
        if (!sourceValidator.IsActiveValidator(state.GetCurrentEpoch()))
            return false;
        if (sourceValidator.ExitEpoch != Presets.FarFutureEpoch)
            return false;

        return true;
    }

    /// <summary>Returns the index of the validator with <paramref name="pubkey"/>, or null when unregistered.</summary>
    private static partial int? FindValidatorIndex(ForkState state, BlsPublicKey pubkey)
    {
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].Pubkey == pubkey)
                return i;
        }
        return null;
    }

    /// <summary>Spec <c>process_attester_slashing</c>: slashes every still-slashable validator attesting in both votes.</summary>
    public static partial void ProcessAttesterSlashing(ForkState state, ForkAttesterSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures, BlockSignatureBatch? batch)
    {
        const string invalidAttestation1 = "Attester slashing attestation 1 is invalid";
        const string invalidAttestation2 = "Attester slashing attestation 2 is invalid";
        ForkIndexedAttestation attestation1 = slashing.Attestation1!;
        ForkIndexedAttestation attestation2 = slashing.Attestation2!;

        if (!BeaconStateAccessors.IsSlashableAttestationData(attestation1.Data!, attestation2.Data!))
            throw new BeaconStateException("Attester slashing votes are not slashable");
        if (!IsValidIndexedAttestation(state, attestation1, pubkeys, verifySignatures, batch?.Defer(invalidAttestation1)))
            throw new BeaconStateException(invalidAttestation1);
        if (!IsValidIndexedAttestation(state, attestation2, pubkeys, verifySignatures, batch?.Defer(invalidAttestation2)))
            throw new BeaconStateException(invalidAttestation2);

        ulong currentEpoch = state.GetCurrentEpoch();
        HashSet<ulong> indices2 = [.. attestation2.AttestingIndices!];
        bool slashedAny = false;
        // attestation_1's indices are validated ascending, so the intersection is visited in sorted order.
        foreach (ulong index in attestation1.AttestingIndices!)
        {
            if (indices2.Contains(index) && state.Validators![(int)index].IsSlashableValidator(currentEpoch))
            {
                state.SlashValidator((int)index, cache);
                slashedAny = true;
            }
        }
        if (!slashedAny)
            throw new BeaconStateException("Attester slashing slashed no validator");
    }

    /// <summary>Spec <c>process_voluntary_exit</c> (Electra).</summary>
    public static partial void ProcessVoluntaryExit(ForkState state, SignedVoluntaryExit signedExit, EpochCache cache, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
    {
        VoluntaryExit exit = signedExit.Message!;
        if (exit.ValidatorIndex >= (ulong)state.Validators!.Length)
            throw new BeaconStateException($"Voluntary exit validator index {exit.ValidatorIndex} is out of range");

        Validator validator = state.Validators[(int)exit.ValidatorIndex];
        ulong currentEpoch = state.GetCurrentEpoch();
        if (!validator.IsActiveValidator(currentEpoch))
            throw new BeaconStateException($"Exiting validator {exit.ValidatorIndex} is not active");
        if (validator.ExitEpoch != Presets.FarFutureEpoch)
            throw new BeaconStateException($"Validator {exit.ValidatorIndex} already initiated an exit");
        if (currentEpoch < exit.Epoch)
            throw new BeaconStateException($"Voluntary exit is not valid before epoch {exit.Epoch}");
        if (currentEpoch < validator.ActivationEpoch + Presets.ShardCommitteePeriod)
            throw new BeaconStateException($"Validator {exit.ValidatorIndex} has not been active long enough");
        // [New in Electra:EIP7251] Only exit when no withdrawals are pending in the queue.
        if (state.GetPendingBalanceToWithdraw((int)exit.ValidatorIndex) != 0)
            throw new BeaconStateException($"Validator {exit.ValidatorIndex} has pending partial withdrawals");
        const string invalidSignature = "Invalid voluntary exit signature";
        if (verifySignature && !ForkSignatureSets.VerifyVoluntaryExit(state, signedExit, pubkeys, batch?.Defer(invalidSignature)))
            throw new BeaconStateException(invalidSignature);

        state.InitiateValidatorExit((int)exit.ValidatorIndex, cache);
    }

    /// <summary>Spec <c>process_bls_to_execution_change</c> (Capella).</summary>
    public static partial void ProcessBlsToExecutionChange(ForkState state, SignedBlsToExecutionChange signedChange, bool verifySignature, BlockSignatureBatch? batch)
    {
        BlsToExecutionChange change = signedChange.Message!;
        if (change.ValidatorIndex >= (ulong)state.Validators!.Length)
            throw new BeaconStateException($"BLS change validator index {change.ValidatorIndex} is out of range");

        Validator validator = state.Validators[(int)change.ValidatorIndex];
        ReadOnlySpan<byte> credentials = validator.WithdrawalCredentials!.Bytes;
        if (credentials[0] != Presets.BlsWithdrawalPrefix)
            throw new BeaconStateException($"Validator {change.ValidatorIndex} does not have BLS withdrawal credentials");
        if (!credentials[1..].SequenceEqual(SHA256.HashData(change.FromBlsPubkey.Bytes).AsSpan(1)))
            throw new BeaconStateException("BLS change pubkey does not match the withdrawal credentials");
        const string invalidSignature = "Invalid BLS to execution change signature";
        if (verifySignature && !ForkSignatureSets.VerifyBlsToExecutionChange(state, signedChange, batch?.Defer(invalidSignature)))
            throw new BeaconStateException(invalidSignature);

        Span<byte> newCredentials = stackalloc byte[32];
        newCredentials[0] = Presets.EthWithdrawalPrefix;
        change.ToExecutionAddress!.Bytes.CopyTo(newCredentials[12..]);
        Validator updated = validator.Clone();
        updated.WithdrawalCredentials = new Hash256(newCredentials);
        state.Validators[(int)change.ValidatorIndex] = updated;
    }

    /// <summary>Spec <c>process_deposit_request</c> (EIP-6110).</summary>
#if GLOAS
    internal static partial void ProcessDepositRequest(ForkState state, DepositRequest request)
#else
    public static partial void ProcessDepositRequest(ForkState state, DepositRequest request)
#endif
    {
        if (state.DepositRequestsStartIndex == Presets.UnsetDepositRequestsStartIndex)
            state.DepositRequestsStartIndex = request.Index;

        state.PendingDeposits = [.. state.PendingDeposits!, new PendingDeposit
        {
            Pubkey = request.Pubkey,
            WithdrawalCredentials = request.WithdrawalCredentials,
            Amount = request.Amount,
            Signature = request.Signature,
            Slot = state.Slot,
        }];
    }

    /// <summary>
    /// Spec <c>process_withdrawal_request</c> (EIP-7002/EIP-7251). Invalid requests are ignored,
    /// never invalidating the block.
    /// </summary>
#if GLOAS
    internal static partial void ProcessWithdrawalRequest(ForkState state, WithdrawalRequest request, EpochCache cache)
#else
    public static partial void ProcessWithdrawalRequest(ForkState state, WithdrawalRequest request, EpochCache cache)
#endif
    {
        bool isFullExitRequest = request.Amount == Presets.FullExitRequestAmount;

        // When the partial withdrawal queue is full, only full exits are processed.
        if (state.PendingPartialWithdrawals!.Length == Presets.PendingPartialWithdrawalsLimit && !isFullExitRequest)
            return;

        if (FindValidatorIndex(state, request.ValidatorPubkey) is not int index)
            return;
        Validator validator = state.Validators![index];

        bool isCorrectSourceAddress = validator.WithdrawalCredentials!.Bytes[12..].SequenceEqual(request.SourceAddress!.Bytes);
        if (!validator.HasExecutionWithdrawalCredential() || !isCorrectSourceAddress)
            return;
        ulong currentEpoch = state.GetCurrentEpoch();
        if (!validator.IsActiveValidator(currentEpoch))
            return;
        if (validator.ExitEpoch != Presets.FarFutureEpoch)
            return;
        if (currentEpoch < validator.ActivationEpoch + Presets.ShardCommitteePeriod)
            return;

        ulong pendingBalanceToWithdraw = state.GetPendingBalanceToWithdraw(index);

        if (isFullExitRequest)
        {
            // Only exit the validator when it has no withdrawals pending in the queue.
            if (pendingBalanceToWithdraw == 0)
                state.InitiateValidatorExit(index, cache);
            return;
        }

        bool hasSufficientEffectiveBalance = validator.EffectiveBalance >= Presets.MinActivationBalance;
        bool hasExcessBalance = state.Balances![index] > Presets.MinActivationBalance + pendingBalanceToWithdraw;

        // Only compounding credentials allow partial withdrawals.
        if (validator.HasCompoundingWithdrawalCredential() && hasSufficientEffectiveBalance && hasExcessBalance)
        {
            ulong toWithdraw = Math.Min(state.Balances[index] - Presets.MinActivationBalance - pendingBalanceToWithdraw, request.Amount);
            ulong exitQueueEpoch = state.ComputeExitEpochAndUpdateChurn(toWithdraw, cache);
            state.PendingPartialWithdrawals = [.. state.PendingPartialWithdrawals, new PendingPartialWithdrawal
            {
                ValidatorIndex = (ulong)index,
                Amount = toWithdraw,
                WithdrawableEpoch = exitQueueEpoch + Presets.MinValidatorWithdrawabilityDelay,
            }];
        }
    }

    /// <summary>
    /// Spec <c>process_consolidation_request</c> (EIP-7251): a self-consolidation switches the
    /// validator to compounding credentials; otherwise the source's balance is consolidated into
    /// the target through the consolidation churn. Invalid requests are ignored.
    /// </summary>
#if GLOAS
    internal static partial void ProcessConsolidationRequest(ForkState state, ConsolidationRequest request, EpochCache cache)
#else
    public static partial void ProcessConsolidationRequest(ForkState state, ConsolidationRequest request, EpochCache cache)
#endif
    {
        if (IsValidSwitchToCompoundingRequest(state, request))
        {
            SwitchToCompoundingValidator(state, FindValidatorIndex(state, request.SourcePubkey)!.Value);
            return;
        }

        // A consolidation with source == target cannot be used as an exit.
        if (request.SourcePubkey == request.TargetPubkey)
            return;
        if (state.PendingConsolidations!.Length == Presets.PendingConsolidationsLimit)
            return;
        if (state.GetConsolidationChurnLimit(cache) <= Presets.MinActivationBalance)
            return;

        if (FindValidatorIndex(state, request.SourcePubkey) is not int sourceIndex)
            return;
        if (FindValidatorIndex(state, request.TargetPubkey) is not int targetIndex)
            return;
        Validator sourceValidator = state.Validators![sourceIndex];
        Validator targetValidator = state.Validators[targetIndex];

        bool isCorrectSourceAddress = sourceValidator.WithdrawalCredentials!.Bytes[12..].SequenceEqual(request.SourceAddress!.Bytes);
        if (!sourceValidator.HasExecutionWithdrawalCredential() || !isCorrectSourceAddress)
            return;
        if (!targetValidator.HasCompoundingWithdrawalCredential())
            return;
        ulong currentEpoch = state.GetCurrentEpoch();
        if (!sourceValidator.IsActiveValidator(currentEpoch) || !targetValidator.IsActiveValidator(currentEpoch))
            return;
        if (sourceValidator.ExitEpoch != Presets.FarFutureEpoch || targetValidator.ExitEpoch != Presets.FarFutureEpoch)
            return;
        if (currentEpoch < sourceValidator.ActivationEpoch + Presets.ShardCommitteePeriod)
            return;
        if (state.GetPendingBalanceToWithdraw(sourceIndex) > 0)
            return;

        Validator updatedSource = sourceValidator.Clone();
        updatedSource.ExitEpoch = state.ComputeConsolidationEpochAndUpdateChurn(sourceValidator.EffectiveBalance, cache);
        updatedSource.WithdrawableEpoch = updatedSource.ExitEpoch + Presets.MinValidatorWithdrawabilityDelay;
        state.Validators[sourceIndex] = updatedSource;

        state.PendingConsolidations = [.. state.PendingConsolidations, new PendingConsolidation
        {
            SourceIndex = (ulong)sourceIndex,
            TargetIndex = (ulong)targetIndex,
        }];
    }

    /// <summary>Spec <c>process_randao</c>: verifies the proposer's reveal and mixes it into the current randao mix.</summary>
    public static partial void ProcessRandao(ForkState state, ForkBody body, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
    {
        const string invalidSignature = "Invalid RANDAO reveal";
        ulong epoch = state.GetCurrentEpoch();
#if GLOAS
        if (verifySignature && !VerifyRandaoReveal(state, state.GetBeaconProposerIndex(), epoch, body.RandaoReveal, pubkeys, batch?.Defer(invalidSignature)))
#else
        if (verifySignature && !ForkSignatureSets.VerifyRandaoReveal(state, (int)state.GetBeaconProposerIndex(), epoch, body.RandaoReveal, pubkeys, batch?.Defer(invalidSignature)))
#endif
            throw new BeaconStateException(invalidSignature);

        Span<byte> mix = stackalloc byte[32];
        SHA256.HashData(body.RandaoReveal.Bytes, mix);
        ReadOnlySpan<byte> currentMix = state.GetRandaoMix(epoch).Bytes;
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] ^= currentMix[i];
        }
        state.RandaoMixes![(int)(epoch % Presets.EpochsPerHistoricalVector)] = new Hash256(mix);
    }

    /// <summary>Spec <c>process_sync_aggregate</c> (Altair): verifies the aggregate and applies participant, proposer, and non-participant balance changes.</summary>
    public static partial void ProcessSyncAggregate(ForkState state, SyncAggregate syncAggregate, EpochCache cache, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
    {
#if GLOAS
        BlockProcessing.EnsureSyncCommitteeWidth(syncAggregate);
#else
        EnsureSyncCommitteeWidth(syncAggregate);
#endif

        const string invalidSignature = "Invalid sync aggregate signature";
#if GLOAS
        if (verifySignature && !VerifySyncAggregate(state, syncAggregate, cache.FindSyncCommitteeIndices(state.CurrentSyncCommittee!, state.Validators!), pubkeys, batch?.Defer(invalidSignature)))
#else
        if (verifySignature && !ForkSignatureSets.VerifySyncAggregate(state, syncAggregate, cache.FindSyncCommitteeIndices(state.CurrentSyncCommittee!, state.Validators!), pubkeys, batch?.Defer(invalidSignature)))
#endif
            throw new BeaconStateException(invalidSignature);

        ulong totalActiveIncrements = state.GetTotalActiveBalance(cache) / Presets.EffectiveBalanceIncrement;
        ulong totalBaseRewards = state.GetBaseRewardPerIncrement(cache) * totalActiveIncrements;
        ulong maxParticipantRewards = totalBaseRewards * Presets.SyncRewardWeight / Presets.WeightDenominator / Presets.SlotsPerEpoch;
#if GLOAS
        ulong participantReward = maxParticipantRewards / (ulong)Presets.SyncCommitteeSize;
#else
        ulong participantReward = maxParticipantRewards / Presets.SyncCommitteeSize;
#endif
        ulong proposerReward = participantReward * Presets.ProposerWeight / (Presets.WeightDenominator - Presets.ProposerWeight);

        // The committee's (possibly repeated) pubkeys mapped back to validator indices.
        int[] participantIndices = cache.GetSyncCommitteeIndices(state.CurrentSyncCommittee!, state.Validators!);

        int proposerIndex = (int)state.GetBeaconProposerIndex();
#if GLOAS
        System.Collections.BitArray bits = syncAggregate.SyncCommitteeBits!;
#else
        BitArray bits = syncAggregate.SyncCommitteeBits!;
#endif
        for (int i = 0; i < participantIndices.Length; i++)
        {
            int participantIndex = participantIndices[i];
            if (bits[i])
            {
                state.IncreaseBalance(participantIndex, participantReward);
                state.IncreaseBalance(proposerIndex, proposerReward);
            }
            else
            {
                state.DecreaseBalance(participantIndex, participantReward);
            }
        }
    }

    /// <summary>Spec <c>process_proposer_slashing</c>.</summary>
    /// <remarks>Gloas EIP-7732 clears only the matching proposer's payment in the two-epoch window.</remarks>
    public static partial void ProcessProposerSlashing(ForkState state, ProposerSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures, BlockSignatureBatch? batch)
    {
        BeaconBlockHeader header1 = slashing.SignedHeader1!.Message!;
        BeaconBlockHeader header2 = slashing.SignedHeader2!.Message!;

        if (header1.Slot != header2.Slot)
            throw new BeaconStateException("Proposer slashing header slots do not match");
        if (header1.ProposerIndex != header2.ProposerIndex)
            throw new BeaconStateException("Proposer slashing proposer indices do not match");
        if (HeaderEquals(header1, header2))
            throw new BeaconStateException("Proposer slashing headers are identical");
        if (header1.ProposerIndex >= (ulong)state.Validators!.Length)
            throw new BeaconStateException($"Proposer slashing index {header1.ProposerIndex} is out of range");
        if (!state.Validators[(int)header1.ProposerIndex].IsSlashableValidator(state.GetCurrentEpoch()))
            throw new BeaconStateException($"Proposer {header1.ProposerIndex} is not slashable");

        if (verifySignatures)
        {
            const string invalidSignature1 = "Invalid proposer slashing signature 1";
            const string invalidSignature2 = "Invalid proposer slashing signature 2";
            if (!ForkSignatureSets.VerifySignedBeaconBlockHeader(state, slashing.SignedHeader1, pubkeys, batch?.Defer(invalidSignature1)))
                throw new BeaconStateException(invalidSignature1);
            if (!ForkSignatureSets.VerifySignedBeaconBlockHeader(state, slashing.SignedHeader2, pubkeys, batch?.Defer(invalidSignature2)))
                throw new BeaconStateException(invalidSignature2);
        }

#if GLOAS
        // Only the payment recorded for this proposer is cleared: an unrelated same-slot
        // equivocation must not grief an honest proposer's payment.
        ulong proposalEpoch = BeaconStateAccessors.ComputeEpochAtSlot(header1.Slot);
        int? paymentIndex = proposalEpoch == state.GetCurrentEpoch()
            ? (int)(Presets.SlotsPerEpoch + header1.Slot % Presets.SlotsPerEpoch)
            : proposalEpoch == state.GetPreviousEpoch()
                ? (int)(header1.Slot % Presets.SlotsPerEpoch)
                : null;
        if (paymentIndex is int index && state.BuilderPendingPayments![index].ProposerIndex == header1.ProposerIndex)
            state.BuilderPendingPayments[index] = EmptyBuilderPendingPayment();

#endif
        state.SlashValidator((int)header1.ProposerIndex, cache);
    }

    /// <summary>
    /// Spec <c>is_valid_indexed_attestation</c>: indices must be non-empty, sorted, unique, and in
    /// range, and the aggregate signature must verify.
    /// </summary>
    /// <param name="deferral">Defers the signature to a batch; <c>null</c> verifies it now.</param>
    /// <remarks>Gloas EIP-7688 additionally bounds the indices by the committee capacity.</remarks>
    public static partial bool IsValidIndexedAttestation(ForkState state, ForkIndexedAttestation attestation, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch.Deferral? deferral)
    {
        ulong[] indices = attestation.AttestingIndices ?? [];
#if GLOAS
        if (indices.Length == 0 || indices.Length > Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot)
#else
        if (indices.Length == 0)
#endif
            return false;
        for (int i = 0; i < indices.Length; i++)
        {
            if (i > 0 && indices[i - 1] >= indices[i])
                return false;
            if (indices[i] >= (ulong)state.Validators!.Length)
                return false;
        }
        return !verifySignature || ForkSignatureSets.VerifyIndexedAttestation(state, attestation, pubkeys, deferral);
    }

    public static partial void ProcessBlockHeader(ForkState state, ForkBlock block)
    {
        if (block.Slot != state.Slot)
            throw new BeaconStateException($"Block slot {block.Slot} does not match state slot {state.Slot}");
        if (block.Slot <= state.LatestBlockHeader!.Slot)
            throw new BeaconStateException($"Block slot {block.Slot} is not newer than latest header slot {state.LatestBlockHeader.Slot}");
        if (block.ProposerIndex != state.GetBeaconProposerIndex())
            // ethereum/consensus-specs gloas/p2p-interface.md: "[REJECT] The block is proposed by the expected proposer for the slot".
            throw new BeaconStateException($"Block proposer {block.ProposerIndex} does not match expected proposer {state.GetBeaconProposerIndex()}") { RejectGossip = true };
        if (block.ParentRoot != SszRoots.HashTreeRoot(state.LatestBlockHeader))
            throw new BeaconStateException($"Block parent root {block.ParentRoot} does not match latest header root");

        state.LatestBlockHeader = new BeaconBlockHeader
        {
            Slot = block.Slot,
            ProposerIndex = block.ProposerIndex,
            ParentRoot = block.ParentRoot,
            StateRoot = Hash256.Zero, // Overwritten by the next process_slot.
            BodyRoot = SszRoots.HashTreeRoot(block.Body!),
        };

        if (state.Validators![(int)block.ProposerIndex].Slashed)
            throw new BeaconStateException($"Proposer {block.ProposerIndex} is slashed");
    }

    public static partial void ProcessEth1Data(ForkState state, ForkBody body)
    {
        state.Eth1DataVotes = [.. state.Eth1DataVotes!, body.Eth1Data!];
        int votes = 0;
        foreach (Eth1Data vote in state.Eth1DataVotes)
        {
#if GLOAS
            if (vote.DepositRoot == body.Eth1Data!.DepositRoot && vote.DepositCount == body.Eth1Data.DepositCount && vote.BlockHash == body.Eth1Data.BlockHash)
#else
            if (Eth1DataEquals(vote, body.Eth1Data!))
#endif
                votes++;
        }
        if ((ulong)votes * 2 > Presets.EpochsPerEth1VotingPeriod * Presets.SlotsPerEpoch)
            state.Eth1Data = body.Eth1Data;
    }

#if GLOAS
    public static partial void ProcessAttestation(ForkState state, ForkAttestation attestation, ulong parentSlot, EpochCache cache, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
#else
    public static partial void ProcessAttestation(ForkState state, ForkAttestation attestation, EpochCache cache, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
#endif
    {
        AttestationData data = attestation.Data!;
        ulong currentEpoch = state.GetCurrentEpoch();
        if (data.Target!.Epoch != state.GetPreviousEpoch() && data.Target.Epoch != currentEpoch)
            throw new BeaconStateException($"Attestation target epoch {data.Target.Epoch} is not the previous or current epoch");
        if (data.Target.Epoch != BeaconStateAccessors.ComputeEpochAtSlot(data.Slot))
            throw new BeaconStateException("Attestation target epoch does not match its slot");
        if (data.Slot + Presets.MinAttestationInclusionDelay > state.Slot)
            throw new BeaconStateException($"Attestation for slot {data.Slot} is included too early at slot {state.Slot}");
#if GLOAS
        if (data.Index >= 2)
            throw new BeaconStateException($"Attestation data index {data.Index} must encode a payload status (0 or 1)");
#else
        // [Modified in Electra:EIP7549] The committee is selected by committee_bits.
        if (data.Index != 0)
            throw new BeaconStateException($"Attestation data index {data.Index} must be zero");
#endif

        // GetAttestingIndices performs the spec's committee/aggregation-bits structural asserts.
        CommitteeCache committees = cache.GetCommitteeCache(state, data.Target.Epoch);
        ulong[] attestingIndices = state.GetAttestingIndices(attestation, committees);

#if GLOAS
        byte participationFlags = GetAttestationParticipationFlagIndices(state, data, state.Slot - data.Slot, parentSlot);
#else
        byte participationFlags = GetAttestationParticipationFlagIndices(state, data, state.Slot - data.Slot);
#endif

        ForkIndexedAttestation indexed = new()
        {
            AttestingIndices = attestingIndices,
            Data = data,
            Signature = attestation.Signature,
        };
        const string invalidAttestation = "Invalid indexed attestation";
        if (!IsValidIndexedAttestation(state, indexed, pubkeys, verifySignature, batch?.Defer(invalidAttestation)))
            throw new BeaconStateException(invalidAttestation);

#if GLOAS
        bool currentEpochTarget = data.Target.Epoch == currentEpoch;
        byte[] epochParticipation = currentEpochTarget ? state.CurrentEpochParticipation! : state.PreviousEpochParticipation!;
        int paymentIndex = (int)(currentEpochTarget ? Presets.SlotsPerEpoch + data.Slot % Presets.SlotsPerEpoch : data.Slot % Presets.SlotsPerEpoch);
        BuilderPendingPayment payment = state.BuilderPendingPayments![paymentIndex];
        bool weighsForPayment = payment.Withdrawal!.Amount > 0 && state.IsAttestationSameSlot(data);
#else
        byte[] epochParticipation = data.Target.Epoch == currentEpoch
            ? state.CurrentEpochParticipation!
            : state.PreviousEpochParticipation!;
#endif

        ulong proposerRewardNumerator = 0;
#if GLOAS
        ulong addedWeight = 0;
#endif
        ulong? baseRewardPerIncrement = null;
        foreach (ulong index in attestingIndices)
        {
            ulong? baseReward = null;
#if GLOAS
            bool hadNoParticipation = epochParticipation[index] == 0;
            bool willSetNewFlag = false;
#endif
            for (int flagIndex = 0; flagIndex < Presets.ParticipationFlagWeights.Length; flagIndex++)
            {
                byte flag = (byte)(1 << flagIndex);
                if ((participationFlags & flag) != 0 && (epochParticipation[index] & flag) == 0)
                {
                    epochParticipation[index] |= flag;
                    baseRewardPerIncrement ??= state.GetBaseRewardPerIncrement(cache);
                    baseReward ??= state.Validators![(int)index].EffectiveBalance / Presets.EffectiveBalanceIncrement * baseRewardPerIncrement.Value;
                    proposerRewardNumerator += baseReward.Value * Presets.ParticipationFlagWeights[flagIndex];
#if GLOAS
                    willSetNewFlag = true;
#endif
                }
            }
#if GLOAS
            if (willSetNewFlag && hadNoParticipation && weighsForPayment)
                addedWeight += state.Validators![(int)index].EffectiveBalance;
#endif
        }

        ulong proposerRewardDenominator = (Presets.WeightDenominator - Presets.ProposerWeight) * Presets.WeightDenominator / Presets.ProposerWeight;
        state.IncreaseBalance((int)state.GetBeaconProposerIndex(), proposerRewardNumerator / proposerRewardDenominator);
#if GLOAS

        // Written back as a new entry (the spec reassigns the payment) rather than in place: a
        // cloned state shares the entry objects, only the vector is copied.
        if (addedWeight > 0)
        {
            state.BuilderPendingPayments[paymentIndex] = new BuilderPendingPayment
            {
                Weight = payment.Weight + addedWeight,
                Withdrawal = payment.Withdrawal,
                ProposerIndex = payment.ProposerIndex,
            };
        }
#endif
    }

#if GLOAS
    private static partial byte GetAttestationParticipationFlagIndices(ForkState state, AttestationData data, ulong inclusionDelay, ulong parentSlot)
#else
    private static partial byte GetAttestationParticipationFlagIndices(ForkState state, AttestationData data, ulong inclusionDelay)
#endif
    {
        Checkpoint justifiedCheckpoint = data.Target!.Epoch == state.GetCurrentEpoch()
            ? state.CurrentJustifiedCheckpoint!
            : state.PreviousJustifiedCheckpoint!;
        if (data.Source!.Epoch != justifiedCheckpoint.Epoch || data.Source.Root != justifiedCheckpoint.Root)
            throw new BeaconStateException("Attestation source does not match the justified checkpoint");

        bool isMatchingTarget = data.Target.Root == state.GetBlockRoot(data.Target.Epoch);
#if GLOAS

        bool payloadMatches;
        if (state.IsAttestationSameSlot(data))
        {
            if (data.Index != 0)
                throw new BeaconStateException("An attestation for the block proposed at its own slot must carry index 0");
            payloadMatches = true;
        }
        else
        {
            bool payloadAvailable = state.ExecutionPayloadAvailability![(int)(parentSlot % Presets.SlotsPerHistoricalRoot)];
            payloadMatches = data.Index == (payloadAvailable ? 1UL : 0UL);
        }

        bool isMatchingHead = isMatchingTarget && data.BeaconBlockRoot == state.GetBlockRootAtSlot(data.Slot) && payloadMatches;
#else
        bool isMatchingHead = isMatchingTarget && data.BeaconBlockRoot == state.GetBlockRootAtSlot(data.Slot);
#endif

        byte flags = 0;
        if (inclusionDelay <= BeaconStateAccessors.IntegerSquareRoot(Presets.SlotsPerEpoch))
            flags |= 1 << Presets.TimelySourceFlagIndex;
        if (isMatchingTarget)
            flags |= 1 << Presets.TimelyTargetFlagIndex;
        if (isMatchingHead && inclusionDelay == Presets.MinAttestationInclusionDelay)
            flags |= 1 << Presets.TimelyHeadFlagIndex;
        return flags;
    }

    private static partial void SwitchToCompoundingValidator(ForkState state, int index)
    {
        Span<byte> credentials = stackalloc byte[32];
        Validator validator = state.Validators![index].Clone();
        validator.WithdrawalCredentials!.Bytes.CopyTo(credentials);
        credentials[0] = Presets.CompoundingWithdrawalPrefix;
        validator.WithdrawalCredentials = new Hash256(credentials);
        state.Validators[index] = validator;
#if GLOAS

        ulong balance = state.Balances![index];
        if (balance <= Presets.MinActivationBalance)
            return;
        state.Balances[index] = Presets.MinActivationBalance;
        state.PendingDeposits = [.. state.PendingDeposits!, new PendingDeposit
        {
            Pubkey = validator.Pubkey,
            WithdrawalCredentials = validator.WithdrawalCredentials,
            Amount = balance - Presets.MinActivationBalance,
            Signature = new BlsSignature(SignatureSets.G2PointAtInfinity),
            Slot = Presets.GenesisSlot,
        }];
#else
        QueueExcessActiveBalance(state, index);
#endif
    }
}
