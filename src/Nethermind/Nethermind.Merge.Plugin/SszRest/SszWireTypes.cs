// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// SSZ representation of a single variable-length transaction byte-string.
/// Wraps ByteList[<c>MAX_BYTES_PER_TRANSACTION</c>] as a container so it can be used in lists,
/// where <c>MAX_BYTES_PER_TRANSACTION</c> = <c>2**30</c> (1073741824)
/// </summary>
[SszContainer(isCollectionItself: true)]
public partial struct SszTransaction
{
    [SszList(0x4000_0000)] public byte[]? Bytes { get; set; }
}

[SszContainer]
public partial struct SszWithdrawal
{
    public ulong Index { get; set; }
    public ulong ValidatorIndex { get; set; }
    public Address Address { get; set; }
    public ulong Amount { get; set; }
}

[SszContainer(isCollectionItself: true)]
public partial struct SszValidationError
{
    [SszList(1024)] public byte[]? Bytes { get; set; }
}

[SszContainer]
public partial struct PayloadStatusWire
{
    public byte Status { get; set; }
    [SszList(1)] public Hash256[]? LatestValidHash { get; set; }
    [SszList(1)] public SszValidationError[]? ValidationError { get; set; }
}

[SszContainer]
public partial struct ForkchoiceStateWire
{
    public Hash256 HeadBlockHash { get; set; }
    public Hash256 SafeBlockHash { get; set; }
    public Hash256 FinalizedBlockHash { get; set; }
}

/// <summary>
/// Marker for the per-fork SSZ payload-attributes wire structs, exposing the field needed
/// by fork-routing logic (timestamp). Each <c>PayloadAttributes*Wire</c> already has the
/// <c>Timestamp</c> property; the interface lets generic helpers consume them uniformly.
/// </summary>
public interface ISszPayloadAttributesWire
{
    ulong Timestamp { get; }
}

[SszContainer]
public partial struct PayloadAttributesV1Wire : ISszPayloadAttributesWire
{
    public ulong Timestamp { get; set; }
    public Hash256 PrevRandao { get; set; }
    public Address SuggestedFeeRecipient { get; set; }
}

[SszContainer]
public partial struct PayloadAttributesV2Wire : ISszPayloadAttributesWire
{
    public ulong Timestamp { get; set; }
    public Hash256 PrevRandao { get; set; }
    public Address SuggestedFeeRecipient { get; set; }
    [SszList(16)] public SszWithdrawal[]? Withdrawals { get; set; }
}

[SszContainer]
public partial struct PayloadAttributesV3Wire : ISszPayloadAttributesWire
{
    public ulong Timestamp { get; set; }
    public Hash256 PrevRandao { get; set; }
    public Address SuggestedFeeRecipient { get; set; }
    [SszList(16)] public SszWithdrawal[]? Withdrawals { get; set; }
    public Hash256 ParentBeaconBlockRoot { get; set; }
}

[SszContainer]
public partial struct PayloadAttributesWire : ISszPayloadAttributesWire
{
    public ulong Timestamp { get; set; }
    public Hash256 PrevRandao { get; set; }
    public Address SuggestedFeeRecipient { get; set; }
    [SszList(16)] public SszWithdrawal[]? Withdrawals { get; set; }
    public Hash256 ParentBeaconBlockRoot { get; set; }
    public ulong SlotNumber { get; set; }
}

[SszContainer]
public partial struct ForkchoiceUpdatedV1RequestWire
{
    public ForkchoiceStateWire ForkchoiceState { get; set; }
    [SszList(1)] public PayloadAttributesV1Wire[]? PayloadAttributes { get; set; }
}

[SszContainer]
public partial struct ForkchoiceUpdatedV2RequestWire
{
    public ForkchoiceStateWire ForkchoiceState { get; set; }
    [SszList(1)] public PayloadAttributesV2Wire[]? PayloadAttributes { get; set; }
}

[SszContainer]
public partial struct ForkchoiceUpdatedV3RequestWire
{
    public ForkchoiceStateWire ForkchoiceState { get; set; }
    [SszList(1)] public PayloadAttributesV3Wire[]? PayloadAttributes { get; set; }
}

[SszContainer]
public partial struct ForkchoiceUpdatedRequestWire
{
    public ForkchoiceStateWire ForkchoiceState { get; set; }
    [SszList(1)] public PayloadAttributesWire[]? PayloadAttributes { get; set; }
    [SszList(1)] public SszCustodyColumns[]? CustodyColumns { get; set; }
}

[SszContainer(isCollectionItself: true)]
public partial struct SszCustodyColumns
{
    [SszVector(128)] public BitArray? Bits { get; set; }
}

[SszContainer(isCollectionItself: true)]
public partial struct SszPayloadId
{
    [SszVector(8)] public byte[]? Bytes { get; set; }
}

[SszContainer]
public partial struct ForkchoiceUpdatedResponseWire
{
    public PayloadStatusWire PayloadStatus { get; set; }
    [SszList(1)] public SszPayloadId[]? PayloadId { get; set; }
}

[SszContainer]
public partial struct NewPayloadV1RequestWire
{
    public SszExecutionPayloadV1 ExecutionPayload { get; set; }
}

[SszContainer]
public partial struct NewPayloadV2RequestWire
{
    public SszExecutionPayloadV2 ExecutionPayload { get; set; }
}

[SszContainer]
public partial struct NewPayloadV3RequestWire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    public Hash256 ParentBeaconBlockRoot { get; set; }
}

[SszContainer]
public partial struct NewPayloadV4RequestWire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    public Hash256 ParentBeaconBlockRoot { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
}

[SszContainer]
public partial struct NewPayloadV5RequestWire
{
    public SszExecutionPayloadV4 ExecutionPayload { get; set; }
    public Hash256 ParentBeaconBlockRoot { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
}

[SszContainer]
public partial struct SszBlob
{
    [SszVector(0x2_0000)] public byte[]? Bytes { get; set; }
}

[SszContainer]
public partial struct BlobsBundleV1Wire
{
    [SszList(4096)] public SszKzgCommitment[]? Commitments { get; set; }
    [SszList(4096)] public SszKzgCommitment[]? Proofs { get; set; }
    [SszList(4096)] public SszBlob[]? Blobs { get; set; }
}

[SszContainer]
public partial struct BlobsBundleV2Wire
{
    [SszList(4096)] public SszKzgCommitment[]? Commitments { get; set; }
    [SszList(0x8_0000)] public SszKzgCommitment[]? Proofs { get; set; }
    [SszList(4096)] public SszBlob[]? Blobs { get; set; }
}

[SszContainer]
public partial struct BuiltPayloadParisWire
{
    public SszExecutionPayloadV1 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
}

[SszContainer]
public partial struct GetPayloadResponseV2Wire
{
    public SszExecutionPayloadV2 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
}

[SszContainer]
public partial struct GetPayloadResponseV3Wire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
    public BlobsBundleV1Wire BlobsBundle { get; set; }
    public bool ShouldOverrideBuilder { get; set; }
}

// Field order: execution_requests precedes should_override_builder (diverges from JSON-RPC).
[SszContainer]
public partial struct GetPayloadResponseV4Wire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
    public BlobsBundleV1Wire BlobsBundle { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
    public bool ShouldOverrideBuilder { get; set; }
}

[SszContainer]
public partial struct GetPayloadResponseV5Wire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
    public BlobsBundleV2Wire BlobsBundle { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
    public bool ShouldOverrideBuilder { get; set; }
}

[SszContainer]
public partial struct GetPayloadResponseV6Wire
{
    public SszExecutionPayloadV4 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
    public BlobsBundleV2Wire BlobsBundle { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
    public bool ShouldOverrideBuilder { get; set; }
}

[SszContainer]
public partial struct GetBlobsRequestWire
{
    [SszList(128)] public Hash256[]? VersionedHashes { get; set; }
}

[SszContainer]
public partial struct BlobAndProofV1Wire
{
    [SszVector(0x2_0000)] public byte[]? Blob { get; set; }
    [SszVector(48)] public byte[]? Proof { get; set; }
}

[SszContainer]
public partial struct BlobV1EntryWire
{
    public bool Available { get; set; }
    public BlobAndProofV1Wire Contents { get; set; }
}

[SszContainer]
public partial struct GetBlobsV1ResponseWire
{
    [SszList(128)] public BlobV1EntryWire[]? Entries { get; set; }
}

[SszContainer(isCollectionItself: true)]
public partial struct SszCapabilityName
{
    [SszList(64)] public byte[]? Name { get; set; }
}

[SszContainer]
public partial struct ExchangeCapabilitiesRequestWire
{
    [SszList(64)] public SszCapabilityName[]? Capabilities { get; set; }
}

[SszContainer]
public partial struct ExchangeCapabilitiesResponseWire
{
    [SszList(64)] public SszCapabilityName[]? Capabilities { get; set; }
}

[SszContainer]
public partial struct ClientVersionWire
{
    [SszList(2)] public byte[]? Code { get; set; }
    [SszList(64)] public byte[]? Name { get; set; }
    [SszList(64)] public byte[]? Version { get; set; }
    [SszVector(4)] public byte[]? Commit { get; set; }
}

[SszContainer]
public partial struct GetClientVersionRequestWire
{
    public ClientVersionWire ClientVersion { get; set; }
}

[SszContainer]
public partial struct GetClientVersionResponseWire
{
    [SszList(4)] public ClientVersionWire[]? Versions { get; set; }
}

[SszContainer]
public partial struct ExecutionPayloadBodyV1Wire
{
    [SszList(0x10_0000)] public SszTransaction[]? Transactions { get; set; }
    [SszList(16)] public SszWithdrawal[]? Withdrawals { get; set; }
}

[SszContainer]
public partial struct ExecutionPayloadBodyV2Wire
{
    [SszList(0x10_0000)] public SszTransaction[]? Transactions { get; set; }
    [SszList(16)] public SszWithdrawal[]? Withdrawals { get; set; }
    [SszList(0x4000_0000)] public byte[]? BlockAccessList { get; set; }
}

[SszContainer]
public partial struct GetPayloadBodiesByHashRequestWire
{
    [SszList(32)] public Hash256[]? BlockHashes { get; set; }
}

[SszContainer]
public partial struct GetPayloadBodiesByRangeRequestWire
{
    public ulong Start { get; set; }
    public ulong Count { get; set; }
}

/// <summary>
/// <c>BodyEntry { available: Boolean; body: ExecutionPayloadBody }</c> per spec.
/// </summary>
[SszContainer]
public partial struct BodyEntryV1Wire
{
    public bool Available { get; set; }
    public ExecutionPayloadBodyV1Wire Body { get; set; }
}

[SszContainer]
public partial struct PayloadBodiesV1ResponseWire
{
    [SszList(32)] public BodyEntryV1Wire[]? Entries { get; set; }
}

/// <summary>
/// <c>BodyEntry { available: Boolean; body: ExecutionPayloadBodyAmsterdam }</c> per spec.
/// </summary>
[SszContainer]
public partial struct BodyEntryV2Wire
{
    public bool Available { get; set; }
    public ExecutionPayloadBodyV2Wire Body { get; set; }
}

[SszContainer]
public partial struct PayloadBodiesV2ResponseWire
{
    [SszList(32)] public BodyEntryV2Wire[]? Entries { get; set; }
}

[SszContainer]
public partial struct BlobAndProofV2Wire
{
    [SszVector(0x2_0000)] public byte[]? Blob { get; set; }
    // V2 uses CELLS_PER_EXT_BLOB (128) proofs per blob
    [SszList(128)] public SszKzgCommitment[]? Proofs { get; set; }
}

[SszContainer]
public partial struct BlobV2EntryWire
{
    public bool Available { get; set; }
    public BlobAndProofV2Wire Contents { get; set; }
}

[SszContainer]
public partial struct GetBlobsV2ResponseWire
{
    [SszList(128)] public BlobV2EntryWire[]? Entries { get; set; }
}

[SszContainer]
public partial struct GetBlobsV4RequestWire
{
    [SszList(128)] public Hash256[]? BlobVersionedHashes { get; set; }
    [SszVector(128)] public BitArray? IndicesBitarray { get; set; }
}

[SszContainer(isCollectionItself: true)]
public partial struct NullableBlobCellWire
{
    [SszList(1)] public SszBlobCell[]? Cell { get; set; }
}

[SszContainer(isCollectionItself: true)]
public partial struct NullableKzgProofWire
{
    [SszList(1)] public SszKzgCommitment[]? Proof { get; set; }
}

[SszContainer]
public partial struct BlobCellsAndProofsWire
{
    [SszList(128)] public NullableBlobCellWire[]? BlobCells { get; set; }
    [SszList(128)] public NullableKzgProofWire[]? Proofs { get; set; }
}

/// <summary>
/// <c>BlobV4Entry { available: Boolean; contents: BlobCellsAndProofs }</c> per spec.
/// </summary>
[SszContainer]
public partial struct BlobV4EntryWire
{
    public bool Available { get; set; }
    public BlobCellsAndProofsWire Contents { get; set; }
}

[SszContainer]
public partial struct GetBlobsV4ResponseWire
{
    [SszList(128)] public BlobV4EntryWire[]? Entries { get; set; }
}
