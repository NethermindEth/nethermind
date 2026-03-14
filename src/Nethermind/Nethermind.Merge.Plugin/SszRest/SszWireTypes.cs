// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

// ─── Primitive wrappers for fixed-size items in SSZ lists ───

[SszSerializable]
public struct SszHash32
{
    [SszVector(32)]
    public byte[] Bytes { get; set; }
}

[SszSerializable]
public struct SszPayloadId
{
    [SszVector(8)]
    public byte[] Bytes { get; set; }
}

[SszSerializable]
public struct SszKzgCommitment
{
    [SszVector(48)]
    public byte[] Bytes { get; set; }
}

[SszSerializable]
public struct SszKzgProof
{
    [SszVector(48)]
    public byte[] Bytes { get; set; }
}

[SszSerializable]
public struct SszBlob
{
    [SszVector(131072)]
    public byte[] Bytes { get; set; }
}

// Transaction: isCollectionItself so it encodes as raw bytes (List[byte, MAX]).
// In a list, each element gets an offset — matching standard SSZ List[Transaction, MAX].
[SszSerializable(isCollectionItself: true)]
public struct SszTransaction
{
    [SszList(1073741824)]
    public byte[] Data { get; set; }
}

// Capability: isCollectionItself so it encodes as raw UTF-8 bytes (List[byte, 64]).
[SszSerializable(isCollectionItself: true)]
public struct SszCapability
{
    [SszList(64)]
    public byte[] Name { get; set; }
}

// ─── ForkchoiceState: 96 bytes fixed ───

[SszSerializable]
public struct ForkchoiceStateWire
{
    [SszVector(32)]
    public byte[] HeadBlockHash { get; set; }

    [SszVector(32)]
    public byte[] SafeBlockHash { get; set; }

    [SszVector(32)]
    public byte[] FinalizedBlockHash { get; set; }
}

// ─── Withdrawal: 44 bytes fixed ───

[SszSerializable]
public struct WithdrawalWire
{
    public ulong Index { get; set; }
    public ulong ValidatorIndex { get; set; }

    [SszVector(20)]
    public byte[] Address { get; set; }

    public ulong Amount { get; set; }
}

// ─── PayloadAttributes V3 ───

[SszSerializable]
public struct PayloadAttributesV3Wire
{
    public ulong Timestamp { get; set; }

    [SszVector(32)]
    public byte[] PrevRandao { get; set; }

    [SszVector(20)]
    public byte[] SuggestedFeeRecipient { get; set; }

    [SszList(16)]
    public WithdrawalWire[] Withdrawals { get; set; }

    [SszVector(32)]
    public byte[] ParentBeaconBlockRoot { get; set; }
}

// ─── ForkchoiceUpdated request ───

[SszSerializable]
public struct ForkchoiceUpdatedRequestWire
{
    public ForkchoiceStateWire ForkchoiceState { get; set; }

    [SszList(1)]
    public PayloadAttributesV3Wire[] PayloadAttributes { get; set; }
}

// ─── PayloadStatus response ───

[SszSerializable]
public struct PayloadStatusWire
{
    // SszVector(1) byte[] instead of bare byte to avoid Merkleizer.Feed() ambiguity.
    [SszVector(1)]
    public byte[] Status { get; set; }

    [SszList(1)]
    public SszHash32[] LatestValidHash { get; set; }

    [SszList(1024)]
    public byte[] ValidationError { get; set; }
}

// ─── ForkchoiceUpdated response ───

[SszSerializable]
public struct ForkchoiceUpdatedResponseWire
{
    public PayloadStatusWire PayloadStatus { get; set; }

    [SszList(1)]
    public SszPayloadId[] PayloadId { get; set; }
}

// ─── ExecutionPayload V1 (Paris): 508 bytes fixed ───

[SszSerializable]
public struct ExecutionPayloadV1Wire
{
    [SszVector(32)] public byte[] ParentHash { get; set; }
    [SszVector(20)] public byte[] FeeRecipient { get; set; }
    [SszVector(32)] public byte[] StateRoot { get; set; }
    [SszVector(32)] public byte[] ReceiptsRoot { get; set; }
    [SszVector(256)] public byte[] LogsBloom { get; set; }
    [SszVector(32)] public byte[] PrevRandao { get; set; }
    public ulong BlockNumber { get; set; }
    public ulong GasLimit { get; set; }
    public ulong GasUsed { get; set; }
    public ulong Timestamp { get; set; }
    [SszList(32)] public byte[] ExtraData { get; set; }
    public UInt256 BaseFeePerGas { get; set; }
    [SszVector(32)] public byte[] BlockHash { get; set; }
    [SszList(1048576)] public SszTransaction[] Transactions { get; set; }
}

// ─── ExecutionPayload V2 (Shanghai): 512 bytes fixed ───

[SszSerializable]
public struct ExecutionPayloadV2Wire
{
    [SszVector(32)] public byte[] ParentHash { get; set; }
    [SszVector(20)] public byte[] FeeRecipient { get; set; }
    [SszVector(32)] public byte[] StateRoot { get; set; }
    [SszVector(32)] public byte[] ReceiptsRoot { get; set; }
    [SszVector(256)] public byte[] LogsBloom { get; set; }
    [SszVector(32)] public byte[] PrevRandao { get; set; }
    public ulong BlockNumber { get; set; }
    public ulong GasLimit { get; set; }
    public ulong GasUsed { get; set; }
    public ulong Timestamp { get; set; }
    [SszList(32)] public byte[] ExtraData { get; set; }
    public UInt256 BaseFeePerGas { get; set; }
    [SszVector(32)] public byte[] BlockHash { get; set; }
    [SszList(1048576)] public SszTransaction[] Transactions { get; set; }
    [SszList(16)] public WithdrawalWire[] Withdrawals { get; set; }
}

// ─── ExecutionPayload V3 (Deneb): 528 bytes fixed ───

[SszSerializable]
public struct ExecutionPayloadV3Wire
{
    [SszVector(32)] public byte[] ParentHash { get; set; }
    [SszVector(20)] public byte[] FeeRecipient { get; set; }
    [SszVector(32)] public byte[] StateRoot { get; set; }
    [SszVector(32)] public byte[] ReceiptsRoot { get; set; }
    [SszVector(256)] public byte[] LogsBloom { get; set; }
    [SszVector(32)] public byte[] PrevRandao { get; set; }
    public ulong BlockNumber { get; set; }
    public ulong GasLimit { get; set; }
    public ulong GasUsed { get; set; }
    public ulong Timestamp { get; set; }
    [SszList(32)] public byte[] ExtraData { get; set; }
    public UInt256 BaseFeePerGas { get; set; }
    [SszVector(32)] public byte[] BlockHash { get; set; }
    [SszList(1048576)] public SszTransaction[] Transactions { get; set; }
    [SszList(16)] public WithdrawalWire[] Withdrawals { get; set; }
    public ulong BlobGasUsed { get; set; }
    public ulong ExcessBlobGas { get; set; }
}

// ─── ExecutionRequests: 12 bytes fixed (3 offsets) ───

[SszSerializable]
public struct ExecutionRequestsWire
{
    [SszList(8388608)]
    public byte[] Deposits { get; set; }

    [SszList(8388608)]
    public byte[] Withdrawals { get; set; }

    [SszList(8388608)]
    public byte[] Consolidations { get; set; }
}

// ─── BlobsBundle: 12 bytes fixed (3 offsets) ───

[SszSerializable]
public struct BlobsBundleWire
{
    [SszList(4096)]
    public SszKzgCommitment[] Commitments { get; set; }

    [SszList(4096)]
    public SszKzgProof[] Proofs { get; set; }

    [SszList(4096)]
    public SszBlob[] Blobs { get; set; }
}

// ─── NewPayload V3 request: 40 bytes fixed ───

[SszSerializable]
public struct NewPayloadV3RequestWire
{
    public ExecutionPayloadV3Wire ExecutionPayload { get; set; }

    [SszList(4096)]
    public SszHash32[] VersionedHashes { get; set; }

    [SszVector(32)]
    public byte[] ParentBeaconBlockRoot { get; set; }
}

// ─── NewPayload V4 request: 44 bytes fixed ───

[SszSerializable]
public struct NewPayloadV4RequestWire
{
    public ExecutionPayloadV3Wire ExecutionPayload { get; set; }

    [SszList(4096)]
    public SszHash32[] VersionedHashes { get; set; }

    [SszVector(32)]
    public byte[] ParentBeaconBlockRoot { get; set; }

    public ExecutionRequestsWire ExecutionRequests { get; set; }
}

// ─── GetPayload response V3/V4: 45 bytes fixed ───

[SszSerializable]
public struct GetPayloadResponseWire
{
    public ExecutionPayloadV3Wire ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
    public BlobsBundleWire BlobsBundle { get; set; }
    public bool ShouldOverrideBuilder { get; set; }
    public ExecutionRequestsWire ExecutionRequests { get; set; }
}

// ─── GetBlobs request ───

[SszSerializable]
public struct GetBlobsRequestWire
{
    [SszList(4096)]
    public SszHash32[] VersionedHashes { get; set; }
}

// ─── Exchange capabilities ───

[SszSerializable]
public struct ExchangeCapabilitiesWire
{
    [SszList(128)]
    public SszCapability[] Capabilities { get; set; }
}

// ─── Client version ───

[SszSerializable]
public struct ClientVersionV1Wire
{
    [SszList(8)]
    public byte[] Code { get; set; }

    [SszList(64)]
    public byte[] Name { get; set; }

    [SszList(64)]
    public byte[] Version { get; set; }

    [SszVector(4)]
    public byte[] Commit { get; set; }
}

[SszSerializable]
public struct ClientVersionResponseWire
{
    [SszList(16)]
    public ClientVersionV1Wire[] Versions { get; set; }
}
