// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Gloas <c>process_block</c> (EIP-7732's ePBS split) over <see cref="BeaconStateGloas"/>.
/// </summary>
/// <remarks>
/// Ported from ethereum/consensus-specs <c>v1.7.0-beta.2</c> (<c>specs/gloas/beacon-chain.md</c>,
/// cross-referenced against <c>specs/gloas/fork-choice.md</c> at the same tag for
/// <c>on_execution_payload_envelope</c>). The spec is explicitly work in progress; this file
/// documents every place its text was ambiguous or (as found for the block-processing step order)
/// inconsistent with a paraphrase, and the reading taken, rather than picking silently.
/// <para/>
/// <b>Block-processing step order.</b> A short prose summary of this task described the order as
/// header, RANDAO, Eth1 data, operations, bid, withdrawals. The pinned spec's actual
/// <c>process_block</c> body, and its own prose note on <c>process_withdrawals</c> ("must be
/// called after <c>process_parent_execution_payload</c> ... and before
/// <c>process_execution_payload_bid</c> as the latter function affects validator balances"), both
/// give a different, more specific order:
/// <c>process_parent_execution_payload, process_block_header, process_withdrawals,
/// process_execution_payload_bid, process_randao, process_eth1_data, process_operations,
/// process_sync_aggregate</c>. This file follows the pinned spec text over the paraphrase, since
/// the paraphrase is not itself a source of truth and the spec text is internally self-consistent
/// (the withdrawals/bid ordering note only makes sense under this order) and would otherwise
/// silently produce the wrong post-state root.
/// <para/>
/// <b>Envelope processing timing.</b> The task described envelope processing as verifying an
/// envelope against its committed bid "then applying the payload to state" as one step. The pinned
/// fork-choice spec's <c>on_execution_payload_envelope</c> calls only
/// <c>verify_execution_payload_envelope</c> - a pure verification with no state mutation - and
/// records the envelope in the fork-choice store; the payload is applied to state one block later,
/// inside the child block's own <c>process_parent_execution_payload</c>. This file follows that
/// split: <see cref="VerifyExecutionPayloadEnvelope"/> is the separate, non-mutating entry point
/// the task asked for, and application happens through <see cref="ProcessBlock"/> processing the
/// next block, matching the task's own framing ("applied to state one block later") more precisely
/// than a same-step read would.
/// <para/>
/// <b>Scope.</b> The whole of <c>process_block</c>: block header and RANDAO, bid processing,
/// envelope verification, withdrawals computed from state, the builder payment/deposit/exit
/// machinery - including a parent payload whose execution requests are non-empty, via the full
/// deposit/withdrawal/consolidation/builder-deposit/builder-exit request pipeline in
/// <see cref="ApplyParentExecutionPayload"/> - with the payment window addressed exactly as the
/// spec does now that <see cref="GloasEpochProcessing"/> rotates it at every epoch boundary, and
/// every block-body operation (<see cref="ProcessOperations"/>), attestations with their builder
/// payment weight and PTC payload attestations included. The operations inherited unchanged from
/// Electra are ported to the Gloas state type rather than shared with <see cref="BlockProcessing"/>,
/// for the reason given on <see cref="GloasStateAccessors"/>; their signature checks live in
/// <see cref="GloasSignatureSets"/>.
/// </remarks>
public static class GloasBlockProcessing
{
    /// <summary>Spec <c>process_block</c> (Gloas). See this type's remarks for the step order and why.</summary>
    public static void ProcessBlock(BeaconStateGloas state, BeaconBlockGloas block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, BeaconChainSpec spec, bool verifySignatures = true)
    {
        BeaconBlockBodyGloas body = block.Body!;
        ulong parentSlot = state.LatestBlockHeader!.Slot;

        ProcessParentExecutionPayload(state, block, cache);
        ProcessBlockHeader(state, block);
        ProcessWithdrawals(state);
        ProcessExecutionPayloadBid(state, body.SignedExecutionPayloadBid!, spec, pubkeys, verifySignatures);
        ProcessRandao(state, body, pubkeys, verifySignatures);
        ProcessEth1Data(state, body);
        ProcessOperations(state, body, parentSlot, spec, cache, pubkeys, verifySignatures);
        ProcessSyncAggregate(state, body.SyncAggregate!, cache, verifySignatures);
    }

    /// <summary>Verifies a Gloas block's outer proposer signature - not part of <c>process_block</c> itself, called once by the top-level state transition.</summary>
    /// <exception cref="BeaconStateException">The proposer index is not a validator of <paramref name="state"/>, or <paramref name="pubkeys"/> has no key for it.</exception>
    public static bool VerifyProposerSignature(BeaconStateGloas state, SignedBeaconBlockGloas signedBlock, PubkeyCache pubkeys)
    {
        BeaconBlockGloas block = signedBlock.Message!;
        // verify_block_signature indexes state.validators with the untrusted proposer_index (p2p beacon_block: [REJECT] a valid validator index).
        ulong proposerIndex = block.ProposerIndex;
        if (proposerIndex >= (ulong)state.Validators!.Length)
            throw new BeaconStateException($"Block proposer index {proposerIndex} is not a validator index (registry size {state.Validators.Length})");
        // Epoch processing inside process_slots can grow the registry past the cache.
        if (proposerIndex >= (ulong)pubkeys.Count)
            throw new BeaconStateException($"Block proposer index {proposerIndex} has no cached public key ({pubkeys.Count} cached)");
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), domain);
        return BlsSigner.Verify(pubkeys.GetPublicKey((int)proposerIndex), signedBlock.Signature.Bytes, signingRoot.Bytes);
    }

    /// <summary>Spec <c>process_block_header</c>: unchanged from Fulu except for the Gloas block/body types.</summary>
    public static void ProcessBlockHeader(BeaconStateGloas state, BeaconBlockGloas block)
    {
        if (block.Slot != state.Slot)
            throw new BeaconStateException($"Block slot {block.Slot} does not match state slot {state.Slot}");
        if (block.Slot <= state.LatestBlockHeader!.Slot)
            throw new BeaconStateException($"Block slot {block.Slot} is not newer than latest header slot {state.LatestBlockHeader.Slot}");
        if (block.ProposerIndex != state.GetBeaconProposerIndex())
            throw new BeaconStateException($"Block proposer {block.ProposerIndex} does not match expected proposer {state.GetBeaconProposerIndex()}");
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

    /// <summary>Spec <c>process_randao</c>: unchanged from Fulu except for the Gloas body type.</summary>
    public static void ProcessRandao(BeaconStateGloas state, BeaconBlockBodyGloas body, PubkeyCache pubkeys, bool verifySignature = true)
    {
        ulong epoch = state.GetCurrentEpoch();
        if (verifySignature && !VerifyRandaoReveal(state, (int)state.GetBeaconProposerIndex(), epoch, body.RandaoReveal, pubkeys))
            throw new BeaconStateException("Invalid RANDAO reveal");

        Span<byte> mix = stackalloc byte[32];
        SHA256.HashData(body.RandaoReveal.Bytes, mix);
        ReadOnlySpan<byte> currentMix = state.GetRandaoMix(epoch).Bytes;
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] ^= currentMix[i];
        }
        state.RandaoMixes![(int)(epoch % Presets.EpochsPerHistoricalVector)] = new Hash256(mix);
    }

    private static bool VerifyRandaoReveal(BeaconStateGloas state, int proposerIndex, ulong epoch, BlsSignature reveal, PubkeyCache pubkeys)
    {
        Span<byte> epochRoot = stackalloc byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(epochRoot, epoch);
        Hash256 domain = state.GetDomain(DomainType.Randao, epoch);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(epochRoot), domain);
        return BlsSigner.Verify(pubkeys.GetPublicKey(proposerIndex), reveal.Bytes, signingRoot.Bytes);
    }

    /// <summary>Spec <c>process_eth1_data</c>: unchanged from Fulu except for the Gloas body type.</summary>
    public static void ProcessEth1Data(BeaconStateGloas state, BeaconBlockBodyGloas body)
    {
        state.Eth1DataVotes = [.. state.Eth1DataVotes!, body.Eth1Data!];
        int votes = 0;
        foreach (Eth1Data vote in state.Eth1DataVotes)
        {
            if (vote.DepositRoot == body.Eth1Data!.DepositRoot && vote.DepositCount == body.Eth1Data.DepositCount && vote.BlockHash == body.Eth1Data.BlockHash)
                votes++;
        }
        if ((ulong)votes * 2 > Presets.EpochsPerEth1VotingPeriod * Presets.SlotsPerEpoch)
            state.Eth1Data = body.Eth1Data;
    }

    /// <summary>
    /// Spec <c>process_operations</c> (Gloas): the deposit-request/withdrawal-request/
    /// consolidation-request dispatch is gone (moved to <see cref="ApplyParentExecutionPayload"/>),
    /// payload attestations are added, and attestations learn the parent block's slot.
    /// </summary>
    public static void ProcessOperations(BeaconStateGloas state, BeaconBlockBodyGloas body, ulong parentSlot, BeaconChainSpec spec, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true)
    {
        VerifyBlockBodyOperationLimits(body);

        foreach (ProposerSlashing slashing in body.ProposerSlashings ?? [])
        {
            ProcessProposerSlashing(state, slashing, cache, pubkeys, verifySignatures);
        }
        foreach (AttesterSlashingGloas slashing in body.AttesterSlashings ?? [])
        {
            ProcessAttesterSlashing(state, slashing, cache, pubkeys, verifySignatures);
        }
        foreach (AttestationGloas attestation in body.Attestations ?? [])
        {
            ProcessAttestation(state, attestation, parentSlot, cache, pubkeys, verifySignatures);
        }
        foreach (SignedVoluntaryExit exit in body.VoluntaryExits ?? [])
        {
            ProcessVoluntaryExit(state, exit, cache, pubkeys, verifySignatures);
        }
        foreach (SignedBlsToExecutionChange change in body.BlsToExecutionChanges ?? [])
        {
            ProcessBlsToExecutionChange(state, change, verifySignatures);
        }
        foreach (PayloadAttestation attestation in body.PayloadAttestations ?? [])
        {
            ProcessPayloadAttestation(state, attestation, spec, pubkeys, verifySignatures);
        }
    }

    /// <summary>
    /// Spec <c>verify_block_body_operation_limits</c> (gloas/p2p-interface.md), also asserted by
    /// <c>process_operations</c>: zero deposits and every operation count within its limit.
    /// </summary>
    /// <remarks>[New in Gloas:EIP7688] The lists are progressive (no SSZ-level bound), so the limits are asserted here instead.</remarks>
    /// <exception cref="BeaconStateException">A count is over its limit, or the body carries a deposit.</exception>
    internal static void VerifyBlockBodyOperationLimits(BeaconBlockBodyGloas body)
    {
        if ((body.Deposits?.Length ?? 0) != 0)
            throw new BeaconStateException("Gloas block body must carry zero deposits (EIP-6110: the Eth1 deposit path is fully retired)");

        RequireAtMost(body.ProposerSlashings, Presets.MaxProposerSlashings, "proposer slashings");
        RequireAtMost(body.AttesterSlashings, Presets.MaxAttesterSlashingsElectra, "attester slashings");
        RequireAtMost(body.Attestations, Presets.MaxAttestationsElectra, "attestations");
        RequireAtMost(body.VoluntaryExits, Presets.MaxVoluntaryExits, "voluntary exits");
        RequireAtMost(body.BlsToExecutionChanges, Presets.MaxBlsToExecutionChanges, "BLS-to-execution changes");
        RequireAtMost(body.PayloadAttestations, Presets.MaxPayloadAttestations, "payload attestations");
    }

    private static void RequireAtMost<T>(T[]? operations, int limit, string name)
    {
        int count = operations?.Length ?? 0;
        if (count > limit)
            throw new BeaconStateException($"Block has {count} {name}, exceeding the limit of {limit}");
    }

    /// <summary>
    /// Spec <c>process_payload_attestation</c> (new in Gloas): a PTC vote on the parent block's
    /// payload, valid only for the parent and the previous slot. Pure verification - the vote's
    /// content feeds fork choice, not the state.
    /// </summary>
    public static void ProcessPayloadAttestation(BeaconStateGloas state, PayloadAttestation attestation, BeaconChainSpec spec, PubkeyCache pubkeys, bool verifySignature = true)
    {
        PayloadAttestationData data = attestation.Data!;
        if (data.BeaconBlockRoot != state.LatestBlockHeader!.ParentRoot)
            throw new BeaconStateException("Payload attestation is not for the parent beacon block");
        if (data.Slot + 1 != state.Slot)
            throw new BeaconStateException($"Payload attestation for slot {data.Slot} is not for the slot before {state.Slot}");

        IndexedPayloadAttestation indexed = state.GetIndexedPayloadAttestation(attestation, spec);
        if (!IsValidIndexedPayloadAttestation(state, indexed, pubkeys, verifySignature))
            throw new BeaconStateException("Invalid indexed payload attestation");
    }

    /// <summary>
    /// Spec <c>is_valid_indexed_payload_attestation</c>: indices must be non-empty, sorted (a PTC
    /// is sampled with replacement, so repeats are legitimate) and in range, and the aggregate
    /// signature must verify.
    /// </summary>
    public static bool IsValidIndexedPayloadAttestation(BeaconStateGloas state, IndexedPayloadAttestation attestation, PubkeyCache pubkeys, bool verifySignature)
    {
        ulong[] indices = attestation.AttestingIndices ?? [];
        if (indices.Length == 0)
            return false;
        for (int i = 0; i < indices.Length; i++)
        {
            if (i > 0 && indices[i - 1] > indices[i])
                return false;
            if (indices[i] >= (ulong)state.Validators!.Length)
                return false;
        }
        return !verifySignature || GloasSignatureSets.VerifyIndexedPayloadAttestation(state, attestation, pubkeys);
    }

    /// <summary>
    /// Spec <c>process_proposer_slashing</c> (Gloas): the Electra checks and slashing, plus the
    /// EIP-7732 clearing of the pending builder payment for the equivocated proposal, when that
    /// payment is still in the two-epoch window and was recorded for this same proposer.
    /// </summary>
    public static void ProcessProposerSlashing(BeaconStateGloas state, ProposerSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true)
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
            if (!GloasSignatureSets.VerifySignedBeaconBlockHeader(state, slashing.SignedHeader1, pubkeys))
                throw new BeaconStateException("Invalid proposer slashing signature 1");
            if (!GloasSignatureSets.VerifySignedBeaconBlockHeader(state, slashing.SignedHeader2, pubkeys))
                throw new BeaconStateException("Invalid proposer slashing signature 2");
        }

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

        state.SlashValidator((int)header1.ProposerIndex, cache);
    }

    private static bool HeaderEquals(BeaconBlockHeader a, BeaconBlockHeader b) =>
        a.Slot == b.Slot
        && a.ProposerIndex == b.ProposerIndex
        && a.ParentRoot == b.ParentRoot
        && a.StateRoot == b.StateRoot
        && a.BodyRoot == b.BodyRoot;

    /// <summary>Spec <c>BuilderPendingPayment.empty()</c>, in the shape <see cref="GloasEpochProcessing.ProcessBuilderPendingPayments"/> and <see cref="SettleBuilderPayment"/> write.</summary>
    private static BuilderPendingPayment EmptyBuilderPendingPayment() =>
        new() { Withdrawal = new BuilderPendingWithdrawal() };

    /// <summary>Spec <c>process_attester_slashing</c> (unmodified in Gloas): slashes every still-slashable validator attesting in both votes.</summary>
    public static void ProcessAttesterSlashing(BeaconStateGloas state, AttesterSlashingGloas slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true)
    {
        IndexedAttestationGloas attestation1 = slashing.Attestation1!;
        IndexedAttestationGloas attestation2 = slashing.Attestation2!;

        if (!BeaconStateAccessors.IsSlashableAttestationData(attestation1.Data!, attestation2.Data!))
            throw new BeaconStateException("Attester slashing votes are not slashable");
        if (!IsValidIndexedAttestation(state, attestation1, pubkeys, verifySignatures))
            throw new BeaconStateException("Attester slashing attestation 1 is invalid");
        if (!IsValidIndexedAttestation(state, attestation2, pubkeys, verifySignatures))
            throw new BeaconStateException("Attester slashing attestation 2 is invalid");

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

    /// <summary>
    /// Spec <c>is_valid_indexed_attestation</c> (Gloas): indices must be non-empty, within the
    /// EIP-7688 bound that replaced the list's SSZ limit, sorted, unique and in range, and the
    /// aggregate signature must verify.
    /// </summary>
    public static bool IsValidIndexedAttestation(BeaconStateGloas state, IndexedAttestationGloas attestation, PubkeyCache pubkeys, bool verifySignature)
    {
        ulong[] indices = attestation.AttestingIndices ?? [];
        if (indices.Length == 0 || indices.Length > Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot)
            return false;
        for (int i = 0; i < indices.Length; i++)
        {
            if (i > 0 && indices[i - 1] >= indices[i])
                return false;
            if (indices[i] >= (ulong)state.Validators!.Length)
                return false;
        }
        return !verifySignature || GloasSignatureSets.VerifyIndexedAttestation(state, attestation, pubkeys);
    }

    /// <summary>
    /// Spec <c>process_attestation</c> (Gloas): the Electra aggregate validation, participation
    /// flags and proposer reward, with two EIP-7732 changes - <c>data.index</c> now encodes the
    /// attested block's payload status (0 absent, 1 present) instead of a committee index, and a
    /// validator's first participation in the target epoch, when it votes for the block proposed
    /// at the attestation slot, adds its effective balance to the weight of that slot's pending
    /// builder payment (the PTC-quorum the epoch transition honors the payment against).
    /// </summary>
    /// <param name="parentSlot">The slot of the block's parent, where the attested block's payload availability is tracked.</param>
    public static void ProcessAttestation(BeaconStateGloas state, AttestationGloas attestation, ulong parentSlot, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true)
    {
        AttestationData data = attestation.Data!;
        ulong currentEpoch = state.GetCurrentEpoch();
        if (data.Target!.Epoch != state.GetPreviousEpoch() && data.Target.Epoch != currentEpoch)
            throw new BeaconStateException($"Attestation target epoch {data.Target.Epoch} is not the previous or current epoch");
        if (data.Target.Epoch != BeaconStateAccessors.ComputeEpochAtSlot(data.Slot))
            throw new BeaconStateException("Attestation target epoch does not match its slot");
        if (data.Slot + Presets.MinAttestationInclusionDelay > state.Slot)
            throw new BeaconStateException($"Attestation for slot {data.Slot} is included too early at slot {state.Slot}");
        if (data.Index >= 2)
            throw new BeaconStateException($"Attestation data index {data.Index} must encode a payload status (0 or 1)");

        // GetAttestingIndices performs the spec's committee/aggregation-bits structural asserts.
        CommitteeCache committees = cache.GetCommitteeCache(state, data.Target.Epoch);
        ulong[] attestingIndices = state.GetAttestingIndices(attestation, committees);

        byte participationFlags = GetAttestationParticipationFlagIndices(state, data, state.Slot - data.Slot, parentSlot);

        IndexedAttestationGloas indexed = new()
        {
            AttestingIndices = attestingIndices,
            Data = data,
            Signature = attestation.Signature,
        };
        if (!IsValidIndexedAttestation(state, indexed, pubkeys, verifySignature))
            throw new BeaconStateException("Invalid indexed attestation");

        bool currentEpochTarget = data.Target.Epoch == currentEpoch;
        byte[] epochParticipation = currentEpochTarget ? state.CurrentEpochParticipation! : state.PreviousEpochParticipation!;
        int paymentIndex = (int)(currentEpochTarget ? Presets.SlotsPerEpoch + data.Slot % Presets.SlotsPerEpoch : data.Slot % Presets.SlotsPerEpoch);
        BuilderPendingPayment payment = state.BuilderPendingPayments![paymentIndex];
        bool weighsForPayment = payment.Withdrawal!.Amount > 0 && state.IsAttestationSameSlot(data);

        ulong proposerRewardNumerator = 0;
        ulong addedWeight = 0;
        foreach (ulong index in attestingIndices)
        {
            bool hadNoParticipation = epochParticipation[index] == 0;
            bool willSetNewFlag = false;
            for (int flagIndex = 0; flagIndex < Presets.ParticipationFlagWeights.Length; flagIndex++)
            {
                byte flag = (byte)(1 << flagIndex);
                if ((participationFlags & flag) != 0 && (epochParticipation[index] & flag) == 0)
                {
                    epochParticipation[index] |= flag;
                    proposerRewardNumerator += state.GetBaseReward((int)index, cache) * Presets.ParticipationFlagWeights[flagIndex];
                    willSetNewFlag = true;
                }
            }
            if (willSetNewFlag && hadNoParticipation && weighsForPayment)
                addedWeight += state.Validators![(int)index].EffectiveBalance;
        }

        ulong proposerRewardDenominator = (Presets.WeightDenominator - Presets.ProposerWeight) * Presets.WeightDenominator / Presets.ProposerWeight;
        state.IncreaseBalance((int)state.GetBeaconProposerIndex(), proposerRewardNumerator / proposerRewardDenominator);

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
    }

    /// <summary>
    /// Spec <c>get_attestation_participation_flag_indices</c> (Gloas), returned as a bitmask over
    /// the participation flag indices. The head flag additionally requires the attestation's
    /// payload status to match: trivially for a vote for the block proposed at the attestation
    /// slot (which must then carry index 0), otherwise against the availability recorded at
    /// <paramref name="parentSlot"/>.
    /// </summary>
    /// <exception cref="BeaconStateException">The source does not match the justified checkpoint, or a same-slot vote carries a non-zero index.</exception>
    private static byte GetAttestationParticipationFlagIndices(BeaconStateGloas state, AttestationData data, ulong inclusionDelay, ulong parentSlot)
    {
        Checkpoint justifiedCheckpoint = data.Target!.Epoch == state.GetCurrentEpoch()
            ? state.CurrentJustifiedCheckpoint!
            : state.PreviousJustifiedCheckpoint!;
        if (data.Source!.Epoch != justifiedCheckpoint.Epoch || data.Source.Root != justifiedCheckpoint.Root)
            throw new BeaconStateException("Attestation source does not match the justified checkpoint");

        bool isMatchingTarget = data.Target.Root == state.GetBlockRoot(data.Target.Epoch);

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

        byte flags = 0;
        if (inclusionDelay <= BeaconStateAccessors.IntegerSquareRoot(Presets.SlotsPerEpoch))
            flags |= 1 << Presets.TimelySourceFlagIndex;
        if (isMatchingTarget)
            flags |= 1 << Presets.TimelyTargetFlagIndex;
        if (isMatchingHead && inclusionDelay == Presets.MinAttestationInclusionDelay)
            flags |= 1 << Presets.TimelyHeadFlagIndex;
        return flags;
    }

    /// <summary>Spec <c>process_voluntary_exit</c> (Electra, unmodified in Gloas); the exit it initiates draws on the EIP-8061 exit churn.</summary>
    public static void ProcessVoluntaryExit(BeaconStateGloas state, SignedVoluntaryExit signedExit, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true)
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
        if (state.GetPendingBalanceToWithdraw((int)exit.ValidatorIndex) != 0)
            throw new BeaconStateException($"Validator {exit.ValidatorIndex} has pending partial withdrawals");
        if (verifySignature && !GloasSignatureSets.VerifyVoluntaryExit(state, signedExit, pubkeys))
            throw new BeaconStateException("Invalid voluntary exit signature");

        state.InitiateValidatorExit((int)exit.ValidatorIndex, cache);
    }

    /// <summary>Spec <c>process_bls_to_execution_change</c> (Capella, unmodified in Gloas).</summary>
    public static void ProcessBlsToExecutionChange(BeaconStateGloas state, SignedBlsToExecutionChange signedChange, bool verifySignature = true)
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
        if (verifySignature && !GloasSignatureSets.VerifyBlsToExecutionChange(state, signedChange))
            throw new BeaconStateException("Invalid BLS to execution change signature");

        Span<byte> newCredentials = stackalloc byte[32];
        newCredentials[0] = Presets.EthWithdrawalPrefix;
        change.ToExecutionAddress!.Bytes.CopyTo(newCredentials[12..]);
        Validator updated = validator.Clone();
        updated.WithdrawalCredentials = new Hash256(newCredentials);
        state.Validators[(int)change.ValidatorIndex] = updated;
    }

    /// <summary>
    /// Spec <c>process_sync_aggregate</c> (Altair): unchanged semantics, ported to
    /// <see cref="BeaconStateGloas"/> because this step runs unconditionally in every
    /// <c>process_block</c>, Gloas included.
    /// </summary>
    public static void ProcessSyncAggregate(BeaconStateGloas state, SyncAggregate syncAggregate, EpochCache cache, bool verifySignature = true)
    {
        if (verifySignature && !VerifySyncAggregate(state, syncAggregate))
            throw new BeaconStateException("Invalid sync aggregate signature");

        ulong totalActiveIncrements = state.GetTotalActiveBalance(cache) / Presets.EffectiveBalanceIncrement;
        ulong totalBaseRewards = state.GetBaseRewardPerIncrement(cache) * totalActiveIncrements;
        ulong maxParticipantRewards = totalBaseRewards * Presets.SyncRewardWeight / Presets.WeightDenominator / Presets.SlotsPerEpoch;
        ulong participantReward = maxParticipantRewards / (ulong)Presets.SyncCommitteeSize;
        ulong proposerReward = participantReward * Presets.ProposerWeight / (Presets.WeightDenominator - Presets.ProposerWeight);

        Dictionary<BlsPublicKey, int> indexByPubkey = new(state.Validators!.Length);
        for (int i = 0; i < state.Validators.Length; i++)
        {
            indexByPubkey.TryAdd(state.Validators[i].Pubkey, i);
        }

        int proposerIndex = (int)state.GetBeaconProposerIndex();
        BlsPublicKey[] committee = state.CurrentSyncCommittee!.Pubkeys!;
        System.Collections.BitArray bits = syncAggregate.SyncCommitteeBits!;
        for (int i = 0; i < committee.Length; i++)
        {
            int participantIndex = indexByPubkey[committee[i]];
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

    private static bool VerifySyncAggregate(BeaconStateGloas state, SyncAggregate syncAggregate)
    {
        System.Collections.BitArray bits = syncAggregate.SyncCommitteeBits!;
        BlsPublicKey[] committee = state.CurrentSyncCommittee!.Pubkeys!;

        BlsSigner.AggregatedPublicKey participants = new(stackalloc long[Bls.P1.Sz]);
        int participantCount = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            if (!bits[i])
                continue;
            if (!participants.TryAggregate(committee[i].Bytes, out _))
                return false;
            participantCount++;
        }

        if (participantCount == 0)
            return syncAggregate.SyncCommitteeSignature.Bytes.SequenceEqual(SignatureSets.G2PointAtInfinity);

        ulong previousSlot = Math.Max(state.Slot, 1) - 1;
        Hash256 domain = state.GetDomain(DomainType.SyncCommittee, BeaconStateAccessors.ComputeEpochAtSlot(previousSlot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(state.GetBlockRootAtSlot(previousSlot), domain);

        Bls.P2 point = new(stackalloc long[Bls.P2.Sz]);
        return point.TryDecode(syncAggregate.SyncCommitteeSignature.Bytes, out _)
            && BlsSigner.VerifyAggregate(participants, new BlsSigner.Signature(point), signingRoot.Bytes);
    }

    // ---- Execution payload bid (EIP-7732) ----

    /// <summary>
    /// Spec <c>process_execution_payload_bid</c>: verifies the builder (or the self-build sentinel),
    /// verifies the bid is consistent with the chain tip, records the pending payment, and commits
    /// the bid as <c>state.latest_execution_payload_bid</c>.
    /// </summary>
    public static void ProcessExecutionPayloadBid(BeaconStateGloas state, SignedExecutionPayloadBid signedBid, BeaconChainSpec spec, PubkeyCache pubkeys, bool verifySignature = true)
    {
        ExecutionPayloadBid bid = signedBid.Message!;
        ulong builderIndex = bid.BuilderIndex;
        ulong amount = bid.Value;

        if (builderIndex == Presets.BuilderIndexSelfBuild)
        {
            if (amount != 0)
                throw new BeaconStateException("A self-build bid must have zero value");
            if (!signedBid.Signature.Bytes.SequenceEqual(SignatureSets.G2PointAtInfinity))
                throw new BeaconStateException("A self-build bid must carry the G2 point-at-infinity signature placeholder");
        }
        else
        {
            if (!state.IsActiveBuilder(builderIndex))
                throw new BeaconStateException($"Builder {builderIndex} is not active");
            if (state.Builders![(int)builderIndex].Version != Presets.PayloadBuilderVersion)
                throw new BeaconStateException($"Builder {builderIndex} is not registered as a payload builder");
            if (!state.CanBuilderCoverBid(builderIndex, amount))
                throw new BeaconStateException($"Builder {builderIndex} cannot cover a bid of {amount}");
            if (verifySignature && !VerifyExecutionPayloadBidSignature(state, signedBid))
                throw new BeaconStateException("Invalid execution payload bid signature");
        }

        // Spec get_blob_parameters falls back to MAX_BLOBS_PER_BLOCK_ELECTRA when no BLOB_SCHEDULE entry applies, whatever FULU_FORK_EPOCH is.
        ulong maxBlobsPerBlock = spec.GetBlobParameters(state.GetCurrentEpoch())?.MaxBlobsPerBlock ?? spec.MaxBlobsPerBlockElectra;
        if ((ulong)(bid.BlobKzgCommitments?.Length ?? 0) > maxBlobsPerBlock)
            throw new BeaconStateException($"Bid has {bid.BlobKzgCommitments!.Length} blob commitments, exceeding the limit of {maxBlobsPerBlock}");

        if (bid.Slot != state.Slot)
            throw new BeaconStateException($"Bid slot {bid.Slot} does not match state slot {state.Slot}");
        if (state.Slot <= Presets.GenesisSlot)
            throw new BeaconStateException("A bid cannot be processed at the genesis slot");
        if (bid.ParentBlockHash != state.LatestBlockHash)
            throw new BeaconStateException("Bid parent block hash does not match the state's latest block hash");
        if (bid.BlockHash == bid.ParentBlockHash)
            throw new BeaconStateException("Bid block hash must differ from its parent block hash");
        if (bid.ParentBlockRoot != state.GetBlockRootAtSlot(state.Slot - 1))
            throw new BeaconStateException("Bid parent block root does not match the block root at the previous slot");
        if (bid.PrevRandao != state.GetRandaoMix(state.GetCurrentEpoch()))
            throw new BeaconStateException("Bid prev_randao does not match the current randao mix");

        if (amount > 0)
        {
            // The upper half of the window is the current epoch's; GloasEpochProcessing zeroes it at
            // every boundary, so within an epoch each slot's address is written at most once.
            int index = (int)(Presets.SlotsPerEpoch + bid.Slot % Presets.SlotsPerEpoch);
            state.BuilderPendingPayments![index] = new BuilderPendingPayment
            {
                Weight = 0,
                Withdrawal = new BuilderPendingWithdrawal
                {
                    FeeRecipient = bid.FeeRecipient,
                    Amount = amount,
                    BuilderIndex = builderIndex,
                },
                ProposerIndex = state.GetBeaconProposerIndex(),
            };
        }

        state.LatestExecutionPayloadBid = bid;
    }

    private static bool VerifyExecutionPayloadBidSignature(BeaconStateGloas state, SignedExecutionPayloadBid signedBid)
    {
        Builder builder = state.Builders![(int)signedBid.Message!.BuilderIndex];
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(signedBid.Message), domain);

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        return pubkey.TryDecode(builder.Pubkey.Bytes, out _) && BlsSigner.Verify(pubkey, signedBid.Signature.Bytes, signingRoot.Bytes);
    }

    // ---- Execution payload envelope (EIP-7732) ----

    /// <summary>
    /// Spec <c>verify_execution_payload_envelope</c>, the entry point for a received envelope
    /// (fork-choice's <c>on_execution_payload_envelope</c>), bound to the frozen post-state of
    /// <c>envelope.beacon_block_root</c> the way the spec's <c>store.block_states[...]</c> lookup
    /// binds it. A pure verification against the state that already committed the envelope's bid -
    /// it mutates nothing; see this type's remarks on why application happens one block later.
    /// </summary>
    /// <remarks>
    /// The state is resolved here, not accepted from the caller, because the per-field checks below
    /// can only ever compare the envelope with the state they are handed: a self-consistent envelope
    /// built against a state that is not any known block's post-state passes them all. The only
    /// thing that catches it is refusing every root <paramref name="states"/> does not know.
    /// </remarks>
    /// <exception cref="BeaconStateException">The envelope names an unknown block, or fails any spec check.</exception>
    public static void VerifyExecutionPayloadEnvelope(IGloasBlockStateProvider states, SignedExecutionPayloadEnvelope signedEnvelope, INewPayloadNotifier notifier, PubkeyCache pubkeys)
    {
        Hash256 blockRoot = signedEnvelope.Message!.BeaconBlockRoot ?? throw new BeaconStateException("Envelope carries no beacon block root");
        BeaconStateGloas state = states.GetGloasBlockState(blockRoot)
            ?? throw new BeaconStateException($"Envelope names beacon block {blockRoot}, whose post-state is not known");
        VerifyExecutionPayloadEnvelopeAgainst(state, signedEnvelope, notifier, pubkeys);
    }

    /// <summary>
    /// The spec's <c>verify_execution_payload_envelope(state, ...)</c> body. Private on purpose: the
    /// only way in is <see cref="VerifyExecutionPayloadEnvelope"/>, which chooses <paramref name="state"/>
    /// by the envelope's own block root instead of trusting whatever a caller has to hand.
    /// </summary>
    private static void VerifyExecutionPayloadEnvelopeAgainst(BeaconStateGloas state, SignedExecutionPayloadEnvelope signedEnvelope, INewPayloadNotifier notifier, PubkeyCache pubkeys)
    {
        ExecutionPayloadEnvelope envelope = signedEnvelope.Message!;
        ExecutionPayloadGloas payload = envelope.Payload!;

        // The spec's state.builders[envelope.builder_index] fails the envelope for an index outside the registry.
        if (envelope.BuilderIndex != Presets.BuilderIndexSelfBuild && envelope.BuilderIndex >= (ulong)state.Builders!.Length)
            throw new BeaconStateException($"Envelope builder index {envelope.BuilderIndex} is not in the builder registry");

        if (!VerifyExecutionPayloadEnvelopeSignature(state, signedEnvelope, pubkeys))
            throw new BeaconStateException("Invalid execution payload envelope signature");

        BeaconBlockHeader header = new()
        {
            Slot = state.LatestBlockHeader!.Slot,
            ProposerIndex = state.LatestBlockHeader.ProposerIndex,
            ParentRoot = state.LatestBlockHeader.ParentRoot,
            StateRoot = SszRoots.HashTreeRoot(state),
            BodyRoot = state.LatestBlockHeader.BodyRoot,
        };
        if (envelope.BeaconBlockRoot != SszRoots.HashTreeRoot(header))
            throw new BeaconStateException("Envelope beacon block root does not match the state's own latest block");
        if (envelope.ParentBeaconBlockRoot != state.LatestBlockHeader.ParentRoot)
            throw new BeaconStateException("Envelope parent beacon block root does not match the state's latest block header parent root");

        ExecutionPayloadBid bid = state.LatestExecutionPayloadBid!;
        if (envelope.BuilderIndex != bid.BuilderIndex)
            throw new BeaconStateException("Envelope builder index does not match the committed bid");
        if (payload.PrevRandao != bid.PrevRandao)
            throw new BeaconStateException("Envelope payload prev_randao does not match the committed bid");
        if (payload.GasLimit != bid.GasLimit)
            throw new BeaconStateException("Envelope payload gas limit does not match the committed bid");
        if (payload.BlockHash != bid.BlockHash)
            throw new BeaconStateException("Envelope payload block hash does not match the committed bid");
        if (SszRoots.HashTreeRoot(envelope.ExecutionRequests!) != bid.ExecutionRequestsRoot)
            throw new BeaconStateException("Envelope execution requests do not match the committed bid's execution requests root");

        if (payload.SlotNumber != state.Slot)
            throw new BeaconStateException($"Envelope payload slot number {payload.SlotNumber} does not match state slot {state.Slot}");
        if (payload.ParentHash != state.LatestBlockHash)
            throw new BeaconStateException("Envelope payload parent hash does not match the state's latest block hash");
        if (payload.Timestamp != state.ComputeTimeAtSlot(state.Slot))
            throw new BeaconStateException($"Envelope payload timestamp {payload.Timestamp} does not match slot {state.Slot}");
        if (!WithdrawalsEqual(payload.Withdrawals, state.PayloadExpectedWithdrawals))
            throw new BeaconStateException("Envelope payload withdrawals do not match state.payload_expected_withdrawals");

        Hash256?[] versionedHashes = PayloadConverter.ToBlobVersionedHashes(bid.BlobKzgCommitments);
        // Irrelevant is default(ExecutionStatus): a notifier that returns no verdict must not admit the envelope.
        ExecutionStatus executionStatus = notifier.NotifyNewPayload(payload, versionedHashes, envelope.ParentBeaconBlockRoot!, envelope.ExecutionRequests!);
        if (executionStatus is ExecutionStatus.Invalid or ExecutionStatus.Irrelevant)
            throw new BeaconStateException($"Execution payload envelope was rejected by the execution layer ({executionStatus})");
    }

    private static bool VerifyExecutionPayloadEnvelopeSignature(BeaconStateGloas state, SignedExecutionPayloadEnvelope signedEnvelope, PubkeyCache pubkeys)
    {
        ExecutionPayloadEnvelope envelope = signedEnvelope.Message!;
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(envelope), domain);

        if (envelope.BuilderIndex == Presets.BuilderIndexSelfBuild)
        {
            int proposerIndex = (int)state.LatestBlockHeader!.ProposerIndex;
            return BlsSigner.Verify(pubkeys.GetPublicKey(proposerIndex), signedEnvelope.Signature.Bytes, signingRoot.Bytes);
        }

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        Builder builder = state.Builders![(int)envelope.BuilderIndex];
        return pubkey.TryDecode(builder.Pubkey.Bytes, out _) && BlsSigner.Verify(pubkey, signedEnvelope.Signature.Bytes, signingRoot.Bytes);
    }

    /// <summary>
    /// Content equality standing in for <c>hash_tree_root(a) == hash_tree_root(b)</c>: both sides
    /// merkleize the same way (a <c>ProgressiveList[Withdrawal]</c>, see
    /// <see cref="ExecutionPayloadGloas.Withdrawals"/> and <see cref="BeaconStateGloas.PayloadExpectedWithdrawals"/>),
    /// so equal contents in equal order imply equal roots without hand-rolling that merkleization
    /// here too (see the honest-failure rule against a second source of truth for a derivable value).
    /// </summary>
    private static bool WithdrawalsEqual(Withdrawal[]? a, Withdrawal[]? b)
    {
        a ??= [];
        b ??= [];
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Index != b[i].Index || a[i].ValidatorIndex != b[i].ValidatorIndex || a[i].Address != b[i].Address || a[i].Amount != b[i].Amount)
                return false;
        }
        return true;
    }

    // ---- Parent execution payload application (EIP-7732) ----

    /// <summary>
    /// Spec <c>process_parent_execution_payload</c>: the child block declares, through its own bid's
    /// <c>parent_block_hash</c>, whether the parent's committed payload was actually delivered
    /// (matches the state's tip) or missed (an older tip). A missed parent must carry empty
    /// execution requests; a delivered one applies them.
    /// </summary>
    public static void ProcessParentExecutionPayload(BeaconStateGloas state, BeaconBlockGloas block, EpochCache cache)
    {
        ExecutionPayloadBid bid = block.Body!.SignedExecutionPayloadBid!.Message!;
        ExecutionPayloadBid parentBid = state.LatestExecutionPayloadBid!;
        ExecutionRequestsGloas requests = block.Body.ParentExecutionRequests ?? new ExecutionRequestsGloas();
        ulong parentSlot = state.LatestBlockHeader!.Slot;

        if (bid.ParentBlockHash != parentBid.BlockHash)
        {
            if (!IsEmptyExecutionRequests(requests))
                throw new BeaconStateException("Parent payload was not delivered, but the block carries non-empty parent execution requests");
            return;
        }

        if (SszRoots.HashTreeRoot(requests) != parentBid.ExecutionRequestsRoot)
            throw new BeaconStateException("Parent execution requests do not match the committed bid's execution requests root");

        ApplyParentExecutionPayload(state, requests, parentSlot, parentBid, cache);
    }

    private static bool IsEmptyExecutionRequests(ExecutionRequestsGloas requests) =>
        (requests.Deposits?.Length ?? 0) == 0
        && (requests.Withdrawals?.Length ?? 0) == 0
        && (requests.Consolidations?.Length ?? 0) == 0
        && (requests.BuilderDeposits?.Length ?? 0) == 0
        && (requests.BuilderExits?.Length ?? 0) == 0;

    /// <summary>
    /// Spec <c>apply_parent_execution_payload</c>: applies the parent's execution requests at the
    /// child's slot, settles the parent's builder payment, and advances the chain's execution tip.
    /// </summary>
    /// <remarks>
    /// The payment is read back through the window <see cref="GloasEpochProcessing.ProcessBuilderPendingPayments"/>
    /// rotates at every boundary: the upper half while the parent's epoch is still current, the lower
    /// half one epoch later, and once the entry has been evicted altogether the bid's own value is
    /// queued directly. Each branch reads a different address for the same slot, which is why the
    /// rotation and this method must agree on the layout and are tested together.
    /// </remarks>
    private static void ApplyParentExecutionPayload(BeaconStateGloas state, ExecutionRequestsGloas requests, ulong parentSlot, ExecutionPayloadBid parentBid, EpochCache cache)
    {
        VerifyExecutionRequestsLimits(requests);

        foreach (DepositRequest request in requests.Deposits ?? [])
            ProcessDepositRequest(state, request);
        foreach (WithdrawalRequest request in requests.Withdrawals ?? [])
            ProcessWithdrawalRequest(state, request, cache);
        foreach (ConsolidationRequest request in requests.Consolidations ?? [])
            ProcessConsolidationRequest(state, request, cache);
        foreach (BuilderDepositRequest request in requests.BuilderDeposits ?? [])
            ProcessBuilderDepositRequest(state, request);
        foreach (BuilderExitRequest request in requests.BuilderExits ?? [])
            ProcessBuilderExitRequest(state, request);

        ulong parentEpoch = BeaconStateAccessors.ComputeEpochAtSlot(parentSlot);
        if (parentEpoch == state.GetCurrentEpoch())
        {
            SettleBuilderPayment(state, Presets.SlotsPerEpoch + parentSlot % Presets.SlotsPerEpoch);
        }
        else if (parentEpoch == state.GetPreviousEpoch())
        {
            SettleBuilderPayment(state, parentSlot % Presets.SlotsPerEpoch);
        }
        else if (parentBid.Value > 0)
        {
            state.BuilderPendingWithdrawals = [.. state.BuilderPendingWithdrawals ?? [], new BuilderPendingWithdrawal
            {
                FeeRecipient = parentBid.FeeRecipient,
                Amount = parentBid.Value,
                BuilderIndex = parentBid.BuilderIndex,
            }];
        }

        state.ExecutionPayloadAvailability![(int)(parentSlot % Presets.SlotsPerHistoricalRoot)] = true;
        state.LatestBlockHash = parentBid.BlockHash;
    }

    /// <summary>Spec <c>verify_execution_requests_limits</c> (gloas/p2p-interface.md): every execution request count within its limit.</summary>
    /// <exception cref="BeaconStateException">A count is over its limit.</exception>
    internal static void VerifyExecutionRequestsLimits(ExecutionRequestsGloas requests)
    {
        if ((requests.Withdrawals?.Length ?? 0) > Presets.MaxWithdrawalRequestsPerPayload)
            throw new BeaconStateException("Execution requests exceed the withdrawal request limit");
        if ((requests.Consolidations?.Length ?? 0) > Presets.MaxConsolidationRequestsPerPayload)
            throw new BeaconStateException("Execution requests exceed the consolidation request limit");
        if ((requests.BuilderDeposits?.Length ?? 0) > Presets.MaxBuilderDepositRequestsPerPayload)
            throw new BeaconStateException("Execution requests exceed the builder deposit request limit");
        if ((requests.BuilderExits?.Length ?? 0) > Presets.MaxBuilderExitRequestsPerPayload)
            throw new BeaconStateException("Execution requests exceed the builder exit request limit");
    }

    /// <summary>Spec <c>settle_builder_payment</c>.</summary>
    private static void SettleBuilderPayment(BeaconStateGloas state, ulong paymentIndex)
    {
        if (paymentIndex >= (ulong)state.BuilderPendingPayments!.Length)
            throw new BeaconStateException($"Builder payment index {paymentIndex} is out of range");

        BuilderPendingPayment payment = state.BuilderPendingPayments[(int)paymentIndex];
        if (payment.Withdrawal!.Amount > 0)
            state.BuilderPendingWithdrawals = [.. state.BuilderPendingWithdrawals ?? [], payment.Withdrawal];
        state.BuilderPendingPayments[(int)paymentIndex] = EmptyBuilderPendingPayment();
    }

    /// <summary>Spec <c>process_deposit_request</c> (EIP-6110): unchanged from Fulu, ported to <see cref="BeaconStateGloas"/>.</summary>
    internal static void ProcessDepositRequest(BeaconStateGloas state, DepositRequest request)
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

    /// <summary>Spec <c>process_withdrawal_request</c> (EIP-7002/EIP-7251): unchanged from Fulu, ported to <see cref="BeaconStateGloas"/>.</summary>
    internal static void ProcessWithdrawalRequest(BeaconStateGloas state, WithdrawalRequest request, EpochCache cache)
    {
        bool isFullExitRequest = request.Amount == Presets.FullExitRequestAmount;

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
            if (pendingBalanceToWithdraw == 0)
                state.InitiateValidatorExit(index, cache);
            return;
        }

        bool hasSufficientEffectiveBalance = validator.EffectiveBalance >= Presets.MinActivationBalance;
        bool hasExcessBalance = state.Balances![index] > Presets.MinActivationBalance + pendingBalanceToWithdraw;

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

    /// <summary>Spec <c>process_consolidation_request</c> (EIP-7251): unchanged from Fulu, ported to <see cref="BeaconStateGloas"/>.</summary>
    internal static void ProcessConsolidationRequest(BeaconStateGloas state, ConsolidationRequest request, EpochCache cache)
    {
        if (IsValidSwitchToCompoundingRequest(state, request))
        {
            SwitchToCompoundingValidator(state, FindValidatorIndex(state, request.SourcePubkey)!.Value);
            return;
        }

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

    private static bool IsValidSwitchToCompoundingRequest(BeaconStateGloas state, ConsolidationRequest request)
    {
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

    private static void SwitchToCompoundingValidator(BeaconStateGloas state, int index)
    {
        Span<byte> credentials = stackalloc byte[32];
        Validator validator = state.Validators![index].Clone();
        validator.WithdrawalCredentials!.Bytes.CopyTo(credentials);
        credentials[0] = Presets.CompoundingWithdrawalPrefix;
        validator.WithdrawalCredentials = new Hash256(credentials);
        state.Validators[index] = validator;

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
    }

    /// <summary>Spec <c>process_builder_deposit_request</c> (EIP-8282, new in Gloas).</summary>
    internal static void ProcessBuilderDepositRequest(BeaconStateGloas state, BuilderDepositRequest request)
    {
        if (!GloasForkTransition.IsBuilderWithdrawalCredential(request.WithdrawalCredentials!))
            return;

        int builderIndex = Array.FindIndex(state.Builders!, b => b.Pubkey.Equals(request.Pubkey));
        if (builderIndex < 0)
        {
            if (IsValidBuilderDepositSignature(request))
            {
                GloasForkTransition.AddBuilderToRegistry(
                    state,
                    request.Pubkey,
                    Presets.PayloadBuilderVersion,
                    new Address(request.WithdrawalCredentials!.Bytes[12..]),
                    request.Amount,
                    state.Slot);
            }
            return;
        }

        Builder builder = state.Builders![builderIndex];
        ulong withdrawableEpoch = builder.WithdrawableEpoch;
        if (withdrawableEpoch != Presets.FarFutureEpoch && builder.Balance == 0)
            withdrawableEpoch = state.GetCurrentEpoch() + Presets.MinBuilderWithdrawabilityDelay;

        state.Builders[builderIndex] = new Builder
        {
            Pubkey = builder.Pubkey,
            Version = builder.Version,
            ExecutionAddress = builder.ExecutionAddress,
            Balance = builder.Balance + request.Amount,
            DepositEpoch = builder.DepositEpoch,
            WithdrawableEpoch = withdrawableEpoch,
        };
    }

    /// <summary>
    /// Spec <c>is_valid_builder_deposit_signature</c>: a proof-of-possession over
    /// <c>DOMAIN_BUILDER_DEPOSIT</c>, computed fork-agnostically (genesis fork version, zero
    /// genesis validators root) exactly like a validator deposit's own domain - a distinct domain
    /// type, not a duplicate of <see cref="Crypto.DepositSignatureVerifier"/>'s.
    /// </summary>
    private static bool IsValidBuilderDepositSignature(BuilderDepositRequest request)
    {
        DepositMessage.Merkleize(new DepositMessage
        {
            Pubkey = request.Pubkey,
            WithdrawalCredentials = request.WithdrawalCredentials,
            Amount = request.Amount,
        }, out UInt256 root);

        Hash256 domain = Domains.ComputeDomain(DomainType.BuilderDeposit, Presets.GenesisForkVersion, Hash256.Zero);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        return pubkey.TryDecode(request.Pubkey.Bytes, out _) && BlsSigner.Verify(pubkey, request.Signature.Bytes, signingRoot.Bytes);
    }

    /// <summary>Spec <c>process_builder_exit_request</c> (EIP-8282, new in Gloas).</summary>
    internal static void ProcessBuilderExitRequest(BeaconStateGloas state, BuilderExitRequest request)
    {
        int builderIndex = Array.FindIndex(state.Builders!, b => b.Pubkey.Equals(request.Pubkey));
        if (builderIndex < 0)
            return;

        if (!state.IsActiveBuilder((ulong)builderIndex))
            return;
        Builder builder = state.Builders![builderIndex];
        if (builder.ExecutionAddress != request.SourceAddress)
            return;
        if (state.GetPendingBalanceToWithdrawForBuilder((ulong)builderIndex) != 0)
            return;

        state.Builders[builderIndex] = new Builder
        {
            Pubkey = builder.Pubkey,
            Version = builder.Version,
            ExecutionAddress = builder.ExecutionAddress,
            Balance = builder.Balance,
            DepositEpoch = builder.DepositEpoch,
            WithdrawableEpoch = state.GetCurrentEpoch() + Presets.MinBuilderWithdrawabilityDelay,
        };
    }

    private static int? FindValidatorIndex(BeaconStateGloas state, BlsPublicKey pubkey)
    {
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].Pubkey == pubkey)
                return i;
        }
        return null;
    }

    // ---- Withdrawals (EIP-7732: builder balances withdraw alongside validators) ----

    /// <summary>
    /// Spec <c>process_withdrawals</c> (Gloas): computed entirely from state (no payload to echo
    /// back against, unlike pre-Gloas), and a true no-op when the parent payload was not delivered.
    /// </summary>
    public static void ProcessWithdrawals(BeaconStateGloas state)
    {
        if (state.LatestBlockHash != state.LatestExecutionPayloadBid!.BlockHash)
            return;

        (Withdrawal[] withdrawals, int processedBuilderWithdrawals, int processedPartialWithdrawals, int processedBuildersSweep) = GetExpectedWithdrawals(state);

        ApplyWithdrawals(state, withdrawals);

        if (withdrawals.Length != 0)
            state.NextWithdrawalIndex = withdrawals[^1].Index + 1;
        state.PayloadExpectedWithdrawals = withdrawals;
        state.BuilderPendingWithdrawals = state.BuilderPendingWithdrawals![processedBuilderWithdrawals..];
        state.PendingPartialWithdrawals = state.PendingPartialWithdrawals![processedPartialWithdrawals..];
        if (state.Builders!.Length > 0)
            state.NextWithdrawalBuilderIndex = (state.NextWithdrawalBuilderIndex + (ulong)processedBuildersSweep) % (ulong)state.Builders.Length;

        // Spec update_next_withdrawal_validator_index (unchanged from Electra): only the plain
        // (non-builder-flagged) validator sweep advances this cursor, and only full sweep
        // withdrawals (not the earlier builder/partial/builder-sweep stages) count toward whether
        // the sweep filled MAX_WITHDRAWALS_PER_PAYLOAD.
        ulong validatorCount = (ulong)state.Validators!.Length;
        Withdrawal? lastValidatorWithdrawal = null;
        for (int i = withdrawals.Length - 1; i >= 0; i--)
        {
            if (!IsBuilderIndex(withdrawals[i].ValidatorIndex))
            {
                lastValidatorWithdrawal = withdrawals[i];
                break;
            }
        }
        state.NextWithdrawalValidatorIndex = withdrawals.Length == Presets.MaxWithdrawalsPerPayload && lastValidatorWithdrawal is { } last
            ? (last.ValidatorIndex + 1) % validatorCount
            : (state.NextWithdrawalValidatorIndex + (ulong)Presets.MaxValidatorsPerWithdrawalsSweep) % validatorCount;
    }

    private static bool IsBuilderIndex(ulong validatorIndex) => (validatorIndex & Presets.BuilderIndexFlag) != 0;

    // The spec's registry read raises IndexError past the end, which its tests count as a failed assert.
    private static int RequireRegistryIndex(ulong index, int length, string registry) =>
        index < (ulong)length ? (int)index : throw new BeaconStateException($"{registry} index {index} is out of range ({length} entries)");
    private static ulong ToBuilderWithdrawalIndex(ulong builderIndex) => builderIndex | Presets.BuilderIndexFlag;

    /// <summary>Spec <c>get_expected_withdrawals</c> (Gloas): builder withdrawals, then pending partials, then the builders sweep, then the validators sweep.</summary>
    private static (Withdrawal[] Withdrawals, int ProcessedBuilderWithdrawals, int ProcessedPartialWithdrawals, int ProcessedBuildersSweep) GetExpectedWithdrawals(BeaconStateGloas state)
    {
        ulong withdrawalIndex = state.NextWithdrawalIndex;
        List<Withdrawal> withdrawals = [];

        int processedBuilderWithdrawals = GetBuilderWithdrawals(state, ref withdrawalIndex, withdrawals);
        int processedPartialWithdrawals = GetPendingPartialWithdrawals(state, ref withdrawalIndex, withdrawals);
        int processedBuildersSweep = GetBuildersSweepWithdrawals(state, ref withdrawalIndex, withdrawals);
        GetValidatorsSweepWithdrawals(state, ref withdrawalIndex, withdrawals);

        return ([.. withdrawals], processedBuilderWithdrawals, processedPartialWithdrawals, processedBuildersSweep);
    }

    /// <summary>Spec <c>get_builder_withdrawals</c> (new in Gloas).</summary>
    private static int GetBuilderWithdrawals(BeaconStateGloas state, ref ulong withdrawalIndex, List<Withdrawal> accumulated)
    {
        int limit = Presets.MaxWithdrawalsPerPayload - 1;
        int processedCount = 0;
        foreach (BuilderPendingWithdrawal withdrawal in state.BuilderPendingWithdrawals ?? [])
        {
            if (accumulated.Count >= limit)
                break;

            accumulated.Add(new Withdrawal
            {
                Index = withdrawalIndex++,
                ValidatorIndex = ToBuilderWithdrawalIndex(withdrawal.BuilderIndex),
                Address = withdrawal.FeeRecipient,
                Amount = withdrawal.Amount,
            });
            processedCount++;
        }
        return processedCount;
    }

    /// <summary>Spec <c>get_builders_sweep_withdrawals</c> (new in Gloas).</summary>
    private static int GetBuildersSweepWithdrawals(BeaconStateGloas state, ref ulong withdrawalIndex, List<Withdrawal> accumulated)
    {
        Builder[] builders = state.Builders!;
        if (builders.Length == 0)
            return 0;

        ulong epoch = state.GetCurrentEpoch();
        int buildersLimit = Math.Min(builders.Length, Presets.MaxBuildersPerWithdrawalsSweep);
        int withdrawalsLimit = Presets.MaxWithdrawalsPerPayload - 1;

        int processedCount = 0;
        ulong builderIndex = state.NextWithdrawalBuilderIndex;
        for (int i = 0; i < buildersLimit; i++)
        {
            if (accumulated.Count >= withdrawalsLimit)
                break;

            Builder builder = builders[RequireRegistryIndex(builderIndex, builders.Length, "Builder")];
            if (builder.WithdrawableEpoch <= epoch && builder.Balance > 0)
            {
                accumulated.Add(new Withdrawal
                {
                    Index = withdrawalIndex++,
                    ValidatorIndex = ToBuilderWithdrawalIndex(builderIndex),
                    Address = builder.ExecutionAddress,
                    Amount = builder.Balance,
                });
            }
            builderIndex = (builderIndex + 1) % (ulong)builders.Length;
            processedCount++;
        }
        return processedCount;
    }

    /// <summary>Spec <c>get_pending_partial_withdrawals</c> (Electra, unmodified in Gloas): the shared budget's third argument is the only Gloas-era change (it now shares the overall withdrawal cap with the builder stages ahead of it).</summary>
    private static int GetPendingPartialWithdrawals(BeaconStateGloas state, ref ulong withdrawalIndex, List<Withdrawal> accumulated)
    {
        ulong epoch = state.GetCurrentEpoch();
        int withdrawalsLimit = Math.Min(accumulated.Count + Presets.MaxPendingPartialsPerWithdrawalsSweep, Presets.MaxWithdrawalsPerPayload - 1);

        int processedCount = 0;
        foreach (PendingPartialWithdrawal pending in state.PendingPartialWithdrawals!)
        {
            if (pending.WithdrawableEpoch > epoch || accumulated.Count >= withdrawalsLimit)
                break;

            int index = RequireRegistryIndex(pending.ValidatorIndex, state.Validators!.Length, "Validator");
            Validator validator = state.Validators[index];
            ulong balance = state.Balances![index] - TotalWithdrawn(accumulated, pending.ValidatorIndex);
            bool isEligible = validator.ExitEpoch == Presets.FarFutureEpoch && validator.EffectiveBalance >= Presets.MinActivationBalance && balance > Presets.MinActivationBalance;
            if (isEligible)
            {
                accumulated.Add(new Withdrawal
                {
                    Index = withdrawalIndex++,
                    ValidatorIndex = pending.ValidatorIndex,
                    Address = new Address(validator.WithdrawalCredentials!),
                    Amount = Math.Min(balance - Presets.MinActivationBalance, pending.Amount),
                });
            }
            processedCount++;
        }
        return processedCount;
    }

    /// <summary>Spec <c>get_validators_sweep_withdrawals</c> (Electra, unmodified in Gloas): the final stage, which alone is allowed to fill the full <c>MAX_WITHDRAWALS_PER_PAYLOAD</c> budget.</summary>
    private static void GetValidatorsSweepWithdrawals(BeaconStateGloas state, ref ulong withdrawalIndex, List<Withdrawal> accumulated)
    {
        ulong epoch = state.GetCurrentEpoch();
        int bound = Math.Min(state.Validators!.Length, Presets.MaxValidatorsPerWithdrawalsSweep);
        ulong validatorIndex = state.NextWithdrawalValidatorIndex;

        for (int i = 0; i < bound; i++)
        {
            if (accumulated.Count >= Presets.MaxWithdrawalsPerPayload)
                break;

            int index = RequireRegistryIndex(validatorIndex, state.Validators.Length, "Validator");
            Validator validator = state.Validators[index];
            ulong balance = state.Balances![index] - TotalWithdrawn(accumulated, validatorIndex);
            if (validator.IsFullyWithdrawableValidator(balance, epoch))
            {
                accumulated.Add(new Withdrawal
                {
                    Index = withdrawalIndex++,
                    ValidatorIndex = validatorIndex,
                    Address = new Address(validator.WithdrawalCredentials!),
                    Amount = balance,
                });
            }
            else if (validator.IsPartiallyWithdrawableValidator(balance))
            {
                accumulated.Add(new Withdrawal
                {
                    Index = withdrawalIndex++,
                    ValidatorIndex = validatorIndex,
                    Address = new Address(validator.WithdrawalCredentials!),
                    Amount = balance - validator.GetMaxEffectiveBalance(),
                });
            }
            validatorIndex = (validatorIndex + 1) % (ulong)state.Validators.Length;
        }
    }

    private static ulong TotalWithdrawn(List<Withdrawal> withdrawals, ulong validatorIndex)
    {
        ulong total = 0;
        foreach (Withdrawal withdrawal in withdrawals)
        {
            if (withdrawal.ValidatorIndex == validatorIndex)
                total += withdrawal.Amount;
        }
        return total;
    }

    /// <summary>Spec <c>apply_withdrawals</c> (Gloas): a builder-flagged withdrawal debits the builder registry instead of a validator balance, and saturates rather than throwing on underflow.</summary>
    private static void ApplyWithdrawals(BeaconStateGloas state, Withdrawal[] withdrawals)
    {
        foreach (Withdrawal withdrawal in withdrawals)
        {
            if (IsBuilderIndex(withdrawal.ValidatorIndex))
            {
                int builderIndex = RequireRegistryIndex(withdrawal.ValidatorIndex & ~Presets.BuilderIndexFlag, state.Builders!.Length, "Builder");
                Builder builder = state.Builders[builderIndex];
                ulong balance = builder.Balance;
                ulong saturated = balance - Math.Min(balance, withdrawal.Amount);
                state.Builders[builderIndex] = new Builder
                {
                    Pubkey = builder.Pubkey,
                    Version = builder.Version,
                    ExecutionAddress = builder.ExecutionAddress,
                    Balance = saturated,
                    DepositEpoch = builder.DepositEpoch,
                    WithdrawableEpoch = builder.WithdrawableEpoch,
                };
            }
            else
            {
                state.DecreaseBalance((int)withdrawal.ValidatorIndex, withdrawal.Amount);
            }
        }
    }
}
