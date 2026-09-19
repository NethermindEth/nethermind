// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

// Gloas containers a non-attesting follower must decode: EIP-7732 (ePBS bid/envelope split),
// EIP-7688 (progressive SSZ) and the builder registry (EIP-8282 requests). Field order and
// [SszField] index are copied verbatim from specs/gloas/beacon-chain.md on the
// ethereum/consensus-specs `master` branch, fetched 2026-09-19 (post v1.7.0-beta.0, pre v1.7.0-beta.1;
// see the survey cited in the task for the exact churn window). Every [SszField]-indexed type below
// is a spec `ProgressiveContainer`: the SszGenerator (SszType.cs `HasAnyFieldIndex`) treats any
// container where every property carries [SszField(index)] as EIP-7495/7916 progressive, merkleizing
// with Merkle.MerkleizeProgressive + MixInActiveFields instead of the plain pad-to-power-of-two tree.
// None of these Gloas types declare a gap (ACTIVE_FIELDS has no unused position), so the index is
// just the declaration order 0..N-1 and decode/encode is byte-identical to a plain container (per
// ethereum/ssz-specs `container.py`: "Both encode identically: fixed-size fields inline, variable-size
// fields behind offsets" - only merkleization differs between Container and ProgressiveContainer).

/// <summary>Gloas <c>Builder</c> (specs/gloas/beacon-chain.md, "New containers").</summary>
[SszContainer]
public partial class Builder
{
    public BlsPublicKey Pubkey { get; set; }

    public byte Version { get; set; }

    public Address? ExecutionAddress { get; set; }

    public ulong Balance { get; set; }

    public ulong DepositEpoch { get; set; }

    public ulong WithdrawableEpoch { get; set; }
}

/// <summary>Gloas <c>BuilderPendingWithdrawal</c> (specs/gloas/beacon-chain.md, "New containers").</summary>
[SszContainer]
public partial class BuilderPendingWithdrawal
{
    public Address? FeeRecipient { get; set; }

    public ulong Amount { get; set; }

    /// <summary><c>BuilderIndex</c> (a <c>Uint64</c>), represented as <see cref="ulong"/> like every other validator/committee index in this codebase.</summary>
    public ulong BuilderIndex { get; set; }
}

/// <summary>Gloas <c>BuilderPendingPayment</c> (specs/gloas/beacon-chain.md, "New containers").</summary>
[SszContainer]
public partial class BuilderPendingPayment
{
    public ulong Weight { get; set; }

    public BuilderPendingWithdrawal? Withdrawal { get; set; }

    public ulong ProposerIndex { get; set; }
}

/// <summary>Gloas <c>BuilderDepositRequest</c> (specs/gloas/beacon-chain.md, "New containers"; EIP-8282).</summary>
[SszContainer]
public partial class BuilderDepositRequest
{
    public BlsPublicKey Pubkey { get; set; }

    public Hash256? WithdrawalCredentials { get; set; }

    public ulong Amount { get; set; }

    public BlsSignature Signature { get; set; }
}

/// <summary>Gloas <c>BuilderExitRequest</c> (specs/gloas/beacon-chain.md, "New containers"; EIP-8282).</summary>
[SszContainer]
public partial class BuilderExitRequest
{
    public Address? SourceAddress { get; set; }

    public BlsPublicKey Pubkey { get; set; }
}

/// <summary>
/// Gloas <c>PayloadTimelinessCommittee</c> (specs/gloas/beacon-chain.md, "New <c>PayloadTimelinessCommittee</c>"):
/// <c>Vector[ValidatorIndex, PTC_SIZE]</c>. Wrapped in a single-field container (not marked
/// <c>isCollectionItself</c>: that flag only special-cases a sole List/ProgressiveList field) so it can
/// be addressed as an element of <see cref="BeaconStateGloas.PtcWindow"/> — a container with exactly one
/// fixed-size field serializes and merkleizes identically to the bare vector (merkleize of one chunk is
/// that chunk), so this is wire- and root-identical to the spec type without any generator changes.
/// </summary>
[SszContainer]
public partial class PayloadTimelinessCommittee
{
    /// <remarks>Length is <c>PTC_SIZE</c> (512).</remarks>
    [SszVector(512)]
    public ulong[]? Indices { get; set; }
}

/// <summary>Gloas <c>PayloadAttestationData</c> (specs/gloas/beacon-chain.md, "New containers").</summary>
[SszContainer]
public partial class PayloadAttestationData
{
    public Hash256? BeaconBlockRoot { get; set; }

    public ulong Slot { get; set; }

    public bool PayloadPresent { get; set; }

    public bool BlobDataAvailable { get; set; }
}

/// <summary>Gloas <c>PayloadAttestationMessage</c> (specs/gloas/beacon-chain.md, "New containers").</summary>
[SszContainer]
public partial class PayloadAttestationMessage
{
    public ulong ValidatorIndex { get; set; }

    public PayloadAttestationData? Data { get; set; }

    public BlsSignature Signature { get; set; }
}

/// <summary>Gloas <c>PayloadAttestation</c> (specs/gloas/beacon-chain.md, "New containers"): <c>ProgressiveContainer</c>, <c>ACTIVE_FIELDS</c> width 3.</summary>
[SszContainer]
public partial class PayloadAttestation
{
    /// <remarks><c>PayloadTimelinessCommitteeBits</c>: <c>BitVector[PTC_SIZE]</c>.</remarks>
    [SszField(0)]
    [SszVector(512)]
    public BitArray? AggregationBits { get; set; }

    [SszField(1)]
    public PayloadAttestationData? Data { get; set; }

    [SszField(2)]
    public BlsSignature Signature { get; set; }
}

/// <summary>Gloas <c>IndexedPayloadAttestation</c> (specs/gloas/beacon-chain.md, "New containers"): <c>ProgressiveContainer</c>, <c>ACTIVE_FIELDS</c> width 3.</summary>
[SszContainer]
public partial class IndexedPayloadAttestation
{
    /// <remarks><c>PayloadTimelinessCommitteeIndices</c>: <c>List[ValidatorIndex, PTC_SIZE]</c> (a regular bounded list, not progressive).</remarks>
    [SszField(0)]
    [SszList(512)]
    public ulong[]? AttestingIndices { get; set; }

    [SszField(1)]
    public PayloadAttestationData? Data { get; set; }

    [SszField(2)]
    public BlsSignature Signature { get; set; }
}

/// <summary>
/// Gloas <c>ExecutionPayloadBid</c> (specs/gloas/beacon-chain.md, "New containers"): <c>ProgressiveContainer</c>,
/// <c>ACTIVE_FIELDS</c> width 12. The bid a follower needs even without building blocks: it carries the
/// KZG commitments used to validate <c>DataColumnSidecar</c>s before the matching envelope arrives.
/// </summary>
[SszContainer]
public partial class ExecutionPayloadBid
{
    [SszField(0)]
    public Hash256? ParentBlockHash { get; set; }

    [SszField(1)]
    public Hash256? ParentBlockRoot { get; set; }

    [SszField(2)]
    public Hash256? BlockHash { get; set; }

    [SszField(3)]
    public Hash256? PrevRandao { get; set; }

    [SszField(4)]
    public Address? FeeRecipient { get; set; }

    [SszField(5)]
    public ulong GasLimit { get; set; }

    /// <remarks><c>BuilderIndex</c>.</remarks>
    [SszField(6)]
    public ulong BuilderIndex { get; set; }

    [SszField(7)]
    public ulong Slot { get; set; }

    [SszField(8)]
    public ulong Value { get; set; }

    [SszField(9)]
    public ulong ExecutionPayment { get; set; }

    /// <remarks><c>BlobKZGCommitments</c>: <c>ProgressiveList[KZGCommitment]</c> (EIP-7688).</remarks>
    [SszField(10)]
    [SszProgressiveList]
    public SszKzgCommitment[]? BlobKzgCommitments { get; set; }

    [SszField(11)]
    public Hash256? ExecutionRequestsRoot { get; set; }
}

/// <summary>Gloas <c>SignedExecutionPayloadBid</c> (specs/gloas/beacon-chain.md, "New containers").</summary>
[SszContainer]
public partial class SignedExecutionPayloadBid
{
    public ExecutionPayloadBid? Message { get; set; }

    public BlsSignature Signature { get; set; }
}

/// <summary>
/// Gloas <c>Transaction</c> (specs/gloas/beacon-chain.md, "Modified <c>Transaction</c>"):
/// <c>ProgressiveList[Byte]</c>, distinct from the pre-Gloas <see cref="Transaction"/>
/// (a bounded <c>List[Byte, MAX_BYTES_PER_TRANSACTION]</c>) because the two merkleize differently even
/// though both wire-encode as a raw byte blob. <c>isCollectionItself</c> strips the would-be offset so
/// this serializes as the bare progressive byte list, matching how it sits inside
/// <see cref="ExecutionPayloadGloas.Transactions"/> (itself a <c>ProgressiveList[TransactionGloas]</c>).
/// </summary>
[SszContainer(isCollectionItself: true)]
public partial class TransactionGloas
{
    [SszProgressiveList]
    public byte[]? Bytes { get; set; }
}

/// <summary>
/// Gloas <c>ExecutionPayload</c> (specs/gloas/beacon-chain.md, "Modified containers"): <c>ProgressiveContainer</c>,
/// <c>ACTIVE_FIELDS</c> width 19. Adds <c>block_access_list</c> (EIP-7928) and <c>slot_number</c> (EIP-7843);
/// <c>transactions</c>/<c>withdrawals</c> become progressive lists (EIP-7688).
/// </summary>
[SszContainer]
public partial class ExecutionPayloadGloas
{
    [SszField(0)]
    public Hash256? ParentHash { get; set; }

    [SszField(1)]
    public Address? FeeRecipient { get; set; }

    [SszField(2)]
    public Hash256? StateRoot { get; set; }

    [SszField(3)]
    public Hash256? ReceiptsRoot { get; set; }

    [SszField(4)]
    public Bloom? LogsBloom { get; set; }

    [SszField(5)]
    public Hash256? PrevRandao { get; set; }

    [SszField(6)]
    public ulong BlockNumber { get; set; }

    [SszField(7)]
    public ulong GasLimit { get; set; }

    [SszField(8)]
    public ulong GasUsed { get; set; }

    [SszField(9)]
    public ulong Timestamp { get; set; }

    [SszField(10)]
    [SszList(32)]
    public byte[]? ExtraData { get; set; }

    [SszField(11)]
    public UInt256 BaseFeePerGas { get; set; }

    [SszField(12)]
    public Hash256? BlockHash { get; set; }

    [SszField(13)]
    [SszProgressiveList]
    public TransactionGloas[]? Transactions { get; set; }

    [SszField(14)]
    [SszProgressiveList]
    public Withdrawal[]? Withdrawals { get; set; }

    [SszField(15)]
    public ulong BlobGasUsed { get; set; }

    [SszField(16)]
    public ulong ExcessBlobGas { get; set; }

    /// <remarks><c>BlockAccessList</c> (EIP-7928): <c>ProgressiveList[Byte]</c>, opaque to the CL.</remarks>
    [SszField(17)]
    [SszProgressiveList]
    public byte[]? BlockAccessList { get; set; }

    /// <remarks>EIP-7843 (<c>SLOTNUM</c> opcode).</remarks>
    [SszField(18)]
    public ulong SlotNumber { get; set; }
}

/// <summary>
/// Gloas <c>ExecutionRequests</c> (specs/gloas/beacon-chain.md, "Modified containers"): <c>ProgressiveContainer</c>,
/// <c>ACTIVE_FIELDS</c> width 5. Adds <c>builder_deposits</c>/<c>builder_exits</c> (EIP-8282).
/// </summary>
[SszContainer]
public partial class ExecutionRequestsGloas
{
    [SszField(0)]
    [SszProgressiveList]
    public DepositRequest[]? Deposits { get; set; }

    [SszField(1)]
    [SszProgressiveList]
    public WithdrawalRequest[]? Withdrawals { get; set; }

    [SszField(2)]
    [SszProgressiveList]
    public ConsolidationRequest[]? Consolidations { get; set; }

    [SszField(3)]
    [SszProgressiveList]
    public BuilderDepositRequest[]? BuilderDeposits { get; set; }

    [SszField(4)]
    [SszProgressiveList]
    public BuilderExitRequest[]? BuilderExits { get; set; }
}

/// <summary>
/// Gloas <c>ExecutionPayloadEnvelope</c> (specs/gloas/beacon-chain.md, "New containers"): <c>ProgressiveContainer</c>,
/// <c>ACTIVE_FIELDS</c> width 5. Where transactions, withdrawals and execution requests now live: the
/// <c>beacon_block</c> gossip message alone no longer carries full block content.
/// </summary>
[SszContainer]
public partial class ExecutionPayloadEnvelope
{
    [SszField(0)]
    public ExecutionPayloadGloas? Payload { get; set; }

    [SszField(1)]
    public ExecutionRequestsGloas? ExecutionRequests { get; set; }

    /// <remarks><c>BuilderIndex</c>.</remarks>
    [SszField(2)]
    public ulong BuilderIndex { get; set; }

    [SszField(3)]
    public Hash256? BeaconBlockRoot { get; set; }

    [SszField(4)]
    public Hash256? ParentBeaconBlockRoot { get; set; }
}

/// <summary>Gloas <c>SignedExecutionPayloadEnvelope</c> (specs/gloas/beacon-chain.md, "New containers").</summary>
[SszContainer]
public partial class SignedExecutionPayloadEnvelope
{
    public ExecutionPayloadEnvelope? Message { get; set; }

    public BlsSignature Signature { get; set; }
}

/// <summary>
/// Gloas <c>Attestation</c> (specs/gloas/beacon-chain.md, "Modified containers"; EIP-7688): <c>ProgressiveContainer</c>,
/// <c>ACTIVE_FIELDS</c> width 4. Needed to decode a Gloas <see cref="BeaconBlockBodyGloas"/>, which can still
/// carry earlier attestations.
/// </summary>
[SszContainer]
public partial class AttestationGloas
{
    /// <remarks><c>AggregationBits</c>: <c>ProgressiveBitList</c> (no static limit).</remarks>
    [SszField(0)]
    [SszProgressiveBitlist]
    public BitArray? AggregationBits { get; set; }

    [SszField(1)]
    public AttestationData? Data { get; set; }

    [SszField(2)]
    public BlsSignature Signature { get; set; }

    /// <remarks><c>CommitteeBits</c>: unchanged <c>Bitvector[MAX_COMMITTEES_PER_SLOT]</c> (EIP-7549).</remarks>
    [SszField(3)]
    [SszVector(64)]
    public BitArray? CommitteeBits { get; set; }
}

/// <summary>Gloas <c>IndexedAttestation</c> (specs/gloas/beacon-chain.md, "Modified containers"; EIP-7688): <c>ProgressiveContainer</c>, width 3.</summary>
[SszContainer]
public partial class IndexedAttestationGloas
{
    /// <remarks><c>AttestingIndices</c>: <c>ProgressiveList[ValidatorIndex]</c> (no static limit).</remarks>
    [SszField(0)]
    [SszProgressiveList]
    public ulong[]? AttestingIndices { get; set; }

    [SszField(1)]
    public AttestationData? Data { get; set; }

    [SszField(2)]
    public BlsSignature Signature { get; set; }
}

/// <summary>Gloas <c>AttesterSlashing</c>: same two fields as pre-Gloas, retyped to the Gloas <c>IndexedAttestation</c>.</summary>
[SszContainer]
public partial class AttesterSlashingGloas
{
    public IndexedAttestationGloas? Attestation1 { get; set; }

    public IndexedAttestationGloas? Attestation2 { get; set; }
}

/// <summary>
/// Gloas <c>BeaconBlockBody</c> (specs/gloas/beacon-chain.md, "Modified containers"; EIP-7732/EIP-7688):
/// <c>ProgressiveContainer</c>, <c>ACTIVE_FIELDS</c> width 13. <c>execution_payload</c>,
/// <c>blob_kzg_commitments</c> and <c>execution_requests</c> are removed (they now live in
/// <see cref="ExecutionPayloadEnvelope"/>); <c>signed_execution_payload_bid</c>, <c>payload_attestations</c>
/// and <c>parent_execution_requests</c> are new.
/// </summary>
[SszContainer]
public partial class BeaconBlockBodyGloas
{
    [SszField(0)]
    public BlsSignature RandaoReveal { get; set; }

    [SszField(1)]
    public Eth1Data? Eth1Data { get; set; }

    [SszField(2)]
    public Hash256? Graffiti { get; set; }

    [SszField(3)]
    [SszProgressiveList]
    public ProposerSlashing[]? ProposerSlashings { get; set; }

    [SszField(4)]
    [SszProgressiveList]
    public AttesterSlashingGloas[]? AttesterSlashings { get; set; }

    [SszField(5)]
    [SszProgressiveList]
    public AttestationGloas[]? Attestations { get; set; }

    [SszField(6)]
    [SszProgressiveList]
    public Deposit[]? Deposits { get; set; }

    [SszField(7)]
    [SszProgressiveList]
    public SignedVoluntaryExit[]? VoluntaryExits { get; set; }

    [SszField(8)]
    public SyncAggregate? SyncAggregate { get; set; }

    [SszField(9)]
    [SszProgressiveList]
    public SignedBlsToExecutionChange[]? BlsToExecutionChanges { get; set; }

    [SszField(10)]
    public SignedExecutionPayloadBid? SignedExecutionPayloadBid { get; set; }

    [SszField(11)]
    [SszProgressiveList]
    public PayloadAttestation[]? PayloadAttestations { get; set; }

    [SszField(12)]
    public ExecutionRequestsGloas? ParentExecutionRequests { get; set; }
}

/// <summary>Gloas <c>BeaconBlock</c>: unchanged shape, <c>body</c> retyped to <see cref="BeaconBlockBodyGloas"/>.</summary>
[SszContainer]
public partial class BeaconBlockGloas
{
    public ulong Slot { get; set; }

    public ulong ProposerIndex { get; set; }

    public Hash256? ParentRoot { get; set; }

    public Hash256? StateRoot { get; set; }

    public BeaconBlockBodyGloas? Body { get; set; }
}

/// <summary>Gloas <c>SignedBeaconBlock</c>.</summary>
[SszContainer]
public partial class SignedBeaconBlockGloas
{
    public BeaconBlockGloas? Message { get; set; }

    public BlsSignature Signature { get; set; }
}
