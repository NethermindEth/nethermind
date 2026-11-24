// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Globalization;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>
/// Reads beacon containers from the beacon-api JSON representation <see cref="BeaconJsonWriter"/> writes: uints as decimal strings, byte
/// vectors and lists as 0x-prefixed hex, bitlists with their length sentinel, field names in snake_case.
/// </summary>
/// <remarks>
/// Every field is required and checked for its type and byte length; list limits are left to the SSZ codec of the container read.
/// A malformed document throws <see cref="FormatException"/>, or the <see cref="KeyNotFoundException"/> or <see cref="InvalidOperationException"/>
/// of <see cref="JsonElement"/> for a missing field or one of the wrong JSON kind.
/// </remarks>
internal static class BeaconJsonReader
{
    /// <summary>Reads a Fulu <c>SignedBlockContents</c>: the signed Electra block and the blobs and cell proofs of its payload.</summary>
    public static (SignedBeaconBlock Block, byte[][] KzgProofs, byte[][] Blobs) ReadSignedBlockContents(JsonElement json) =>
        (SignedBlock(json.GetProperty("signed_block")), List(json, "kzg_proofs", HexValue), List(json, "blobs", HexValue));

    private static SignedBeaconBlock SignedBlock(JsonElement json) => new() { Message = Block(json.GetProperty("message")), Signature = Signature(json, "signature") };

    private static BeaconBlock Block(JsonElement json) => new()
    {
        Slot = UInt(json, "slot"),
        ProposerIndex = UInt(json, "proposer_index"),
        ParentRoot = Hash(json, "parent_root"),
        StateRoot = Hash(json, "state_root"),
        Body = Body(json.GetProperty("body")),
    };

    private static BeaconBlockBody Body(JsonElement json) => new()
    {
        RandaoReveal = Signature(json, "randao_reveal"),
        Eth1Data = Eth1Data(json.GetProperty("eth1_data")),
        Graffiti = Hash(json, "graffiti"),
        ProposerSlashings = List(json, "proposer_slashings", static slashing => new ProposerSlashing
        {
            SignedHeader1 = SignedHeader(slashing.GetProperty("signed_header_1")),
            SignedHeader2 = SignedHeader(slashing.GetProperty("signed_header_2")),
        }),
        AttesterSlashings = List(json, "attester_slashings", static slashing => new AttesterSlashing
        {
            Attestation1 = IndexedAttestation(slashing.GetProperty("attestation_1")),
            Attestation2 = IndexedAttestation(slashing.GetProperty("attestation_2")),
        }),
        Attestations = List(json, "attestations", static attestation => new Attestation
        {
            AggregationBits = BitList(attestation, "aggregation_bits", (int)Presets.MaxValidatorsPerSlot),
            Data = AttestationData(attestation.GetProperty("data")),
            Signature = Signature(attestation, "signature"),
            CommitteeBits = BitVector(attestation, "committee_bits", Presets.MaxCommitteesPerSlot),
        }),
        Deposits = List(json, "deposits", static deposit => new Deposit
        {
            Proof = List(deposit, "proof", static proof => new Hash256(FixedHex(proof, Hash256.Size))),
            Data = DepositData(deposit.GetProperty("data")),
        }),
        VoluntaryExits = List(json, "voluntary_exits", static exit => new SignedVoluntaryExit
        {
            Message = new VoluntaryExit { Epoch = UInt(exit.GetProperty("message"), "epoch"), ValidatorIndex = UInt(exit.GetProperty("message"), "validator_index") },
            Signature = Signature(exit, "signature"),
        }),
        SyncAggregate = new SyncAggregate
        {
            SyncCommitteeBits = BitVector(json.GetProperty("sync_aggregate"), "sync_committee_bits", Presets.SyncCommitteeSize),
            SyncCommitteeSignature = Signature(json.GetProperty("sync_aggregate"), "sync_committee_signature"),
        },
        ExecutionPayload = ExecutionPayload(json.GetProperty("execution_payload")),
        BlsToExecutionChanges = List(json, "bls_to_execution_changes", static change => new SignedBlsToExecutionChange
        {
            Message = new BlsToExecutionChange
            {
                ValidatorIndex = UInt(change.GetProperty("message"), "validator_index"),
                FromBlsPubkey = Pubkey(change.GetProperty("message"), "from_bls_pubkey"),
                ToExecutionAddress = Address(change.GetProperty("message"), "to_execution_address"),
            },
            Signature = Signature(change, "signature"),
        }),
        BlobKzgCommitments = List(json, "blob_kzg_commitments", static commitment => SszKzgCommitment.FromSpan(FixedHex(commitment, SszKzgCommitment.KzgCommitmentLength))),
        ExecutionRequests = ExecutionRequests(json.GetProperty("execution_requests")),
    };

    private static Types.ExecutionPayload ExecutionPayload(JsonElement json) => new()
    {
        ParentHash = Hash(json, "parent_hash"),
        FeeRecipient = Address(json, "fee_recipient"),
        StateRoot = Hash(json, "state_root"),
        ReceiptsRoot = Hash(json, "receipts_root"),
        LogsBloom = new Bloom(FixedHex(json.GetProperty("logs_bloom"), Bloom.ByteLength)),
        PrevRandao = Hash(json, "prev_randao"),
        BlockNumber = UInt(json, "block_number"),
        GasLimit = UInt(json, "gas_limit"),
        GasUsed = UInt(json, "gas_used"),
        Timestamp = UInt(json, "timestamp"),
        ExtraData = HexValue(json.GetProperty("extra_data")),
        BaseFeePerGas = UInt256.Parse(Text(json.GetProperty("base_fee_per_gas")), NumberStyles.None),
        BlockHash = Hash(json, "block_hash"),
        Transactions = List(json, "transactions", static transaction => new SszTransaction { Bytes = HexValue(transaction) }),
        Withdrawals = List(json, "withdrawals", static withdrawal => new SszWithdrawal
        {
            Index = UInt(withdrawal, "index"),
            ValidatorIndex = UInt(withdrawal, "validator_index"),
            Address = Address(withdrawal, "address"),
            Amount = UInt(withdrawal, "amount"),
        }),
        BlobGasUsed = UInt(json, "blob_gas_used"),
        ExcessBlobGas = UInt(json, "excess_blob_gas"),
    };

    private static ExecutionRequests ExecutionRequests(JsonElement json) => new()
    {
        Deposits = List(json, "deposits", static deposit => new DepositRequest
        {
            Pubkey = Pubkey(deposit, "pubkey"),
            WithdrawalCredentials = Hash(deposit, "withdrawal_credentials"),
            Amount = UInt(deposit, "amount"),
            Signature = Signature(deposit, "signature"),
            Index = UInt(deposit, "index"),
        }),
        Withdrawals = List(json, "withdrawals", static withdrawal => new WithdrawalRequest
        {
            SourceAddress = Address(withdrawal, "source_address"),
            ValidatorPubkey = Pubkey(withdrawal, "validator_pubkey"),
            Amount = UInt(withdrawal, "amount"),
        }),
        Consolidations = List(json, "consolidations", static consolidation => new ConsolidationRequest
        {
            SourceAddress = Address(consolidation, "source_address"),
            SourcePubkey = Pubkey(consolidation, "source_pubkey"),
            TargetPubkey = Pubkey(consolidation, "target_pubkey"),
        }),
    };

    private static Eth1Data Eth1Data(JsonElement json) => new() { DepositRoot = Hash(json, "deposit_root"), DepositCount = UInt(json, "deposit_count"), BlockHash = Hash(json, "block_hash") };

    private static SignedBeaconBlockHeader SignedHeader(JsonElement json)
    {
        JsonElement header = json.GetProperty("message");
        return new SignedBeaconBlockHeader
        {
            Message = new BeaconBlockHeader
            {
                Slot = UInt(header, "slot"),
                ProposerIndex = UInt(header, "proposer_index"),
                ParentRoot = Hash(header, "parent_root"),
                StateRoot = Hash(header, "state_root"),
                BodyRoot = Hash(header, "body_root"),
            },
            Signature = Signature(json, "signature"),
        };
    }

    private static IndexedAttestation IndexedAttestation(JsonElement json) => new()
    {
        AttestingIndices = List(json, "attesting_indices", static index => UIntValue(index)),
        Data = AttestationData(json.GetProperty("data")),
        Signature = Signature(json, "signature"),
    };

    private static AttestationData AttestationData(JsonElement json) => new()
    {
        Slot = UInt(json, "slot"),
        Index = UInt(json, "index"),
        BeaconBlockRoot = Hash(json, "beacon_block_root"),
        Source = Checkpoint(json.GetProperty("source")),
        Target = Checkpoint(json.GetProperty("target")),
    };

    private static Checkpoint Checkpoint(JsonElement json) => new() { Epoch = UInt(json, "epoch"), Root = Hash(json, "root") };

    private static DepositData DepositData(JsonElement json) => new()
    {
        Pubkey = Pubkey(json, "pubkey"),
        WithdrawalCredentials = Hash(json, "withdrawal_credentials"),
        Amount = UInt(json, "amount"),
        Signature = Signature(json, "signature"),
    };

    private static T[] List<T>(JsonElement json, string name, Func<JsonElement, T> read)
    {
        JsonElement array = json.GetProperty(name);
        T[] items = new T[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray()) items[i++] = read(item);
        return items;
    }

    private static ulong UInt(JsonElement json, string name) => UIntValue(json.GetProperty(name));

    private static ulong UIntValue(JsonElement json) =>
        ulong.TryParse(Text(json), NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : throw new FormatException($"'{json}' is not a decimal uint64.");

    private static Hash256 Hash(JsonElement json, string name) => new(FixedHex(json.GetProperty(name), Hash256.Size));

    private static BlsSignature Signature(JsonElement json, string name) => new(FixedHex(json.GetProperty(name), BlsSignature.Length));

    private static BlsPublicKey Pubkey(JsonElement json, string name) => new(FixedHex(json.GetProperty(name), BlsPublicKey.Length));

    private static Address Address(JsonElement json, string name) => new(FixedHex(json.GetProperty(name), Core.Address.Size));

    private static BitArray BitList(JsonElement json, string name, int limit) =>
        HexConvert.TryParseBitList(Text(json.GetProperty(name)), limit, out BitArray? bits) ? bits : throw new FormatException($"{name} is not a bitlist of at most {limit} bits.");

    private static BitArray BitVector(JsonElement json, string name, int length) =>
        HexConvert.TryParseBitVector(Text(json.GetProperty(name)), length, out BitArray? bits) ? bits : throw new FormatException($"{name} is not a bitvector of {length} bits.");

    private static byte[] FixedHex(JsonElement json, int length)
    {
        byte[] bytes = HexValue(json);
        return bytes.Length == length ? bytes : throw new FormatException($"'{json}' is not {length} bytes long.");
    }

    private static byte[] HexValue(JsonElement json) =>
        Text(json) is var hex && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && hex.Length % 2 == 0 ? Bytes.FromHexString(hex) : throw new FormatException($"'{hex}' is not 0x-prefixed hex.");

    private static string Text(JsonElement json) => json.GetString() ?? throw new FormatException("A field is null.");
}
