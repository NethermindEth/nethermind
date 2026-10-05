// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Text;
using System.Collections;
using System.IO.Pipelines;
using System.Text.Json;
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
internal static partial class BeaconJsonWriter
{
    public static partial void WriteSignedBeaconBlock(Utf8JsonWriter w, SignedBeaconBlock block);

    private static partial void WriteBeaconBlock(Utf8JsonWriter w, BeaconBlock block);

    private static partial void WriteBeaconBlockBody(Utf8JsonWriter w, BeaconBlockBody body);

    /// <summary>Writes a Gloas signed block: the bid replaces the payload, and the parent's execution requests ride in the body (specs/gloas/beacon-chain.md <c>BeaconBlockBody</c>).</summary>
    public static partial void WriteSignedBeaconBlock(Utf8JsonWriter w, SignedBeaconBlockGloas block);

    private static partial void WriteBeaconBlockBody(Utf8JsonWriter w, BeaconBlockBodyGloas body);

    private static partial void WriteSignedExecutionPayloadBid(Utf8JsonWriter w, SignedExecutionPayloadBid signed);

    private static partial void WritePayloadAttestationData(Utf8JsonWriter w, PayloadAttestationData data);

    private static partial void WriteExecutionRequests(Utf8JsonWriter w, ExecutionRequestsGloas requests);

    private static partial void WriteProposerSlashings(Utf8JsonWriter w, ProposerSlashing[] slashings);

    private static partial void WriteDeposits(Utf8JsonWriter w, Deposit[] deposits);

    private static partial void WriteVoluntaryExits(Utf8JsonWriter w, SignedVoluntaryExit[] exits);

    private static partial void WriteSyncAggregate(Utf8JsonWriter w, SyncAggregate aggregate);

    private static partial void WriteBlsToExecutionChanges(Utf8JsonWriter w, SignedBlsToExecutionChange[] changes);

    private static partial void WriteKzgCommitments(Utf8JsonWriter w, SszKzgCommitment[] commitments);

    private static partial void WriteExecutionRequests(Utf8JsonWriter w, ExecutionRequests requests);

    private static partial void WriteExecutionPayload(Utf8JsonWriter w, ExecutionPayload payload);

    /// <summary>Writes a Gloas signed execution payload envelope (consensus-specs v1.7.0-beta.2 gloas/beacon-chain.md <c>SignedExecutionPayloadEnvelope</c>).</summary>
    /// <remarks>execution_requests carries the Gloas builder_deposits and builder_exits, which the Electra schema the published operation references lacks.</remarks>
    public static partial void WriteSignedExecutionPayloadEnvelope(Utf8JsonWriter w, SignedExecutionPayloadEnvelope signed);

    /// <summary>The Deneb payload fields plus Gloas <c>block_access_list</c> (EIP-7928) and <c>slot_number</c> (EIP-7843).</summary>
    private static partial void WriteExecutionPayload(Utf8JsonWriter w, ExecutionPayloadGloas payload);

    private static partial void WriteWithdrawals(Utf8JsonWriter w, Types.Withdrawal[] withdrawals);

    private static partial void WriteExecutionPayloadHeader(Utf8JsonWriter w, ExecutionPayloadHeader header);

    public static void WriteAttestations(Utf8JsonWriter w, Attestation[] attestations)
    {
        w.WriteStartArray();
        foreach (Attestation attestation in attestations)
        {
            WriteAttestation(w, attestation.AggregationBits!, attestation.Data!, attestation.Signature, attestation.CommitteeBits!);
        }
        w.WriteEndArray();
    }

    public static void WriteAttestations(Utf8JsonWriter w, AttestationGloas[] attestations)
    {
        w.WriteStartArray();
        foreach (AttestationGloas attestation in attestations)
        {
            WriteAttestation(w, attestation.AggregationBits!, attestation.Data!, attestation.Signature, attestation.CommitteeBits!);
        }
        w.WriteEndArray();
    }

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

    private static partial void WriteAttestationData(Utf8JsonWriter w, AttestationData data);

    private static partial void WriteSignedBeaconBlockHeader(Utf8JsonWriter w, SignedBeaconBlockHeader header);

    private static partial void WriteBeaconBlockHeader(Utf8JsonWriter w, BeaconBlockHeader header);

    private static partial void WriteCheckpoint(Utf8JsonWriter w, Checkpoint checkpoint);

    private static partial void WriteEth1Data(Utf8JsonWriter w, Eth1Data data);

    private static partial void WriteValidator(Utf8JsonWriter w, Validator validator);

    /// <summary>Streams an Electra or Fulu <c>BeaconState</c>, flushing between elements of its large lists.</summary>
    /// <remarks>Only a Fulu state carries <c>proposer_lookahead</c> (EIP-7917).</remarks>
    public static partial Task WriteBeaconStateAsync(BeaconJsonStream s, BeaconStateElectra state);

    public static Task WritePendingDepositsAsync(BeaconJsonStream s, PendingDeposit[] deposits) => WriteArrayValueAsync(s, deposits, WritePendingDeposit);
    public static Task WritePendingPartialWithdrawalsAsync(BeaconJsonStream s, PendingPartialWithdrawal[] withdrawals) => WriteArrayValueAsync(s, withdrawals, WritePendingPartialWithdrawal);
    public static Task WritePendingConsolidationsAsync(BeaconJsonStream s, PendingConsolidation[] consolidations) => WriteArrayValueAsync(s, consolidations, WritePendingConsolidation);
    public static Task WriteUIntArrayValueAsync(BeaconJsonStream s, ulong[] items) => WriteArrayValueAsync(s, items, WriteUIntValue);

    private static partial void WritePendingDeposit(Utf8JsonWriter w, PendingDeposit deposit);

    private static partial void WritePendingPartialWithdrawal(Utf8JsonWriter w, PendingPartialWithdrawal withdrawal);

    private static partial void WritePendingConsolidation(Utf8JsonWriter w, PendingConsolidation consolidation);

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

    private static Task WriteArrayAsync<T>(BeaconJsonStream s, string name, T[] items, Action<Utf8JsonWriter, T> writeItem)
    {
        s.Writer.WritePropertyName(name);
        return WriteArrayValueAsync(s, items, writeItem);
    }

    private static async Task WriteArrayValueAsync<T>(BeaconJsonStream s, T[] items, Action<Utf8JsonWriter, T> writeItem)
    {
        s.Writer.WriteStartArray();
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

    internal static void WriteHexValue(Utf8JsonWriter w, ReadOnlySpan<byte> bytes)
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
