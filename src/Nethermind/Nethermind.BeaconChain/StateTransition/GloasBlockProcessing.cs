// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Spec;
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
/// Ported from ethereum/consensus-specs commit <c>a8475719ce77cb269191e327e1f4175c295851ee</c> on
/// the <c>master</c> branch (<c>specs/gloas/beacon-chain.md</c>, fetched 2026-09-20; also
/// cross-referenced against <c>specs/gloas/fork-choice.md</c> at the same commit for
/// <c>on_execution_payload_envelope</c>). That commit postdates the <c>v1.7.0-beta.0</c> tag (no
/// <c>v1.7.0-beta.1</c> exists yet), consistent with <c>GloasContainers.cs</c>'s own pin. The
/// spec is explicitly work in progress; this file documents every place its text was ambiguous or
/// (as found for the block-processing step order) inconsistent with a paraphrase, and the reading
/// taken, rather than picking silently.
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
/// <b>Scope actually covered here</b> (see the task's stated order of value): block header and
/// RANDAO, bid processing, envelope verification, withdrawals computed from state, and the builder
/// payment/deposit/exit machinery - including a parent payload whose execution requests are
/// non-empty, via the full deposit/withdrawal/consolidation/builder-deposit/builder-exit request
/// pipeline in <see cref="ApplyParentExecutionPayload"/> - are all fully implemented, with the
/// payment window addressed exactly as the spec does now that <see cref="GloasEpochProcessing"/>
/// rotates it at every epoch boundary.
/// Left as a declared, by-name gap rather than a partial or approximated implementation: attester/
/// proposer slashings, attestations, voluntary exits, BLS-to-execution changes and payload
/// attestations inside a Gloas block body - attestation and PTC processing pull in the whole
/// committee-shuffling stack, a separate large piece of work or of scope even than the rest of this.
/// Every one of these throws <see cref="NotSupportedException"/> by name rather than silently
/// skipping the step.
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
        ProcessOperations(state, body, parentSlot);
        ProcessSyncAggregate(state, body.SyncAggregate!, cache, verifySignatures);
    }

    /// <summary>Verifies a Gloas block's outer proposer signature - not part of <c>process_block</c> itself, called once by the top-level state transition.</summary>
    public static bool VerifyProposerSignature(BeaconStateGloas state, SignedBeaconBlockGloas signedBlock, PubkeyCache pubkeys)
    {
        BeaconBlockGloas block = signedBlock.Message!;
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), domain);
        return BlsSigner.Verify(pubkeys.GetPublicKey((int)block.ProposerIndex), signedBlock.Signature.Bytes, signingRoot.Bytes);
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
    /// consolidation-request dispatch is gone (moved to <see cref="ApplyParentExecutionPayload"/>);
    /// payload attestations are added. See this type's remarks for exactly which operation kinds
    /// this method processes versus rejects by name.
    /// </summary>
    public static void ProcessOperations(BeaconStateGloas state, BeaconBlockBodyGloas body, ulong parentSlot)
    {
        if ((body.Deposits?.Length ?? 0) != 0)
            throw new BeaconStateException("Gloas block body must carry zero deposits (EIP-6110: the Eth1 deposit path is fully retired)");

        RejectIfPresent(body.ProposerSlashings, Presets.MaxProposerSlashings, "proposer slashings");
        RejectIfPresent(body.AttesterSlashings, Presets.MaxAttesterSlashingsElectra, "attester slashings");
        RejectIfPresent(body.Attestations, Presets.MaxAttestationsElectra, "attestations");
        RejectIfPresent(body.VoluntaryExits, Presets.MaxVoluntaryExits, "voluntary exits");
        RejectIfPresent(body.BlsToExecutionChanges, Presets.MaxBlsToExecutionChanges, "BLS-to-execution changes");
        RejectIfPresent(body.PayloadAttestations, Presets.MaxPayloadAttestations, "payload attestations");
    }

    /// <summary>
    /// Enforces the operation's spec length bound (still real state-transition behavior: an
    /// oversized list must reject the block) and then, for a genuinely non-empty list, fails loudly
    /// by name instead of silently skipping operations this driver does not process (see this
    /// type's remarks). An empty list is a true no-op, not a gap.
    /// </summary>
    private static void RejectIfPresent<T>(T[]? operations, int limit, string name)
    {
        int count = operations?.Length ?? 0;
        if (count > limit)
            throw new BeaconStateException($"Block has {count} {name}, exceeding the limit of {limit}");
        if (count > 0)
            throw new NotSupportedException($"Gloas block processing of {name} is not implemented (needs the committee/shuffling stack this driver does not carry for Gloas state)");
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

        ulong maxBlobsPerBlock = spec.GetBlobParameters(state.GetCurrentEpoch())!.Value.MaxBlobsPerBlock;
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
        if (!notifier.NotifyNewPayload(payload, versionedHashes, envelope.ParentBeaconBlockRoot!, envelope.ExecutionRequests!))
            throw new BeaconStateException("Execution payload envelope was rejected by the execution layer");
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
        if ((requests.Withdrawals?.Length ?? 0) > Presets.MaxWithdrawalRequestsPerPayload)
            throw new BeaconStateException("Parent execution requests exceed the withdrawal request limit");
        if ((requests.Consolidations?.Length ?? 0) > Presets.MaxConsolidationRequestsPerPayload)
            throw new BeaconStateException("Parent execution requests exceed the consolidation request limit");
        if ((requests.BuilderDeposits?.Length ?? 0) > Presets.MaxBuilderDepositRequestsPerPayload)
            throw new BeaconStateException("Parent execution requests exceed the builder deposit request limit");
        if ((requests.BuilderExits?.Length ?? 0) > Presets.MaxBuilderExitRequestsPerPayload)
            throw new BeaconStateException("Parent execution requests exceed the builder exit request limit");

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

    /// <summary>Spec <c>settle_builder_payment</c>.</summary>
    private static void SettleBuilderPayment(BeaconStateGloas state, ulong paymentIndex)
    {
        if (paymentIndex >= (ulong)state.BuilderPendingPayments!.Length)
            throw new BeaconStateException($"Builder payment index {paymentIndex} is out of range");

        BuilderPendingPayment payment = state.BuilderPendingPayments[(int)paymentIndex];
        if (payment.Withdrawal!.Amount > 0)
            state.BuilderPendingWithdrawals = [.. state.BuilderPendingWithdrawals ?? [], payment.Withdrawal];
        state.BuilderPendingPayments[(int)paymentIndex] = new BuilderPendingPayment { Withdrawal = new BuilderPendingWithdrawal() };
    }

    /// <summary>Spec <c>process_deposit_request</c> (EIP-6110): unchanged from Fulu, ported to <see cref="BeaconStateGloas"/>.</summary>
    private static void ProcessDepositRequest(BeaconStateGloas state, DepositRequest request)
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
    private static void ProcessWithdrawalRequest(BeaconStateGloas state, WithdrawalRequest request, EpochCache cache)
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
    private static void ProcessConsolidationRequest(BeaconStateGloas state, ConsolidationRequest request, EpochCache cache)
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
    private static void ProcessBuilderDepositRequest(BeaconStateGloas state, BuilderDepositRequest request)
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
    private static void ProcessBuilderExitRequest(BeaconStateGloas state, BuilderExitRequest request)
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

            Builder builder = builders[(int)builderIndex];
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

            Validator validator = state.Validators![(int)pending.ValidatorIndex];
            ulong balance = state.Balances![(int)pending.ValidatorIndex] - TotalWithdrawn(accumulated, pending.ValidatorIndex);
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

            Validator validator = state.Validators[(int)validatorIndex];
            ulong balance = state.Balances![(int)validatorIndex] - TotalWithdrawn(accumulated, validatorIndex);
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
                int builderIndex = (int)(withdrawal.ValidatorIndex & ~Presets.BuilderIndexFlag);
                Builder builder = state.Builders![builderIndex];
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
