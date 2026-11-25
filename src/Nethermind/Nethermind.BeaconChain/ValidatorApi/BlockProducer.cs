// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using CkzgLib;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using ExecutionPayload = Nethermind.BeaconChain.Types.ExecutionPayload;

namespace Nethermind.BeaconChain.ValidatorApi;

/// <summary>Builds the unsigned Fulu blocks <see cref="IBeaconNodeValidatorApi.ProduceBlock"/> serves (phase0/validator.md "Block proposal").</summary>
/// <remarks>
/// <para>
/// A block is built on the head state processed to its slot, which <see cref="BlockImporter.AdvanceForProposal"/> copies, with the payload
/// the local execution layer builds. That build starts in <see cref="PreparePayloadAsync"/>, two thirds into the slot before, when a prepared
/// validator proposes the next slot; otherwise it starts when the block is asked for and the execution layer has no time to fill it.
/// </para>
/// <para>
/// Attestations are packed greedily, most attesters first, from the on-chain aggregates of the <see cref="BlockAttestationPool"/>: each is run
/// through <c>process_attestation</c> on a copy of the block's state, and one is kept only when it passes and sets a participation flag the
/// state does not have yet, up to <c>MAX_ATTESTATIONS_ELECTRA</c>. The sync aggregate combines the best contribution held of each
/// subcommittee for the previous slot and the parent root, and is kept only when <c>process_sync_aggregate</c> accepts it with its
/// signature. Slashings, voluntary exits and BLS-to-execution changes are left empty: the node pools none.
/// </para>
/// <para>
/// The state root comes from running the block through the state transition without signature checks. A block that fails it is built again
/// without attestations and sync aggregate; one that fails again is refused, so a block that fails its own transition is never handed out.
/// </para>
/// <para>Thread safety: not thread-safe; only the import worker calls it, as it reads the importer's states and sends forkchoice updates.</para>
/// </remarks>
public sealed class BlockProducer(
    BeaconChainSpec spec,
    EngineDriver engine,
    PubkeyCache pubkeys,
    ProposerPreparations preparations,
    ProposerLookaheadHolder proposerLookaheads,
    BlockAttestationPool attestations,
    SyncCommitteePool syncCommittees,
    ILogManager logManager)
{
    private static readonly UInt256 WeiPerGwei = 1_000_000_000;

    private readonly ILogger _logger = logManager.GetClassLogger<BlockProducer>();
    private PreparedPayload? _prepared;

    /// <summary>Starts building the payload of <paramref name="slot"/> on the head when a prepared validator proposes it.</summary>
    /// <param name="headExecHash">The head of the forkchoice state last sent, which the execution layer answered VALID.</param>
    /// <remarks>A build that cannot start is left to <see cref="ProduceAsync"/>; this never throws for the execution layer.</remarks>
    internal async Task PreparePayloadAsync(BlockImporter importer, Hash256 headRoot, Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash, ulong slot)
    {
        _prepared = null;
        if (SignedBeaconBlockCodec.IsGloasSlot(slot, spec) || proposerLookaheads.Current?.TryGetProposer(slot, out ulong proposer) != true
            || preparations.GetFeeRecipient(proposer) is not { } feeRecipient
            || importer.AdvanceForProposal(headRoot, slot) is not ({ } state, _)
            || state.GetBeaconProposerIndex() != proposer || state.LatestExecutionPayloadHeader!.BlockHash != headExecHash)
            return;

        try
        {
            if (await engine.StartPayloadBuild(headExecHash, safeExecHash, finalizedExecHash, Attributes(state, feeRecipient)) is { } payloadId)
                _prepared = new PreparedPayload(headRoot, slot, feeRecipient, payloadId);
        }
        catch (EngineUnavailableException e)
        {
            if (_logger.IsWarn) _logger.Warn($"The payload of slot {slot} for proposer {proposer} did not start building early; it is built when the block is asked for: {e.Message}");
        }
    }

    /// <summary>Builds the unsigned block of <paramref name="slot"/> on <paramref name="headRoot"/>, with its blobs and their cell proofs.</summary>
    /// <param name="headExecHash">The head of the forkchoice state last sent, which the execution layer answered VALID.</param>
    /// <exception cref="BeaconNodeApiException">
    /// <see cref="BeaconNodeApiError.InvalidRequest"/> for a randao reveal that is not the proposer's or a proposer with no fee recipient prepared;
    /// <see cref="BeaconNodeApiError.NotReady"/> when the head state is not held, the execution layer builds no usable payload, or the block
    /// fails its own state transition.
    /// </exception>
    internal async Task<ProducedBlock> ProduceAsync(BlockImporter importer, Hash256 headRoot, Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash,
        ulong slot, BlsSignature randaoReveal, Hash256 graffiti)
    {
        PreparedPayload? prepared = _prepared;
        _prepared = null;
        if (importer.AdvanceForProposal(headRoot, slot) is not ({ } preState, { } cache))
            throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, $"The post-state of the head {headRoot} is not held.");

        int proposerIndex = (int)preState.GetBeaconProposerIndex();
        ulong epoch = preState.GetCurrentEpoch();
        if (!SignatureSets.VerifyRandaoReveal(preState, proposerIndex, epoch, randaoReveal, pubkeys))
            throw new BeaconNodeApiException(BeaconNodeApiError.InvalidRequest, $"The randao reveal is not the signature of the proposer {proposerIndex} of slot {slot} over epoch {epoch}.");
        Address feeRecipient = preparations.GetFeeRecipient((ulong)proposerIndex)
            ?? throw new BeaconNodeApiException(BeaconNodeApiError.InvalidRequest, $"No fee recipient is prepared for the proposer {proposerIndex} of slot {slot}; prepare_beacon_proposer names it.");
        if (preState.LatestExecutionPayloadHeader!.BlockHash != headExecHash)
            throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, $"The execution layer's head {headExecHash} is not the payload of the head {headRoot}.");

        byte[] payloadId = prepared is not null && prepared.ParentRoot == headRoot && prepared.Slot == slot && prepared.FeeRecipient == feeRecipient
            ? prepared.PayloadId
            : await StartBuild(headExecHash, safeExecHash, finalizedExecHash, Attributes(preState, feeRecipient));
        (ExecutionPayload payload, ExecutionRequests requests, SszKzgCommitment[] commitments, BlobsBundleV2 bundle, UInt256 payloadValue) = await GetPayload(payloadId);

        BeaconStateFulu working = preState.Clone();
        (Attestation[] packed, ulong attestationRewards) = PackAttestations(working, cache, slot, proposerIndex);
        (SyncAggregate syncAggregate, ulong syncRewards) = PackSyncAggregate(working, cache, slot - 1, headRoot, proposerIndex);
        BeaconBlock block = new()
        {
            Slot = slot,
            ProposerIndex = (ulong)proposerIndex,
            ParentRoot = headRoot,
            StateRoot = Hash256.Zero,
            Body = new BeaconBlockBody
            {
                RandaoReveal = randaoReveal,
                // electra/validator.md get_eth1_vote: with every pre-request deposit processed the vote is the state's own eth1_data.
                Eth1Data = new Eth1Data { DepositRoot = preState.Eth1Data!.DepositRoot, DepositCount = preState.Eth1Data.DepositCount, BlockHash = preState.Eth1Data.BlockHash },
                Graffiti = graffiti,
                ProposerSlashings = [],
                AttesterSlashings = [],
                Attestations = packed,
                Deposits = [],
                VoluntaryExits = [],
                SyncAggregate = syncAggregate,
                ExecutionPayload = payload,
                BlsToExecutionChanges = [],
                BlobKzgCommitments = commitments,
                ExecutionRequests = requests,
            },
        };

        ulong consensusValue = attestationRewards + syncRewards;
        Hash256? stateRoot = ComputeStateRoot(preState, block, cache, out string? failure);
        if (stateRoot is null && (packed.Length > 0 || syncAggregate.SyncCommitteeBits!.HasAnySet()))
        {
            if (_logger.IsWarn) _logger.Warn($"The block of slot {slot} with {packed.Length} attestations fails its state transition; building it without attestations and sync aggregate: {failure}");
            block.Body.Attestations = [];
            block.Body.SyncAggregate = EmptySyncAggregate();
            consensusValue = 0;
            stateRoot = ComputeStateRoot(preState, block, cache, out failure);
        }

        block.StateRoot = stateRoot ?? throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, $"The block of slot {slot} fails its own state transition: {failure}");
        if (_logger.IsInfo) _logger.Info($"Produced block of slot {slot} for proposer {proposerIndex}: payload {payload.BlockNumber} ({payload.BlockHash}), {block.Body.Attestations.Length} attestations, {block.Body.BlobKzgCommitments!.Length} blobs");
        return new ProducedBlock(block, bundle.Blobs, bundle.Proofs, payloadValue, (UInt256)consensusValue * WeiPerGwei);
    }

    private async Task<byte[]> StartBuild(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash, PayloadAttributes attributes)
    {
        try
        {
            return await engine.StartPayloadBuild(headExecHash, safeExecHash, finalizedExecHash, attributes)
                ?? throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, "The execution layer did not start building a payload.");
        }
        catch (EngineUnavailableException e)
        {
            throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, e.Message);
        }
    }

    /// <summary>The built payload in its beacon form, with its execution requests, blob commitments, blobs bundle and value.</summary>
    private async Task<(ExecutionPayload Payload, ExecutionRequests Requests, SszKzgCommitment[] Commitments, BlobsBundleV2 Bundle, UInt256 Value)> GetPayload(byte[] payloadId)
    {
        try
        {
            GetPayloadV5Result built = await engine.GetPayload(payloadId);
            ExecutionPayloadV3 payload = built.ExecutionPayload;
            BlobsBundleV2 bundle = built.BlobsBundle;
            if (bundle.Blobs.Length != bundle.Commitments.Length || bundle.Proofs.Length != bundle.Blobs.Length * Ckzg.CellsPerExtBlob)
                throw new InvalidDataException($"The blobs bundle has {bundle.Blobs.Length} blobs, {bundle.Commitments.Length} commitments and {bundle.Proofs.Length} cell proofs.");
            foreach (byte[] blob in bundle.Blobs)
            {
                if (blob.Length != Ckzg.BytesPerBlob) throw new InvalidDataException($"A blob is {blob.Length} bytes long.");
            }

            foreach (byte[] proof in bundle.Proofs)
            {
                if (proof.Length != Ckzg.BytesPerProof) throw new InvalidDataException($"A cell proof is {proof.Length} bytes long.");
            }

            return (PayloadConverter.ToExecutionPayload(payload), PayloadConverter.ToExecutionRequests(built.ExecutionRequests ?? []), ToCommitments(bundle.Commitments), bundle, built.BlockValue);
        }
        catch (Exception e) when (e is EngineUnavailableException or InvalidDataException)
        {
            throw new BeaconNodeApiException(BeaconNodeApiError.NotReady, $"The execution layer built no usable payload: {e.Message}");
        }
    }

    /// <summary>The attestations the block includes, run in order on <paramref name="working"/>, and the proposer's reward for them in Gwei.</summary>
    private (Attestation[] Packed, ulong Rewards) PackAttestations(BeaconStateFulu working, EpochCache cache, ulong slot, int proposerIndex)
    {
        List<(Attestation Attestation, BlsSignature[] Signatures)> candidates = attestations.GetOnChainCandidates(slot);
        candidates.Sort(static (first, second) => BlockAttestationPool.Count(second.Attestation.AggregationBits!).CompareTo(BlockAttestationPool.Count(first.Attestation.AggregationBits!)));
        List<Attestation> packed = [];
        ulong rewards = 0;
        foreach ((Attestation attestation, BlsSignature[] signatures) in candidates)
        {
            if (packed.Count == Presets.MaxAttestationsElectra) break;
            ulong balanceBefore = working.Balances![proposerIndex];
            try
            {
                // Its checks all come before its writes, so a refused attestation leaves the state as it was.
                BlockProcessing.ProcessAttestation(working, attestation, cache, pubkeys, verifySignature: false);
            }
            catch (BeaconStateException)
            {
                continue;
            }

            // The proposer is rewarded for each participation flag the attestation sets, so no reward means it adds nothing.
            ulong reward = working.Balances[proposerIndex] - balanceBefore;
            if (reward == 0) continue;
            attestation.Signature = AttestationPool.AggregateSignatures(signatures);
            packed.Add(attestation);
            rewards += reward;
        }

        return ([.. packed], rewards);
    }

    /// <summary>The sync aggregate the block includes, and the proposer's balance gain from it in Gwei, which counts its own participation when it is a member.</summary>
    private (SyncAggregate Aggregate, ulong Rewards) PackSyncAggregate(BeaconStateFulu working, EpochCache cache, ulong previousSlot, Hash256 parentRoot, int proposerIndex)
    {
        BitArray bits = new(Presets.SyncCommitteeSize);
        List<BlsSignature> signatures = [];
        for (ulong subcommitteeIndex = 0; subcommitteeIndex < SyncCommitteeContribution.SubnetCount; subcommitteeIndex++)
        {
            if (syncCommittees.GetBestContribution(previousSlot, parentRoot, subcommitteeIndex) is not { AggregationBits: { } contributed } contribution) continue;
            int firstPosition = (int)subcommitteeIndex * SyncCommitteeContribution.SubcommitteeSize;
            for (int position = 0; position < contributed.Length; position++)
            {
                if (contributed[position]) bits[firstPosition + position] = true;
            }

            signatures.Add(contribution.Signature);
        }

        if (signatures.Count == 0) return (EmptySyncAggregate(), 0);
        SyncAggregate aggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = AttestationPool.AggregateSignatures([.. signatures]) };
        ulong balanceBefore = working.Balances![proposerIndex];
        try
        {
            BlockProcessing.ProcessSyncAggregate(working, aggregate, cache, pubkeys, verifySignature: true);
        }
        catch (BeaconStateException e)
        {
            if (_logger.IsWarn) _logger.Warn($"The sync aggregate of slot {previousSlot + 1} is refused; the block carries an empty one: {e.Message}");
            return (EmptySyncAggregate(), 0);
        }

        ulong balanceAfter = working.Balances[proposerIndex];
        return (aggregate, balanceAfter > balanceBefore ? balanceAfter - balanceBefore : 0);
    }

    /// <returns>The block's post-state root, or <c>null</c> with the reason when the block fails its state transition.</returns>
    private Hash256? ComputeStateRoot(BeaconStateFulu preState, BeaconBlock block, EpochCache cache, out string? failure)
    {
        BeaconStateFulu postState = preState.Clone();
        try
        {
            StateTransition.StateTransition.ApplyPrepared(postState, new SignedBeaconBlock { Message = block }, cache, pubkeys, BuiltPayload.Instance, spec, validateResult: false, verifySignatures: false);
            failure = null;
            return cache.Hasher.HashTreeRoot(postState);
        }
        catch (BeaconStateException e)
        {
            failure = e.Message;
            return null;
        }
    }

    /// <summary>bellatrix/validator.md <c>prepare_execution_payload</c>: the payload attributes of a block on <paramref name="state"/>, processed to the block's slot.</summary>
    private static PayloadAttributes Attributes(BeaconStateFulu state, Address feeRecipient) => new()
    {
        Timestamp = state.GenesisTime + state.Slot * Presets.SecondsPerSlot,
        PrevRandao = state.GetRandaoMix(state.GetCurrentEpoch()),
        SuggestedFeeRecipient = feeRecipient,
        Withdrawals = PayloadConverter.ToCoreWithdrawals(BlockProcessing.GetExpectedWithdrawals(state)),
        ParentBeaconBlockRoot = SszRoots.HashTreeRoot(state.LatestBlockHeader!),
    };

    private static SszKzgCommitment[] ToCommitments(byte[][] commitments)
    {
        SszKzgCommitment[] converted = new SszKzgCommitment[commitments.Length];
        for (int i = 0; i < converted.Length; i++) converted[i] = SszKzgCommitment.FromSpan(commitments[i]);
        return converted;
    }

    private static SyncAggregate EmptySyncAggregate() =>
        new() { SyncCommitteeBits = new BitArray(Presets.SyncCommitteeSize), SyncCommitteeSignature = new BlsSignature(SignatureSets.G2PointAtInfinity) };

    private sealed record PreparedPayload(Hash256 ParentRoot, ulong Slot, Address FeeRecipient, byte[] PayloadId);

    /// <summary>The execution verdict on a payload the execution layer built itself: valid, without asking it again.</summary>
    private sealed class BuiltPayload : INewPayloadNotifier
    {
        public static readonly BuiltPayload Instance = new();

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
