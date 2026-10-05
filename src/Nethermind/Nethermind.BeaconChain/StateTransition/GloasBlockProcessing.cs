// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>Gloas block and payload processing (EIP-7732).</summary>
/// <remarks>
/// Follows ethereum/consensus-specs <c>v1.7.0-beta.2</c>, <c>specs/gloas/beacon-chain.md</c> and
/// <c>specs/gloas/fork-choice.md</c> at the same tag.
/// <para/>
/// Withdrawals run after the parent payload and before the current bid, because bid processing
/// affects validator balances.
/// <para/>
/// <see cref="VerifyExecutionPayloadEnvelope"/> leaves beacon state unchanged; fork choice records
/// the envelope. The child block applies it through <see cref="ApplyParentExecutionPayload"/>.
/// </remarks>
public static partial class GloasBlockProcessing
{
    /// <summary>Spec <c>process_block</c> (Gloas). See this type's remarks for the step order and why.</summary>
    /// <remarks>The operation signatures are verified as one <see cref="BlockSignatureBatch"/>, with the serial verdicts and messages.</remarks>
    public static void ProcessBlock(BeaconStateGloas state, BeaconBlockGloas block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, BeaconChainSpec spec, bool verifySignatures = true)
    {
        if (verifySignatures)
            BlockSignatureBatch.Run(batch => ProcessBlock(state, block, cache, pubkeys, notifier, spec, verifySignatures: true, batch));
        else
            ProcessBlock(state, block, cache, pubkeys, notifier, spec, verifySignatures: false, batch: null);
    }

    /// <summary>Spec <c>process_block</c> (Gloas), deferring the signatures to <paramref name="batch"/> or, when it is <c>null</c>, verifying each at its own step.</summary>
    internal static void ProcessBlock(BeaconStateGloas state, BeaconBlockGloas block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, BeaconChainSpec spec, bool verifySignatures, BlockSignatureBatch? batch)
    {
        BeaconBlockBodyGloas body = block.Body!;
        ulong parentSlot = state.LatestBlockHeader!.Slot;

        ProcessParentExecutionPayload(state, block, cache);
        ProcessBlockHeader(state, block);
        ProcessWithdrawals(state);
        ProcessExecutionPayloadBid(state, body.SignedExecutionPayloadBid!, spec, pubkeys, verifySignatures, batch);
        ProcessRandao(state, body, pubkeys, verifySignatures, batch);
        ProcessEth1Data(state, body);
        ProcessOperations(state, body, parentSlot, spec, cache, pubkeys, verifySignatures, batch);
        ProcessSyncAggregate(state, body.SyncAggregate!, cache, pubkeys, verifySignatures, batch);
    }

    /// <summary>Verifies a Gloas block's outer proposer signature - not part of <c>process_block</c> itself, called once by the top-level state transition.</summary>
    /// <exception cref="BeaconStateException">The proposer index is not a validator of <paramref name="state"/>, or <paramref name="pubkeys"/> has no key for it.</exception>
    public static bool VerifyProposerSignature(BeaconStateGloas state, SignedBeaconBlockGloas signedBlock, PubkeyCache pubkeys)
    {
        BeaconBlockGloas block = signedBlock.Message!;
        // verify_block_signature indexes state.validators with the untrusted proposer_index (p2p beacon_block: [REJECT] a valid validator index).
        ulong proposerIndex = block.ProposerIndex;
        if (proposerIndex >= (ulong)state.Validators!.Length)
            // ethereum/consensus-specs gloas/p2p-interface.md: "[REJECT] The proposer index is a valid validator index".
            throw new BeaconStateException($"Block proposer index {proposerIndex} is not a validator index (registry size {state.Validators.Length})") { RejectGossip = true };
        // Epoch processing inside process_slots can grow the registry past the cache.
        if (proposerIndex >= (ulong)pubkeys.Count)
            throw new BeaconStateException($"Block proposer index {proposerIndex} has no cached public key ({pubkeys.Count} cached)");
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), domain);
        return pubkeys.TryGetValidPublicKey((int)proposerIndex, out G1Affine key) && BlsSigner.Verify(key, signedBlock.Signature.Bytes, signingRoot.Bytes);
    }

    /// <summary>Spec <c>process_block_header</c>: unchanged from Fulu except for the Gloas block/body types.</summary>
    public static partial void ProcessBlockHeader(BeaconStateGloas state, BeaconBlockGloas block);
    public static partial void ProcessRandao(BeaconStateGloas state, BeaconBlockBodyGloas body, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null);

    private static bool VerifyRandaoReveal(BeaconStateGloas state, ulong proposerIndex, ulong epoch, BlsSignature reveal, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral)
    {
        Span<byte> epochRoot = stackalloc byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(epochRoot, epoch);
        Hash256 domain = state.GetDomain(DomainType.Randao, epoch);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(epochRoot), domain);
        return SignatureSets.TryGetValidatorKey(pubkeys, proposerIndex, out G1Affine key) && BlockSignatureBatch.Verify(key, reveal, signingRoot, deferral);
    }

    /// <summary>Spec <c>process_eth1_data</c>: unchanged from Fulu except for the Gloas body type.</summary>
    public static partial void ProcessEth1Data(BeaconStateGloas state, BeaconBlockBodyGloas body);

    /// <summary>
    /// Spec <c>process_operations</c> (Gloas): the deposit-request/withdrawal-request/
    /// consolidation-request dispatch is gone (moved to <see cref="ApplyParentExecutionPayload"/>),
    /// payload attestations are added, and attestations learn the parent block's slot.
    /// </summary>
    public static void ProcessOperations(BeaconStateGloas state, BeaconBlockBodyGloas body, ulong parentSlot, BeaconChainSpec spec, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true, BlockSignatureBatch? batch = null)
    {
        VerifyBlockBodyOperationLimits(body);

        foreach (ProposerSlashing slashing in body.ProposerSlashings ?? [])
        {
            ProcessProposerSlashing(state, slashing, cache, pubkeys, verifySignatures, batch);
        }
        foreach (AttesterSlashingGloas slashing in body.AttesterSlashings ?? [])
        {
            ProcessAttesterSlashing(state, slashing, cache, pubkeys, verifySignatures, batch);
        }
        foreach (AttestationGloas attestation in body.Attestations ?? [])
        {
            ProcessAttestation(state, attestation, parentSlot, cache, pubkeys, verifySignatures, batch);
        }
        foreach (SignedVoluntaryExit exit in body.VoluntaryExits ?? [])
        {
            ProcessVoluntaryExit(state, exit, cache, pubkeys, verifySignatures, batch);
        }
        foreach (SignedBlsToExecutionChange change in body.BlsToExecutionChanges ?? [])
        {
            ProcessBlsToExecutionChange(state, change, verifySignatures, batch);
        }
        foreach (PayloadAttestation attestation in body.PayloadAttestations ?? [])
        {
            ProcessPayloadAttestation(state, attestation, spec, pubkeys, verifySignatures, batch);
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
    public static void ProcessPayloadAttestation(BeaconStateGloas state, PayloadAttestation attestation, BeaconChainSpec spec, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null)
    {
        PayloadAttestationData data = attestation.Data!;
        if (data.BeaconBlockRoot != state.LatestBlockHeader!.ParentRoot)
            throw new BeaconStateException("Payload attestation is not for the parent beacon block");
        if (data.Slot + 1 != state.Slot)
            throw new BeaconStateException($"Payload attestation for slot {data.Slot} is not for the slot before {state.Slot}");

        const string invalidAttestation = "Invalid indexed payload attestation";
        IndexedPayloadAttestation indexed = state.GetIndexedPayloadAttestation(attestation, spec);
        if (!IsValidIndexedPayloadAttestation(state, indexed, pubkeys, verifySignature, batch?.Defer(invalidAttestation)))
            throw new BeaconStateException(invalidAttestation);
    }

    /// <summary>
    /// Spec <c>is_valid_indexed_payload_attestation</c>: indices must be non-empty, sorted (a PTC
    /// is sampled with replacement, so repeats are legitimate) and in range, and the aggregate
    /// signature must verify.
    /// </summary>
    /// <param name="deferral">Defers the signature to a batch; <c>null</c> verifies it now.</param>
    public static bool IsValidIndexedPayloadAttestation(BeaconStateGloas state, IndexedPayloadAttestation attestation, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch.Deferral? deferral = null)
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
        return !verifySignature || GloasSignatureSets.VerifyIndexedPayloadAttestation(state, attestation, pubkeys, deferral);
    }

    public static partial void ProcessProposerSlashing(BeaconStateGloas state, ProposerSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true, BlockSignatureBatch? batch = null);
    private static partial bool HeaderEquals(BeaconBlockHeader a, BeaconBlockHeader b);

    /// <summary>Spec <c>BuilderPendingPayment.empty()</c>, in the shape <see cref="GloasEpochProcessing.ProcessBuilderPendingPayments"/> and <see cref="SettleBuilderPayment"/> write.</summary>
    private static BuilderPendingPayment EmptyBuilderPendingPayment() =>
        new() { Withdrawal = new BuilderPendingWithdrawal() };

    public static partial void ProcessAttesterSlashing(BeaconStateGloas state, AttesterSlashingGloas slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true, BlockSignatureBatch? batch = null);
    public static partial bool IsValidIndexedAttestation(BeaconStateGloas state, IndexedAttestationGloas attestation, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch.Deferral? deferral = null);

    /// <summary>
    /// Spec <c>process_attestation</c> (Gloas): the Electra aggregate validation, participation
    /// flags and proposer reward, with two EIP-7732 changes - <c>data.index</c> now encodes the
    /// attested block's payload status (0 absent, 1 present) instead of a committee index, and a
    /// validator's first participation in the target epoch, when it votes for the block proposed
    /// at the attestation slot, adds its effective balance to the weight of that slot's pending
    /// builder payment (the PTC-quorum the epoch transition honors the payment against).
    /// </summary>
    /// <param name="parentSlot">The slot of the block's parent, where the attested block's payload availability is tracked.</param>
    public static partial void ProcessAttestation(BeaconStateGloas state, AttestationGloas attestation, ulong parentSlot, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null);

    /// <summary>
    /// Spec <c>get_attestation_participation_flag_indices</c> (Gloas), returned as a bitmask over
    /// the participation flag indices. The head flag additionally requires the attestation's
    /// payload status to match: trivially for a vote for the block proposed at the attestation
    /// slot (which must then carry index 0), otherwise against the availability recorded at
    /// <paramref name="parentSlot"/>.
    /// </summary>
    /// <exception cref="BeaconStateException">The source does not match the justified checkpoint, or a same-slot vote carries a non-zero index.</exception>
    private static partial byte GetAttestationParticipationFlagIndices(BeaconStateGloas state, AttestationData data, ulong inclusionDelay, ulong parentSlot);
    public static partial void ProcessVoluntaryExit(BeaconStateGloas state, SignedVoluntaryExit signedExit, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null);
    public static partial void ProcessBlsToExecutionChange(BeaconStateGloas state, SignedBlsToExecutionChange signedChange, bool verifySignature = true, BlockSignatureBatch? batch = null);
    public static partial void ProcessSyncAggregate(BeaconStateGloas state, SyncAggregate syncAggregate, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null);

    private static bool VerifySyncAggregate(BeaconStateGloas state, SyncAggregate syncAggregate, int[] committeeIndices, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral)
    {
        Bls.P1 participants = new(stackalloc long[Bls.P1.Sz]);
        int participantCount = SignatureSets.AggregateSyncParticipants(syncAggregate.SyncCommitteeBits!, state.CurrentSyncCommittee!.Pubkeys!, committeeIndices, pubkeys, participants);
        if (participantCount < 0)
            return false;
        if (participantCount == 0)
            return syncAggregate.SyncCommitteeSignature.Bytes.SequenceEqual(SignatureSets.G2PointAtInfinity);

        ulong previousSlot = Math.Max(state.Slot, 1) - 1;
        Hash256 domain = state.GetDomain(DomainType.SyncCommittee, BeaconStateAccessors.ComputeEpochAtSlot(previousSlot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(state.GetBlockRootAtSlot(previousSlot), domain);
        return BlockSignatureBatch.Verify(participants.ToAffine(), syncAggregate.SyncCommitteeSignature, signingRoot, deferral);
    }

    // ---- Execution payload bid (EIP-7732) ----

    /// <summary>
    /// Spec <c>process_execution_payload_bid</c>: verifies the builder (or the self-build sentinel),
    /// verifies the bid is consistent with the chain tip, records the pending payment, and commits
    /// the bid as <c>state.latest_execution_payload_bid</c>.
    /// </summary>
    public static void ProcessExecutionPayloadBid(BeaconStateGloas state, SignedExecutionPayloadBid signedBid, BeaconChainSpec spec, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null)
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
            RequireRegistryIndex(builderIndex, state.Builders!.Length, "Builder");
            if (!state.IsActiveBuilder(builderIndex))
                throw new BeaconStateException($"Builder {builderIndex} is not active");
            if (state.Builders![(int)builderIndex].Version != Presets.PayloadBuilderVersion)
                throw new BeaconStateException($"Builder {builderIndex} is not registered as a payload builder");
            if (!state.CanBuilderCoverBid(builderIndex, amount))
                throw new BeaconStateException($"Builder {builderIndex} cannot cover a bid of {amount}");
            const string invalidSignature = "Invalid execution payload bid signature";
            if (verifySignature && !VerifyExecutionPayloadBidSignature(state, signedBid, batch?.Defer(invalidSignature)))
                throw new BeaconStateException(invalidSignature);
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
            // ethereum/consensus-specs gloas/p2p-interface.md: "[REJECT] If the parent is not full, the bid builds on the parent's execution head".
            throw new BeaconStateException("Bid parent block hash does not match the state's latest block hash") { RejectGossip = true };
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

    private static bool VerifyExecutionPayloadBidSignature(BeaconStateGloas state, SignedExecutionPayloadBid signedBid, BlockSignatureBatch.Deferral? deferral)
    {
        Builder builder = state.Builders![(int)signedBid.Message!.BuilderIndex];
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(signedBid.Message), domain);

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        return BlsSignatureSet.TryKeyValidate(builder.Pubkey.Bytes, pubkey) && BlockSignatureBatch.Verify(pubkey, signedBid.Signature, signingRoot, deferral);
    }

    // ---- Execution payload envelope (EIP-7732) ----

    /// <summary>
    /// Verifies an envelope against its block's frozen post-state without mutating it
    /// (spec <c>verify_execution_payload_envelope</c>, called by <c>on_execution_payload_envelope</c>).
    /// </summary>
    /// <remarks>
    /// Resolving through <paramref name="states"/> rejects unknown block roots. Accepting arbitrary
    /// caller-supplied state would let a fabricated state/envelope pair pass every field check.
    /// </remarks>
    /// <param name="hasher">Computes the state root the envelope's block root is checked against; <c>null</c> merkleizes the whole state.</param>
    /// <exception cref="BeaconStateException">The envelope names an unknown block, or fails any spec check.</exception>
    public static void VerifyExecutionPayloadEnvelope(IGloasBlockStateProvider states, SignedExecutionPayloadEnvelope signedEnvelope, INewPayloadNotifier notifier, PubkeyCache pubkeys, IBeaconStateHasher? hasher = null)
    {
        Hash256 blockRoot = signedEnvelope.Message!.BeaconBlockRoot ?? throw new BeaconStateException("Envelope carries no beacon block root");
        BeaconStateGloas state = states.GetGloasBlockState(blockRoot)
            ?? throw new BeaconStateException($"Envelope names beacon block {blockRoot}, whose post-state is not known");
        VerifyExecutionPayloadEnvelopeAgainst(state, signedEnvelope, notifier, pubkeys, hasher);
    }

    /// <summary>Verifies against the frozen state resolved by <see cref="VerifyExecutionPayloadEnvelope"/>.</summary>
    private static void VerifyExecutionPayloadEnvelopeAgainst(BeaconStateGloas state, SignedExecutionPayloadEnvelope signedEnvelope, INewPayloadNotifier notifier, PubkeyCache pubkeys, IBeaconStateHasher? hasher)
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
            StateRoot = hasher?.HashTreeRoot(state) ?? SszRoots.HashTreeRoot(state),
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

    /// <summary>Whether the envelope's builder, or the self-building proposer, signed it under <c>DOMAIN_BEACON_BUILDER</c>; a builder index outside the registry fails.</summary>
    internal static bool VerifyExecutionPayloadEnvelopeSignature(BeaconStateGloas state, SignedExecutionPayloadEnvelope signedEnvelope, PubkeyCache pubkeys)
    {
        if (signedEnvelope.Message!.BuilderIndex != Presets.BuilderIndexSelfBuild && signedEnvelope.Message.BuilderIndex >= (ulong)state.Builders!.Length)
        {
            return false;
        }

        ExecutionPayloadEnvelope envelope = signedEnvelope.Message!;
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(envelope), domain);

        if (envelope.BuilderIndex == Presets.BuilderIndexSelfBuild)
        {
            ulong proposerIndex = state.LatestBlockHeader!.ProposerIndex;
            return SignatureSets.TryGetValidatorKey(pubkeys, proposerIndex, out G1Affine proposerKey) && BlsSigner.Verify(proposerKey, signedEnvelope.Signature.Bytes, signingRoot.Bytes);
        }

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        Builder builder = state.Builders![(int)envelope.BuilderIndex];
        return BlsSignatureSet.TryKeyValidate(builder.Pubkey.Bytes, pubkey) && BlsSigner.Verify(pubkey, signedEnvelope.Signature.Bytes, signingRoot.Bytes);
    }

    /// <summary>
    /// Compares ordered contents instead of roots: both <see cref="ExecutionPayloadGloas.Withdrawals"/>
    /// and <see cref="BeaconStateGloas.PayloadExpectedWithdrawals"/> use <c>ProgressiveList[Withdrawal]</c>.
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

    internal static partial void ProcessDepositRequest(BeaconStateGloas state, DepositRequest request);
    internal static partial void ProcessWithdrawalRequest(BeaconStateGloas state, WithdrawalRequest request, EpochCache cache);
    internal static partial void ProcessConsolidationRequest(BeaconStateGloas state, ConsolidationRequest request, EpochCache cache);
    private static partial bool IsValidSwitchToCompoundingRequest(BeaconStateGloas state, ConsolidationRequest request);
    private static partial void SwitchToCompoundingValidator(BeaconStateGloas state, int index);

    /// <summary>Spec <c>process_builder_deposit_request</c> (EIP-8282, new in Gloas).</summary>
    internal static void ProcessBuilderDepositRequest(BeaconStateGloas state, BuilderDepositRequest request)
    {
        if (!GloasForkTransition.IsBuilderWithdrawalCredential(request.WithdrawalCredentials!))
            return;

        int builderIndex = Array.FindIndex(state.Builders!, b => b.Pubkey.Equals(request.Pubkey));
        if (builderIndex < 0)
        {
            if (IsValidBuilderDepositSignature(state.GenesisValidatorsRoot!, request))
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
        if (request.Amount > ulong.MaxValue - builder.Balance)
            throw new BeaconStateException("Builder deposit would overflow the balance");
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
    private static bool IsValidBuilderDepositSignature(Hash256 genesisValidatorsRoot, BuilderDepositRequest request)
    {
        DepositMessage.Merkleize(new DepositMessage
        {
            Pubkey = request.Pubkey,
            WithdrawalCredentials = request.WithdrawalCredentials,
            Amount = request.Amount,
        }, out UInt256 root);

        Hash256 domain = Domains.ComputeDomain(DomainType.BuilderDeposit, BeaconChainSpec.ForGenesisValidatorsRoot(genesisValidatorsRoot).GenesisForkVersion, Hash256.Zero);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        return BlsSignatureSet.TryKeyValidate(request.Pubkey.Bytes, pubkey) && BlsSigner.Verify(pubkey, request.Signature.Bytes, signingRoot.Bytes);
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

    private static partial int? FindValidatorIndex(BeaconStateGloas state, BlsPublicKey pubkey);

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

    private static partial ulong TotalWithdrawn(List<Withdrawal> withdrawals, ulong validatorIndex);

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
