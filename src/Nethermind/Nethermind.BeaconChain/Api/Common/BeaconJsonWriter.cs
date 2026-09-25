// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>
/// A <see cref="Utf8JsonWriter"/> over a response <see cref="PipeWriter"/> with explicit flush
/// checkpoints, so a multi-hundred-MB state body streams to the client instead of being buffered
/// whole. Kestrel forbids synchronous body writes, so the writer targets the pipe, not the stream.
/// </summary>
internal sealed class BeaconJsonStream(PipeWriter output, CancellationToken token) : IAsyncDisposable
{
    private const int FlushThresholdBytes = 64 * 1024;

    private long _flushedBytes;

    public Utf8JsonWriter Writer { get; } = new(output);

    /// <summary>Pushes buffered bytes to the client once enough have accumulated since the last flush; call between elements of long arrays.</summary>
    /// <remarks>Counts committed bytes too: <see cref="Utf8JsonWriter.BytesPending"/> alone resets every time the writer
    /// grows into a new pipe segment (about 4 KB), so it can never reach the threshold.</remarks>
    public ValueTask CheckpointAsync() => Writer.BytesCommitted + Writer.BytesPending - _flushedBytes < FlushThresholdBytes ? default : FlushAsync();

    public async ValueTask FlushAsync()
    {
        Writer.Flush();
        _flushedBytes = Writer.BytesCommitted;
        FlushResult result = await output.FlushAsync(token);
        if (result.IsCanceled || result.IsCompleted)
        {
            throw new OperationCanceledException("The client stopped reading the response body.");
        }
    }

    public ValueTask DisposeAsync() => Writer.DisposeAsync();
}

/// <summary>
/// Writes beacon containers in the beacon-api JSON representation: every uint as a decimal string,
/// every byte vector/list as 0x-prefixed lowercase hex, bitlists with their SSZ length sentinel,
/// participation flags as an array of uint8 strings, containers with snake_case field names in spec
/// order. Field names are spelled out here rather than derived from the C# properties so a rename
/// on the type side can never silently change the wire format.
/// </summary>
internal static class BeaconJsonWriter
{
    public static void WriteSignedBeaconBlock(Utf8JsonWriter w, SignedBeaconBlock block)
    {
        w.WriteStartObject();
        w.WritePropertyName("message");
        WriteBeaconBlock(w, block.Message!);
        WriteHex(w, "signature", block.Signature.Bytes);
        w.WriteEndObject();
    }

    private static void WriteBeaconBlock(Utf8JsonWriter w, BeaconBlock block)
    {
        w.WriteStartObject();
        WriteUInt(w, "slot", block.Slot);
        WriteUInt(w, "proposer_index", block.ProposerIndex);
        WriteHex(w, "parent_root", block.ParentRoot!.Bytes);
        WriteHex(w, "state_root", block.StateRoot!.Bytes);
        w.WritePropertyName("body");
        WriteBeaconBlockBody(w, block.Body!);
        w.WriteEndObject();
    }

    private static void WriteBeaconBlockBody(Utf8JsonWriter w, BeaconBlockBody body)
    {
        w.WriteStartObject();
        WriteHex(w, "randao_reveal", body.RandaoReveal.Bytes);
        w.WritePropertyName("eth1_data");
        WriteEth1Data(w, body.Eth1Data!);
        WriteHex(w, "graffiti", body.Graffiti!.Bytes);

        WriteProposerSlashings(w, body.ProposerSlashings!);

        w.WriteStartArray("attester_slashings");
        foreach (AttesterSlashing slashing in body.AttesterSlashings!)
        {
            w.WriteStartObject();
            w.WritePropertyName("attestation_1");
            WriteIndexedAttestation(w, slashing.Attestation1!);
            w.WritePropertyName("attestation_2");
            WriteIndexedAttestation(w, slashing.Attestation2!);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("attestations");
        foreach (Attestation attestation in body.Attestations!)
        {
            WriteAttestation(w, attestation);
        }
        w.WriteEndArray();

        WriteDeposits(w, body.Deposits!);
        WriteVoluntaryExits(w, body.VoluntaryExits!);
        WriteSyncAggregate(w, body.SyncAggregate!);

        w.WritePropertyName("execution_payload");
        WriteExecutionPayload(w, body.ExecutionPayload!);

        WriteBlsToExecutionChanges(w, body.BlsToExecutionChanges!);
        WriteKzgCommitments(w, body.BlobKzgCommitments!);

        w.WritePropertyName("execution_requests");
        WriteExecutionRequests(w, body.ExecutionRequests!);
        w.WriteEndObject();
    }

    /// <summary>Writes a Gloas signed block: the bid replaces the payload, and the parent's execution requests ride in the body (specs/gloas/beacon-chain.md <c>BeaconBlockBody</c>).</summary>
    public static void WriteSignedBeaconBlock(Utf8JsonWriter w, SignedBeaconBlockGloas block)
    {
        w.WriteStartObject();
        w.WritePropertyName("message");
        BeaconBlockGloas message = block.Message!;
        w.WriteStartObject();
        WriteUInt(w, "slot", message.Slot);
        WriteUInt(w, "proposer_index", message.ProposerIndex);
        WriteHex(w, "parent_root", message.ParentRoot!.Bytes);
        WriteHex(w, "state_root", message.StateRoot!.Bytes);
        w.WritePropertyName("body");
        WriteBeaconBlockBody(w, message.Body!);
        w.WriteEndObject();
        WriteHex(w, "signature", block.Signature.Bytes);
        w.WriteEndObject();
    }

    private static void WriteBeaconBlockBody(Utf8JsonWriter w, BeaconBlockBodyGloas body)
    {
        w.WriteStartObject();
        WriteHex(w, "randao_reveal", body.RandaoReveal.Bytes);
        w.WritePropertyName("eth1_data");
        WriteEth1Data(w, body.Eth1Data!);
        WriteHex(w, "graffiti", body.Graffiti!.Bytes);
        WriteProposerSlashings(w, body.ProposerSlashings!);

        w.WriteStartArray("attester_slashings");
        foreach (AttesterSlashingGloas slashing in body.AttesterSlashings!)
        {
            w.WriteStartObject();
            w.WritePropertyName("attestation_1");
            WriteIndexedAttestation(w, slashing.Attestation1!.AttestingIndices!, slashing.Attestation1.Data!, slashing.Attestation1.Signature);
            w.WritePropertyName("attestation_2");
            WriteIndexedAttestation(w, slashing.Attestation2!.AttestingIndices!, slashing.Attestation2.Data!, slashing.Attestation2.Signature);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("attestations");
        foreach (AttestationGloas attestation in body.Attestations!)
        {
            WriteAttestation(w, attestation.AggregationBits!, attestation.Data!, attestation.Signature, attestation.CommitteeBits!);
        }
        w.WriteEndArray();

        WriteDeposits(w, body.Deposits!);
        WriteVoluntaryExits(w, body.VoluntaryExits!);
        WriteSyncAggregate(w, body.SyncAggregate!);
        WriteBlsToExecutionChanges(w, body.BlsToExecutionChanges!);

        w.WritePropertyName("signed_execution_payload_bid");
        WriteSignedExecutionPayloadBid(w, body.SignedExecutionPayloadBid!);

        w.WriteStartArray("payload_attestations");
        foreach (PayloadAttestation attestation in body.PayloadAttestations!)
        {
            w.WriteStartObject();
            WriteBitVector(w, "aggregation_bits", attestation.AggregationBits!);
            w.WritePropertyName("data");
            WritePayloadAttestationData(w, attestation.Data!);
            WriteHex(w, "signature", attestation.Signature.Bytes);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WritePropertyName("parent_execution_requests");
        WriteExecutionRequests(w, body.ParentExecutionRequests!);
        w.WriteEndObject();
    }

    private static void WriteSignedExecutionPayloadBid(Utf8JsonWriter w, SignedExecutionPayloadBid signed)
    {
        ExecutionPayloadBid bid = signed.Message!;
        w.WriteStartObject();
        w.WritePropertyName("message");
        w.WriteStartObject();
        WriteHex(w, "parent_block_hash", bid.ParentBlockHash!.Bytes);
        WriteHex(w, "parent_block_root", bid.ParentBlockRoot!.Bytes);
        WriteHex(w, "block_hash", bid.BlockHash!.Bytes);
        WriteHex(w, "prev_randao", bid.PrevRandao!.Bytes);
        WriteHex(w, "fee_recipient", bid.FeeRecipient!.Bytes);
        WriteUInt(w, "gas_limit", bid.GasLimit);
        WriteUInt(w, "builder_index", bid.BuilderIndex);
        WriteUInt(w, "slot", bid.Slot);
        WriteUInt(w, "value", bid.Value);
        WriteUInt(w, "execution_payment", bid.ExecutionPayment);
        WriteKzgCommitments(w, bid.BlobKzgCommitments!);
        WriteHex(w, "execution_requests_root", bid.ExecutionRequestsRoot!.Bytes);
        w.WriteEndObject();
        WriteHex(w, "signature", signed.Signature.Bytes);
        w.WriteEndObject();
    }

    private static void WritePayloadAttestationData(Utf8JsonWriter w, PayloadAttestationData data)
    {
        w.WriteStartObject();
        WriteHex(w, "beacon_block_root", data.BeaconBlockRoot!.Bytes);
        WriteUInt(w, "slot", data.Slot);
        w.WriteBoolean("payload_present", data.PayloadPresent);
        w.WriteBoolean("blob_data_available", data.BlobDataAvailable);
        w.WriteEndObject();
    }

    private static void WriteExecutionRequests(Utf8JsonWriter w, ExecutionRequestsGloas requests)
    {
        w.WriteStartObject();
        WriteExecutionRequestLists(w, requests.Deposits!, requests.Withdrawals!, requests.Consolidations!);

        w.WriteStartArray("builder_deposits");
        foreach (BuilderDepositRequest deposit in requests.BuilderDeposits!)
        {
            w.WriteStartObject();
            WriteHex(w, "pubkey", deposit.Pubkey.Bytes);
            WriteHex(w, "withdrawal_credentials", deposit.WithdrawalCredentials!.Bytes);
            WriteUInt(w, "amount", deposit.Amount);
            WriteHex(w, "signature", deposit.Signature.Bytes);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("builder_exits");
        foreach (BuilderExitRequest exit in requests.BuilderExits!)
        {
            w.WriteStartObject();
            WriteHex(w, "source_address", exit.SourceAddress!.Bytes);
            WriteHex(w, "pubkey", exit.Pubkey.Bytes);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteProposerSlashings(Utf8JsonWriter w, ProposerSlashing[] slashings)
    {
        w.WriteStartArray("proposer_slashings");
        foreach (ProposerSlashing slashing in slashings)
        {
            w.WriteStartObject();
            w.WritePropertyName("signed_header_1");
            WriteSignedBeaconBlockHeader(w, slashing.SignedHeader1!);
            w.WritePropertyName("signed_header_2");
            WriteSignedBeaconBlockHeader(w, slashing.SignedHeader2!);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteDeposits(Utf8JsonWriter w, Deposit[] deposits)
    {
        w.WriteStartArray("deposits");
        foreach (Deposit deposit in deposits)
        {
            w.WriteStartObject();
            w.WriteStartArray("proof");
            foreach (Hash256 node in deposit.Proof!) WriteHexValue(w, node.Bytes);
            w.WriteEndArray();
            w.WritePropertyName("data");
            w.WriteStartObject();
            WriteHex(w, "pubkey", deposit.Data!.Pubkey.Bytes);
            WriteHex(w, "withdrawal_credentials", deposit.Data.WithdrawalCredentials!.Bytes);
            WriteUInt(w, "amount", deposit.Data.Amount);
            WriteHex(w, "signature", deposit.Data.Signature.Bytes);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteVoluntaryExits(Utf8JsonWriter w, SignedVoluntaryExit[] exits)
    {
        w.WriteStartArray("voluntary_exits");
        foreach (SignedVoluntaryExit exit in exits)
        {
            w.WriteStartObject();
            w.WritePropertyName("message");
            w.WriteStartObject();
            WriteUInt(w, "epoch", exit.Message!.Epoch);
            WriteUInt(w, "validator_index", exit.Message.ValidatorIndex);
            w.WriteEndObject();
            WriteHex(w, "signature", exit.Signature.Bytes);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteSyncAggregate(Utf8JsonWriter w, SyncAggregate aggregate)
    {
        w.WritePropertyName("sync_aggregate");
        w.WriteStartObject();
        WriteBitVector(w, "sync_committee_bits", aggregate.SyncCommitteeBits!);
        WriteHex(w, "sync_committee_signature", aggregate.SyncCommitteeSignature.Bytes);
        w.WriteEndObject();
    }

    private static void WriteBlsToExecutionChanges(Utf8JsonWriter w, SignedBlsToExecutionChange[] changes)
    {
        w.WriteStartArray("bls_to_execution_changes");
        foreach (SignedBlsToExecutionChange change in changes)
        {
            w.WriteStartObject();
            w.WritePropertyName("message");
            w.WriteStartObject();
            WriteUInt(w, "validator_index", change.Message!.ValidatorIndex);
            WriteHex(w, "from_bls_pubkey", change.Message.FromBlsPubkey.Bytes);
            WriteHex(w, "to_execution_address", change.Message.ToExecutionAddress!.Bytes);
            w.WriteEndObject();
            WriteHex(w, "signature", change.Signature.Bytes);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteKzgCommitments(Utf8JsonWriter w, SszKzgCommitment[] commitments)
    {
        w.WriteStartArray("blob_kzg_commitments");
        foreach (SszKzgCommitment commitment in commitments) WriteHexValue(w, commitment.AsSpan());
        w.WriteEndArray();
    }

    private static void WriteExecutionRequests(Utf8JsonWriter w, ExecutionRequests requests)
    {
        w.WriteStartObject();
        WriteExecutionRequestLists(w, requests.Deposits!, requests.Withdrawals!, requests.Consolidations!);
        w.WriteEndObject();
    }

    private static void WriteExecutionRequestLists(Utf8JsonWriter w, DepositRequest[] deposits, WithdrawalRequest[] withdrawals, ConsolidationRequest[] consolidations)
    {
        w.WriteStartArray("deposits");
        foreach (DepositRequest deposit in deposits)
        {
            w.WriteStartObject();
            WriteHex(w, "pubkey", deposit.Pubkey.Bytes);
            WriteHex(w, "withdrawal_credentials", deposit.WithdrawalCredentials!.Bytes);
            WriteUInt(w, "amount", deposit.Amount);
            WriteHex(w, "signature", deposit.Signature.Bytes);
            WriteUInt(w, "index", deposit.Index);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("withdrawals");
        foreach (WithdrawalRequest withdrawal in withdrawals)
        {
            w.WriteStartObject();
            WriteHex(w, "source_address", withdrawal.SourceAddress!.Bytes);
            WriteHex(w, "validator_pubkey", withdrawal.ValidatorPubkey.Bytes);
            WriteUInt(w, "amount", withdrawal.Amount);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("consolidations");
        foreach (ConsolidationRequest consolidation in consolidations)
        {
            w.WriteStartObject();
            WriteHex(w, "source_address", consolidation.SourceAddress!.Bytes);
            WriteHex(w, "source_pubkey", consolidation.SourcePubkey.Bytes);
            WriteHex(w, "target_pubkey", consolidation.TargetPubkey.Bytes);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteExecutionPayload(Utf8JsonWriter w, ExecutionPayload payload)
    {
        w.WriteStartObject();
        WriteHex(w, "parent_hash", payload.ParentHash!.Bytes);
        WriteHex(w, "fee_recipient", payload.FeeRecipient!.Bytes);
        WriteHex(w, "state_root", payload.StateRoot!.Bytes);
        WriteHex(w, "receipts_root", payload.ReceiptsRoot!.Bytes);
        WriteHex(w, "logs_bloom", payload.LogsBloom!.Bytes);
        WriteHex(w, "prev_randao", payload.PrevRandao!.Bytes);
        WriteUInt(w, "block_number", payload.BlockNumber);
        WriteUInt(w, "gas_limit", payload.GasLimit);
        WriteUInt(w, "gas_used", payload.GasUsed);
        WriteUInt(w, "timestamp", payload.Timestamp);
        WriteHex(w, "extra_data", payload.ExtraData!);
        w.WriteString("base_fee_per_gas", payload.BaseFeePerGas.ToString());
        WriteHex(w, "block_hash", payload.BlockHash!.Bytes);

        w.WriteStartArray("transactions");
        foreach (Types.Transaction transaction in payload.Transactions!) WriteHexValue(w, transaction.Bytes!);
        w.WriteEndArray();

        w.WriteStartArray("withdrawals");
        foreach (Types.Withdrawal withdrawal in payload.Withdrawals!)
        {
            w.WriteStartObject();
            WriteUInt(w, "index", withdrawal.Index);
            WriteUInt(w, "validator_index", withdrawal.ValidatorIndex);
            WriteHex(w, "address", withdrawal.Address!.Bytes);
            WriteUInt(w, "amount", withdrawal.Amount);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        WriteUInt(w, "blob_gas_used", payload.BlobGasUsed);
        WriteUInt(w, "excess_blob_gas", payload.ExcessBlobGas);
        w.WriteEndObject();
    }

    private static void WriteExecutionPayloadHeader(Utf8JsonWriter w, ExecutionPayloadHeader header)
    {
        w.WriteStartObject();
        WriteHex(w, "parent_hash", header.ParentHash!.Bytes);
        WriteHex(w, "fee_recipient", header.FeeRecipient!.Bytes);
        WriteHex(w, "state_root", header.StateRoot!.Bytes);
        WriteHex(w, "receipts_root", header.ReceiptsRoot!.Bytes);
        WriteHex(w, "logs_bloom", header.LogsBloom!.Bytes);
        WriteHex(w, "prev_randao", header.PrevRandao!.Bytes);
        WriteUInt(w, "block_number", header.BlockNumber);
        WriteUInt(w, "gas_limit", header.GasLimit);
        WriteUInt(w, "gas_used", header.GasUsed);
        WriteUInt(w, "timestamp", header.Timestamp);
        WriteHex(w, "extra_data", header.ExtraData!);
        w.WriteString("base_fee_per_gas", header.BaseFeePerGas.ToString());
        WriteHex(w, "block_hash", header.BlockHash!.Bytes);
        WriteHex(w, "transactions_root", header.TransactionsRoot!.Bytes);
        WriteHex(w, "withdrawals_root", header.WithdrawalsRoot!.Bytes);
        WriteUInt(w, "blob_gas_used", header.BlobGasUsed);
        WriteUInt(w, "excess_blob_gas", header.ExcessBlobGas);
        w.WriteEndObject();
    }

    private static void WriteAttestation(Utf8JsonWriter w, Attestation attestation) =>
        WriteAttestation(w, attestation.AggregationBits!, attestation.Data!, attestation.Signature, attestation.CommitteeBits!);

    /// <remarks>A Gloas <c>ProgressiveBitList</c> serializes like a bitlist, with the same length sentinel.</remarks>
    private static void WriteAttestation(Utf8JsonWriter w, BitArray aggregationBits, AttestationData data, BlsSignature signature, BitArray committeeBits)
    {
        w.WriteStartObject();
        WriteBitList(w, "aggregation_bits", aggregationBits);
        w.WritePropertyName("data");
        WriteAttestationData(w, data);
        WriteHex(w, "signature", signature.Bytes);
        WriteBitVector(w, "committee_bits", committeeBits);
        w.WriteEndObject();
    }

    private static void WriteIndexedAttestation(Utf8JsonWriter w, IndexedAttestation attestation) =>
        WriteIndexedAttestation(w, attestation.AttestingIndices!, attestation.Data!, attestation.Signature);

    private static void WriteIndexedAttestation(Utf8JsonWriter w, ulong[] attestingIndices, AttestationData data, BlsSignature signature)
    {
        w.WriteStartObject();
        w.WriteStartArray("attesting_indices");
        foreach (ulong index in attestingIndices) WriteUIntValue(w, index);
        w.WriteEndArray();
        w.WritePropertyName("data");
        WriteAttestationData(w, data);
        WriteHex(w, "signature", signature.Bytes);
        w.WriteEndObject();
    }

    private static void WriteAttestationData(Utf8JsonWriter w, AttestationData data)
    {
        w.WriteStartObject();
        WriteUInt(w, "slot", data.Slot);
        WriteUInt(w, "index", data.Index);
        WriteHex(w, "beacon_block_root", data.BeaconBlockRoot!.Bytes);
        w.WritePropertyName("source");
        WriteCheckpoint(w, data.Source!);
        w.WritePropertyName("target");
        WriteCheckpoint(w, data.Target!);
        w.WriteEndObject();
    }

    private static void WriteSignedBeaconBlockHeader(Utf8JsonWriter w, SignedBeaconBlockHeader header)
    {
        w.WriteStartObject();
        w.WritePropertyName("message");
        WriteBeaconBlockHeader(w, header.Message!);
        WriteHex(w, "signature", header.Signature.Bytes);
        w.WriteEndObject();
    }

    private static void WriteBeaconBlockHeader(Utf8JsonWriter w, BeaconBlockHeader header)
    {
        w.WriteStartObject();
        WriteUInt(w, "slot", header.Slot);
        WriteUInt(w, "proposer_index", header.ProposerIndex);
        WriteHex(w, "parent_root", header.ParentRoot!.Bytes);
        WriteHex(w, "state_root", header.StateRoot!.Bytes);
        WriteHex(w, "body_root", header.BodyRoot!.Bytes);
        w.WriteEndObject();
    }

    private static void WriteCheckpoint(Utf8JsonWriter w, Checkpoint checkpoint)
    {
        w.WriteStartObject();
        WriteUInt(w, "epoch", checkpoint.Epoch);
        WriteHex(w, "root", checkpoint.Root!.Bytes);
        w.WriteEndObject();
    }

    private static void WriteEth1Data(Utf8JsonWriter w, Eth1Data data)
    {
        w.WriteStartObject();
        WriteHex(w, "deposit_root", data.DepositRoot!.Bytes);
        WriteUInt(w, "deposit_count", data.DepositCount);
        WriteHex(w, "block_hash", data.BlockHash!.Bytes);
        w.WriteEndObject();
    }

    private static void WriteValidator(Utf8JsonWriter w, Validator validator)
    {
        w.WriteStartObject();
        WriteHex(w, "pubkey", validator.Pubkey.Bytes);
        WriteHex(w, "withdrawal_credentials", validator.WithdrawalCredentials!.Bytes);
        WriteUInt(w, "effective_balance", validator.EffectiveBalance);
        w.WriteBoolean("slashed", validator.Slashed);
        WriteUInt(w, "activation_eligibility_epoch", validator.ActivationEligibilityEpoch);
        WriteUInt(w, "activation_epoch", validator.ActivationEpoch);
        WriteUInt(w, "exit_epoch", validator.ExitEpoch);
        WriteUInt(w, "withdrawable_epoch", validator.WithdrawableEpoch);
        w.WriteEndObject();
    }

    /// <summary>Streams a Fulu <c>BeaconState</c>, flushing between elements of its large lists.</summary>
    public static async Task WriteBeaconStateAsync(BeaconJsonStream s, BeaconStateFulu state)
    {
        Utf8JsonWriter w = s.Writer;
        w.WriteStartObject();
        WriteUInt(w, "genesis_time", state.GenesisTime);
        WriteHex(w, "genesis_validators_root", state.GenesisValidatorsRoot!.Bytes);
        WriteUInt(w, "slot", state.Slot);

        w.WritePropertyName("fork");
        w.WriteStartObject();
        WriteHex(w, "previous_version", state.Fork!.PreviousVersion!);
        WriteHex(w, "current_version", state.Fork.CurrentVersion!);
        WriteUInt(w, "epoch", state.Fork.Epoch);
        w.WriteEndObject();

        w.WritePropertyName("latest_block_header");
        WriteBeaconBlockHeader(w, state.LatestBlockHeader!);

        await WriteHashArrayAsync(s, "block_roots", state.BlockRoots!);
        await WriteHashArrayAsync(s, "state_roots", state.StateRoots!);
        await WriteHashArrayAsync(s, "historical_roots", state.HistoricalRoots!);

        w.WritePropertyName("eth1_data");
        WriteEth1Data(w, state.Eth1Data!);
        await WriteArrayAsync(s, "eth1_data_votes", state.Eth1DataVotes!, WriteEth1Data);
        WriteUInt(w, "eth1_deposit_index", state.Eth1DepositIndex);

        await WriteArrayAsync(s, "validators", state.Validators!, WriteValidator);
        await WriteUIntArrayAsync(s, "balances", state.Balances!);
        await WriteHashArrayAsync(s, "randao_mixes", state.RandaoMixes!);
        await WriteUIntArrayAsync(s, "slashings", state.Slashings!);
        await WriteParticipationAsync(s, "previous_epoch_participation", state.PreviousEpochParticipation!);
        await WriteParticipationAsync(s, "current_epoch_participation", state.CurrentEpochParticipation!);

        WriteBitVector(w, "justification_bits", state.JustificationBits!);
        w.WritePropertyName("previous_justified_checkpoint");
        WriteCheckpoint(w, state.PreviousJustifiedCheckpoint!);
        w.WritePropertyName("current_justified_checkpoint");
        WriteCheckpoint(w, state.CurrentJustifiedCheckpoint!);
        w.WritePropertyName("finalized_checkpoint");
        WriteCheckpoint(w, state.FinalizedCheckpoint!);

        await WriteUIntArrayAsync(s, "inactivity_scores", state.InactivityScores!);
        await WriteSyncCommitteeAsync(s, "current_sync_committee", state.CurrentSyncCommittee!);
        await WriteSyncCommitteeAsync(s, "next_sync_committee", state.NextSyncCommittee!);

        w.WritePropertyName("latest_execution_payload_header");
        WriteExecutionPayloadHeader(w, state.LatestExecutionPayloadHeader!);
        WriteUInt(w, "next_withdrawal_index", state.NextWithdrawalIndex);
        WriteUInt(w, "next_withdrawal_validator_index", state.NextWithdrawalValidatorIndex);

        await WriteArrayAsync(s, "historical_summaries", state.HistoricalSummaries!, static (w, summary) =>
        {
            w.WriteStartObject();
            WriteHex(w, "block_summary_root", summary.BlockSummaryRoot!.Bytes);
            WriteHex(w, "state_summary_root", summary.StateSummaryRoot!.Bytes);
            w.WriteEndObject();
        });

        WriteUInt(w, "deposit_requests_start_index", state.DepositRequestsStartIndex);
        WriteUInt(w, "deposit_balance_to_consume", state.DepositBalanceToConsume);
        WriteUInt(w, "exit_balance_to_consume", state.ExitBalanceToConsume);
        WriteUInt(w, "earliest_exit_epoch", state.EarliestExitEpoch);
        WriteUInt(w, "consolidation_balance_to_consume", state.ConsolidationBalanceToConsume);
        WriteUInt(w, "earliest_consolidation_epoch", state.EarliestConsolidationEpoch);

        await WriteArrayAsync(s, "pending_deposits", state.PendingDeposits!, static (w, deposit) =>
        {
            w.WriteStartObject();
            WriteHex(w, "pubkey", deposit.Pubkey.Bytes);
            WriteHex(w, "withdrawal_credentials", deposit.WithdrawalCredentials!.Bytes);
            WriteUInt(w, "amount", deposit.Amount);
            WriteHex(w, "signature", deposit.Signature.Bytes);
            WriteUInt(w, "slot", deposit.Slot);
            w.WriteEndObject();
        });

        await WriteArrayAsync(s, "pending_partial_withdrawals", state.PendingPartialWithdrawals!, static (w, withdrawal) =>
        {
            w.WriteStartObject();
            WriteUInt(w, "validator_index", withdrawal.ValidatorIndex);
            WriteUInt(w, "amount", withdrawal.Amount);
            WriteUInt(w, "withdrawable_epoch", withdrawal.WithdrawableEpoch);
            w.WriteEndObject();
        });

        await WriteArrayAsync(s, "pending_consolidations", state.PendingConsolidations!, static (w, consolidation) =>
        {
            w.WriteStartObject();
            WriteUInt(w, "source_index", consolidation.SourceIndex);
            WriteUInt(w, "target_index", consolidation.TargetIndex);
            w.WriteEndObject();
        });

        await WriteUIntArrayAsync(s, "proposer_lookahead", state.ProposerLookahead!);
        w.WriteEndObject();
    }

    private static async Task WriteSyncCommitteeAsync(BeaconJsonStream s, string name, SyncCommittee committee)
    {
        Utf8JsonWriter w = s.Writer;
        w.WritePropertyName(name);
        w.WriteStartObject();
        w.WriteStartArray("pubkeys");
        foreach (BlsPublicKey pubkey in committee.Pubkeys!)
        {
            WriteHexValue(w, pubkey.Bytes);
            await s.CheckpointAsync();
        }
        w.WriteEndArray();
        WriteHex(w, "aggregate_pubkey", committee.AggregatePubkey.Bytes);
        w.WriteEndObject();
    }

    private static async Task WriteArrayAsync<T>(BeaconJsonStream s, string name, T[] items, Action<Utf8JsonWriter, T> writeItem)
    {
        s.Writer.WriteStartArray(name);
        foreach (T item in items)
        {
            writeItem(s.Writer, item);
            await s.CheckpointAsync();
        }
        s.Writer.WriteEndArray();
    }

    private static async Task WriteHashArrayAsync(BeaconJsonStream s, string name, Hash256[] items)
    {
        s.Writer.WriteStartArray(name);
        foreach (Hash256 item in items)
        {
            WriteHexValue(s.Writer, item.Bytes);
            await s.CheckpointAsync();
        }
        s.Writer.WriteEndArray();
    }

    private static async Task WriteUIntArrayAsync(BeaconJsonStream s, string name, ulong[] items)
    {
        s.Writer.WriteStartArray(name);
        foreach (ulong item in items)
        {
            WriteUIntValue(s.Writer, item);
            await s.CheckpointAsync();
        }
        s.Writer.WriteEndArray();
    }

    /// <summary>Participation flags are an SSZ <c>List[uint8]</c>: one decimal string per validator, not a hex blob.</summary>
    private static async Task WriteParticipationAsync(BeaconJsonStream s, string name, byte[] flags)
    {
        s.Writer.WriteStartArray(name);
        foreach (byte flag in flags)
        {
            WriteUIntValue(s.Writer, flag);
            await s.CheckpointAsync();
        }
        s.Writer.WriteEndArray();
    }

    private static void WriteUInt(Utf8JsonWriter w, string name, ulong value)
    {
        w.WritePropertyName(name);
        WriteUIntValue(w, value);
    }

    private static void WriteUIntValue(Utf8JsonWriter w, ulong value)
    {
        Span<byte> digits = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, digits, out int written);
        w.WriteStringValue(digits[..written]);
    }

    private static void WriteHex(Utf8JsonWriter w, string name, ReadOnlySpan<byte> bytes)
    {
        w.WritePropertyName(name);
        WriteHexValue(w, bytes);
    }

    private static void WriteHexValue(Utf8JsonWriter w, ReadOnlySpan<byte> bytes)
    {
        int length = 2 + bytes.Length * 2;
        byte[]? rented = length > 512 ? ArrayPool<byte>.Shared.Rent(length) : null;
        Span<byte> hex = rented is null ? stackalloc byte[512] : rented;
        hex = hex[..length];
        hex[0] = (byte)'0';
        hex[1] = (byte)'x';
        bytes.OutputBytesToByteHex(hex[2..], extraNibble: false);
        w.WriteStringValue(hex);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }

    /// <summary>SSZ bitvector: LSB-first bits packed into <c>ceil(n / 8)</c> bytes, no length sentinel.</summary>
    private static void WriteBitVector(Utf8JsonWriter w, string name, BitArray bits)
    {
        byte[] bytes = new byte[(bits.Length + 7) / 8];
        bits.CopyTo(bytes, 0);
        WriteHex(w, name, bytes);
    }

    /// <summary>SSZ bitlist: LSB-first bits followed by a sentinel 1 bit at position <c>n</c>, so the length survives the encoding.</summary>
    private static void WriteBitList(Utf8JsonWriter w, string name, BitArray bits)
    {
        byte[] bytes = new byte[bits.Length / 8 + 1];
        bits.CopyTo(bytes, 0);
        bytes[^1] |= (byte)(1 << (bits.Length % 8));
        WriteHex(w, name, bytes);
    }
}
