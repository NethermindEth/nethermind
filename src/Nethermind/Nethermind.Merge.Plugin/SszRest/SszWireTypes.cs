// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

[SszContainer]
public partial struct SszBytes8
{
    [SszVector(8)] public byte[]? Bytes { get; set; }
}

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
public partial struct WithdrawalWire
{
    public ulong Index { get; set; }
    public ulong ValidatorIndex { get; set; }
    public Address Address { get; set; }
    public ulong Amount { get; set; }
}

[SszContainer]
public partial struct PayloadStatusWire
{
    public byte Status { get; set; }
    [SszList(1)] public Hash256[]? LatestValidHash { get; set; }
    [SszList(1024)] public byte[]? ValidationError { get; set; }
}

[SszContainer]
public partial struct ForkchoiceStateWire
{
    public Hash256 HeadBlockHash { get; set; }
    public Hash256 SafeBlockHash { get; set; }
    public Hash256 FinalizedBlockHash { get; set; }
}

[SszContainer]
public partial struct PayloadAttributesV1Wire
{
    public ulong Timestamp { get; set; }
    public Hash256 PrevRandao { get; set; }
    public Address SuggestedFeeRecipient { get; set; }
}

[SszContainer]
public partial struct PayloadAttributesV2Wire
{
    public ulong Timestamp { get; set; }
    public Hash256 PrevRandao { get; set; }
    public Address SuggestedFeeRecipient { get; set; }
    [SszList(16)] public WithdrawalWire[]? Withdrawals { get; set; }
}

[SszContainer]
public partial struct PayloadAttributesV3Wire
{
    public ulong Timestamp { get; set; }
    public Hash256 PrevRandao { get; set; }
    public Address SuggestedFeeRecipient { get; set; }
    [SszList(16)] public WithdrawalWire[]? Withdrawals { get; set; }
    [SszList(1)] public Hash256[]? ParentBeaconBlockRoot { get; set; }
}

[SszContainer]
public partial struct PayloadAttributesWire
{
    public ulong Timestamp { get; set; }
    public Hash256 PrevRandao { get; set; }
    public Address SuggestedFeeRecipient { get; set; }
    [SszList(16)] public WithdrawalWire[]? Withdrawals { get; set; }
    [SszList(1)] public Hash256[]? ParentBeaconBlockRoot { get; set; }
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
}

[SszContainer]
public partial struct ForkchoiceUpdatedResponseWire
{
    public PayloadStatusWire PayloadStatus { get; set; }
    [SszList(1)] public SszBytes8[]? PayloadId { get; set; }
}

[SszContainer]
public partial struct NewPayloadV1RequestWire
{
    public SszExecutionPayloadV1 ExecutionPayload { get; set; }
}

[SszContainer]
public partial struct NewPayloadV2RequestWire
{
    public SszExecutionPayload ExecutionPayload { get; set; }
}

[SszContainer]
public partial struct NewPayloadV3RequestWire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    [SszList(4096)] public Hash256[]? ExpectedBlobVersionedHashes { get; set; }
    public Hash256 ParentBeaconBlockRoot { get; set; }
}

[SszContainer]
public partial struct NewPayloadV4RequestWire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    [SszList(4096)] public Hash256[]? ExpectedBlobVersionedHashes { get; set; }
    public Hash256 ParentBeaconBlockRoot { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
}

[SszContainer]
public partial struct NewPayloadV5RequestWire
{
    public SszExecutionPayloadV4 ExecutionPayload { get; set; }
    [SszList(4096)] public Hash256[]? ExpectedBlobVersionedHashes { get; set; }
    public Hash256 ParentBeaconBlockRoot { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
}

[SszContainer]
public partial struct SszKzgCommitment
{
    [SszVector(48)] public byte[]? Bytes { get; set; }
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
public partial struct GetPayloadResponseV2Wire
{
    public SszExecutionPayload ExecutionPayload { get; set; }
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

[SszContainer]
public partial struct GetPayloadResponseV4Wire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
    public BlobsBundleV1Wire BlobsBundle { get; set; }
    public bool ShouldOverrideBuilder { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
}

[SszContainer]
public partial struct GetPayloadResponseV5Wire
{
    public SszExecutionPayloadV3 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
    public BlobsBundleV2Wire BlobsBundle { get; set; }
    public bool ShouldOverrideBuilder { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
}

[SszContainer]
public partial struct GetPayloadResponseV6Wire
{
    public SszExecutionPayloadV4 ExecutionPayload { get; set; }
    public UInt256 BlockValue { get; set; }
    public BlobsBundleV2Wire BlobsBundle { get; set; }
    public bool ShouldOverrideBuilder { get; set; }
    [SszList(256)] public SszTransaction[]? ExecutionRequests { get; set; }
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
public partial struct GetBlobsV1ResponseWire
{
    [SszList(128)] public BlobAndProofV1Wire[]? BlobsAndProofs { get; set; }
}

[SszContainer]
public partial struct SszCapabilityName
{
    [SszList(128)] public byte[]? Name { get; set; }
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
    [SszList(16)] public WithdrawalWire[]? Withdrawals { get; set; }
}

[SszContainer]
public partial struct ExecutionPayloadBodyV2Wire
{
    [SszList(0x10_0000)] public SszTransaction[]? Transactions { get; set; }
    [SszList(16)] public WithdrawalWire[]? Withdrawals { get; set; }
    [SszList(1)] public SszTransaction[]? BlockAccessList { get; set; }
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
/// Each inner list has 0 elements for unknown blocks, 1 element for known blocks.
/// </summary>
[SszContainer]
public partial struct NullablePayloadBodyV1Wire
{
    [SszList(1)] public ExecutionPayloadBodyV1Wire[]? Body { get; set; }
}

[SszContainer]
public partial struct PayloadBodiesV1ResponseWire
{
    [SszList(32)] public NullablePayloadBodyV1Wire[]? PayloadBodies { get; set; }
}

/// <summary>
/// Each inner list has 0 elements for unknown blocks, 1 element for known blocks.
/// </summary>
[SszContainer]
public partial struct NullablePayloadBodyV2Wire
{
    [SszList(1)] public ExecutionPayloadBodyV2Wire[]? Body { get; set; }
}

[SszContainer]
public partial struct PayloadBodiesV2ResponseWire
{
    [SszList(32)] public NullablePayloadBodyV2Wire[]? PayloadBodies { get; set; }
}

[SszContainer]
public partial struct BlobAndProofV2Wire
{
    [SszVector(0x2_0000)] public byte[]? Blob { get; set; }
    // V2 uses CELLS_PER_EXT_BLOB (128) proofs per blob
    [SszList(128)] public SszKzgCommitment[]? Proofs { get; set; }
}

[SszContainer]
public partial struct GetBlobsV2ResponseWire
{
    [SszList(128)] public BlobAndProofV2Wire[]? BlobsAndProofs { get; set; }
}

/// <summary>
/// V3 uses nullable per-element encoding: List[BlobAndProofV2, 1] where 0 = missing, 1 = present.
/// </summary>
[SszContainer]
public partial struct NullableBlobAndProofV2Wire
{
    [SszList(1)] public BlobAndProofV2Wire[]? BlobAndProof { get; set; }
}

[SszContainer]
public partial struct GetBlobsV3ResponseWire
{
    [SszList(128)] public NullableBlobAndProofV2Wire[]? BlobsAndProofs { get; set; }
}

[SszContainer]
public partial struct TransitionConfigurationV1Wire
{
    // uint256 encoded little-endian (same convention as BaseFeePerGas)
    public UInt256 TerminalTotalDifficulty { get; set; }
    public Hash256 TerminalBlockHash { get; set; }
    public ulong TerminalBlockNumber { get; set; }
}

[SszContainer]
public partial struct ExchangeTransitionConfigurationRequestWire
{
    public TransitionConfigurationV1Wire TransitionConfiguration { get; set; }
}
