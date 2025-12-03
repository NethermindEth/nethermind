// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Docs.Ssz;

using System;
using System.Collections.Generic;

// Attribute and helper stubs keep this reference file self-contained for documentation purposes.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class SszContainerAttribute : Attribute
{
    public SszContainerAttribute()
    {
    }

    public SszContainerAttribute(string name) => Name = name;

    public string? Name { get; }

    public string? Version { get; init; }

    public bool Progressive { get; init; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SszFieldAttribute : Attribute
{
    public SszFieldAttribute(int index) => Index = index;

    public int Index { get; }

    public string? Alias { get; init; }

    public string? SinceVersion { get; init; }

    public string? UntilVersion { get; init; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SszBoundAttribute : Attribute
{
    public SszBoundAttribute(int maxLength) => MaxLength = maxLength;

    public int MaxLength { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class SszUnionAttribute : Attribute
{
    public SszUnionAttribute(string name) => Name = name;

    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SszUnionArmAttribute : Attribute
{
    public SszUnionArmAttribute(byte selector) => Selector = selector;

    public byte Selector { get; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SszOptionalAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SszBitFieldAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SszCustomCodecAttribute : Attribute
{
    public SszCustomCodecAttribute(Type codecType) => CodecType = codecType;

    public Type CodecType { get; }
}

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class SszFixedBytesAttribute : Attribute
{
    public SszFixedBytesAttribute(int length) => Length = length;

    public int Length { get; }
}

public sealed record SszList<T>(IReadOnlyList<T> Items);

public sealed record SszVector<T>(IReadOnlyList<T> Items);

public sealed record SszBitVector(ReadOnlyMemory<byte> Bytes);

public sealed record SszBitList(ReadOnlyMemory<byte> Bytes);

public sealed record SszUnion<T0, T1>(byte Selector, object? Value);

public sealed record SszOptional<T>(T? Value);

public static class ConsensusConstants
{
    public const int SLOTS_PER_EPOCH = 32;
    public const int SLOTS_PER_HISTORICAL_ROOT = 8192;
    public const int EPOCHS_PER_HISTORICAL_VECTOR = 65536;
    public const int EPOCHS_PER_SLASHINGS_VECTOR = 8192;
    public const int MAX_VALIDATORS_PER_COMMITTEE = 2048;
    public const int MAX_PROPOSER_SLASHINGS = 16;
    public const int MAX_ATTESTER_SLASHINGS = 2;
    public const int MAX_ATTESTATIONS = 128;
    public const int MAX_PENDING_ATTESTATIONS = 128;
    public const int MAX_DEPOSITS = 16;
    public const int MAX_VOLUNTARY_EXITS = 16;
    public const int MAX_BLS_TO_EXECUTION_CHANGES = 16;
    public const int MAX_ETH1_DATA_VOTES = 64;
    public const int SYNC_COMMITTEE_SIZE = 512;
    public const int MAX_WITHDRAWALS_PER_PAYLOAD = 16;
    public const int MAX_BLOB_COMMITMENTS_PER_BLOCK = 4096;
    public const int MAX_TRANSACTION_BYTES = 1 << 24;
    public const int MAX_DEPOSIT_REQUESTS_PER_PAYLOAD = 2048;
    public const int MAX_WITHDRAWAL_REQUESTS_PER_PAYLOAD = 2048;
    public const int MAX_CONSOLIDATION_REQUESTS_PER_PAYLOAD = 64;
    public const int MAX_PENDING_BALANCE_TO_EXECUTION_CHANGES = 131072;
    public const int MAX_PENDING_CONSOLIDATIONS = 2048;
    public const int MAX_PENDING_PARTIAL_WITHDRAWALS = 65536;
    public const int MAX_SYNC_COMMITTEE_CONTRIBUTIONS = 512;
}

[SszFixedBytes(32)]
public readonly record struct Bytes32(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(48)]
public readonly record struct Bytes48(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(96)]
public readonly record struct Bytes96(ReadOnlyMemory<byte> Bytes);

public readonly record struct Root(Bytes32 Value);

public readonly record struct Version(uint Value);

public readonly record struct Slot(ulong Value);

public readonly record struct Epoch(ulong Value);

public readonly record struct Gwei(ulong Value);

public readonly record struct ValidatorIndex(uint Value);

public readonly record struct CommitteeIndex(uint Value);

public readonly record struct WithdrawalIndex(ulong Value);

[SszFixedBytes(20)]
public readonly record struct ExecutionAddress(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(48)]
public readonly record struct KzgCommitment(Bytes48 Value);

[SszFixedBytes(48)]
public readonly record struct KzgProof(Bytes48 Value);

[SszFixedBytes(131072)]
public readonly record struct Blob(ReadOnlyMemory<byte> Bytes);

public readonly record struct Transaction(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(48)]
public readonly record struct BlsPublicKey(Bytes48 Value);

[SszFixedBytes(96)]
public readonly record struct BlsSignature(Bytes96 Value);

[SszFixedBytes(32)]
public readonly record struct Graffiti(Bytes32 Value);

[SszFixedBytes(256)]
public readonly record struct LogsBloom(ReadOnlyMemory<byte> Bytes);

[Flags]
public enum ParticipationFlags : byte
{
    None = 0,
    Source = 1,
    Target = 1 << 1,
    Head = 1 << 2,
}

[SszContainer]
public sealed record Fork
{
    [SszField(0)]
    public Version PreviousVersion { get; init; }

    [SszField(1)]
    public Version CurrentVersion { get; init; }

    [SszField(2)]
    public Epoch Epoch { get; init; }
}

[SszContainer]
public sealed record Checkpoint
{
    [SszField(0)]
    public Epoch Epoch { get; init; }

    [SszField(1)]
    public Root Root { get; init; }
}

[SszContainer]
public sealed record Eth1Data
{
    [SszField(0)]
    public Root DepositRoot { get; init; }

    [SszField(1)]
    public ulong DepositCount { get; init; }

    [SszField(2)]
    public Bytes32 BlockHash { get; init; }
}

[SszContainer]
public sealed record Validator
{
    [SszField(0)]
    public BlsPublicKey Pubkey { get; init; }

    [SszField(1)]
    public Bytes32 WithdrawalCredentials { get; init; }

    [SszField(2)]
    public Gwei EffectiveBalance { get; init; }

    [SszField(3)]
    public bool Slashed { get; init; }

    [SszField(4)]
    public Epoch ActivationEligibilityEpoch { get; init; }

    [SszField(5)]
    public Epoch ActivationEpoch { get; init; }

    [SszField(6)]
    public Epoch ExitEpoch { get; init; }

    [SszField(7)]
    public Epoch WithdrawableEpoch { get; init; }
}

[SszContainer]
public sealed record BeaconBlockHeader
{
    [SszField(0)]
    public Slot Slot { get; init; }

    [SszField(1)]
    public ValidatorIndex ProposerIndex { get; init; }

    [SszField(2)]
    public Root ParentRoot { get; init; }

    [SszField(3)]
    public Root StateRoot { get; init; }

    [SszField(4)]
    public Root BodyRoot { get; init; }
}

[SszContainer]
public sealed record SignedBeaconBlockHeader
{
    [SszField(0)]
    public BeaconBlockHeader Message { get; init; } = default!;

    [SszField(1)]
    public BlsSignature Signature { get; init; }
}

[SszContainer]
public sealed record AttestationData
{
    [SszField(0)]
    public Slot Slot { get; init; }

    [SszField(1)]
    public CommitteeIndex Index { get; init; }

    [SszField(2)]
    public Root BeaconBlockRoot { get; init; }

    [SszField(3)]
    public Checkpoint Source { get; init; } = default!;

    [SszField(4)]
    public Checkpoint Target { get; init; } = default!;
}

[SszContainer]
public sealed record IndexedAttestation
{
    [SszField(0)]
    [SszBound(MAX_VALIDATORS_PER_COMMITTEE)]
    public SszList<ValidatorIndex> AttestingIndices { get; init; } = default!;

    [SszField(1)]
    public AttestationData Data { get; init; } = default!;

    [SszField(2)]
    public BlsSignature Signature { get; init; }
}

[SszContainer]
public sealed record Attestation
{
    [SszField(0)]
    [SszBound(MAX_VALIDATORS_PER_COMMITTEE)]
    public SszBitList AggregationBits { get; init; } = default!;

    [SszField(1)]
    public AttestationData Data { get; init; } = default!;

    [SszField(2)]
    public BlsSignature Signature { get; init; }
}

[SszContainer]
public sealed record PendingAttestation
{
    [SszField(0)]
    [SszBound(MAX_VALIDATORS_PER_COMMITTEE)]
    public SszBitList AggregationBits { get; init; } = default!;

    [SszField(1)]
    public AttestationData Data { get; init; } = default!;

    [SszField(2)]
    public Slot InclusionDelay { get; init; }

    [SszField(3)]
    public ValidatorIndex ProposerIndex { get; init; }
}

[SszContainer]
public sealed record HistoricalBatch
{
    [SszField(0)]
    [SszBound(SLOTS_PER_HISTORICAL_ROOT)]
    public SszVector<Root> BlockRoots { get; init; } = default!;

    [SszField(1)]
    [SszBound(SLOTS_PER_HISTORICAL_ROOT)]
    public SszVector<Root> StateRoots { get; init; } = default!;
}

[SszContainer]
public sealed record HistoricalSummary
{
    [SszField(0)]
    public Root BlockSummaryRoot { get; init; }

    [SszField(1)]
    public Root StateSummaryRoot { get; init; }
}

[SszContainer]
public sealed record DepositData
{
    [SszField(0)]
    public BlsPublicKey Pubkey { get; init; }

    [SszField(1)]
    public Bytes32 WithdrawalCredentials { get; init; }

    [SszField(2)]
    public Gwei Amount { get; init; }

    [SszField(3)]
    public BlsSignature Signature { get; init; }
}

[SszContainer]
public sealed record Deposit
{
    [SszField(0)]
    [SszBound(33)]
    public SszVector<Root> Proof { get; init; } = default!;

    [SszField(1)]
    public DepositData Data { get; init; } = default!;
}

[SszContainer]
public sealed record SignedVoluntaryExit
{
    [SszField(0)]
    public VoluntaryExit Message { get; init; } = default!;

    [SszField(1)]
    public BlsSignature Signature { get; init; }
}

[SszContainer]
public sealed record VoluntaryExit
{
    [SszField(0)]
    public Epoch Epoch { get; init; }

    [SszField(1)]
    public ValidatorIndex ValidatorIndex { get; init; }
}

[SszContainer]
public sealed record ProposerSlashing
{
    [SszField(0)]
    public SignedBeaconBlockHeader Header1 { get; init; } = default!;

    [SszField(1)]
    public SignedBeaconBlockHeader Header2 { get; init; } = default!;
}

[SszContainer]
public sealed record AttesterSlashing
{
    [SszField(0)]
    public IndexedAttestation Attestation1 { get; init; } = default!;

    [SszField(1)]
    public IndexedAttestation Attestation2 { get; init; } = default!;
}

[SszContainer]
public sealed record AggregateAndProof
{
    [SszField(0)]
    public ValidatorIndex AggregatorIndex { get; init; }

    [SszField(1)]
    public Attestation Aggregate { get; init; } = default!;

    [SszField(2)]
    public BlsSignature SelectionProof { get; init; }
}

[SszContainer]
public sealed record SignedAggregateAndProof
{
    [SszField(0)]
    public AggregateAndProof Message { get; init; } = default!;

    [SszField(1)]
    public BlsSignature Signature { get; init; }
}

[SszContainer]
public sealed record SyncAggregate
{
    [SszField(0)]
    [SszBound(SYNC_COMMITTEE_SIZE)]
    public SszBitVector SyncCommitteeBits { get; init; } = default!;

    [SszField(1)]
    public BlsSignature SyncCommitteeSignature { get; init; }
}

[SszContainer]
public sealed record SyncCommittee
{
    [SszField(0)]
    [SszBound(SYNC_COMMITTEE_SIZE)]
    public SszVector<BlsPublicKey> Pubkeys { get; init; } = default!;

    [SszField(1)]
    public BlsPublicKey AggregatePubkey { get; init; }
}

[SszContainer]
public sealed record SyncCommitteeContribution
{
    [SszField(0)]
    public Slot Slot { get; init; }

    [SszField(1)]
    public Root BeaconBlockRoot { get; init; }

    [SszField(2)]
    public byte SubcommitteeIndex { get; init; }

    [SszField(3)]
    [SszBound(SYNC_COMMITTEE_SIZE)]
    public SszBitVector AggregationBits { get; init; } = default!;

    [SszField(4)]
    public BlsSignature Signature { get; init; }
}

[SszContainer]
public sealed record ContributionAndProof
{
    [SszField(0)]
    public ValidatorIndex AggregatorIndex { get; init; }

    [SszField(1)]
    public SyncCommitteeContribution Contribution { get; init; } = default!;

    [SszField(2)]
    public BlsSignature SelectionProof { get; init; }
}

[SszContainer]
public sealed record SignedContributionAndProof
{
    [SszField(0)]
    public ContributionAndProof Message { get; init; } = default!;

    [SszField(1)]
    public BlsSignature Signature { get; init; }
}

[SszContainer]
public sealed record Withdrawal
{
    [SszField(0)]
    public WithdrawalIndex Index { get; init; }

    [SszField(1)]
    public ValidatorIndex ValidatorIndex { get; init; }

    [SszField(2)]
    public ExecutionAddress Address { get; init; }

    [SszField(3)]
    public Gwei Amount { get; init; }
}

[SszContainer]
public sealed record ExecutionPayloadHeader
{
    [SszField(0, SinceVersion = "bellatrix")]
    public Bytes32 ParentHash { get; init; }

    [SszField(1, SinceVersion = "bellatrix")]
    public ExecutionAddress FeeRecipient { get; init; }

    [SszField(2, SinceVersion = "bellatrix")]
    public Root StateRoot { get; init; }

    [SszField(3, SinceVersion = "bellatrix")]
    public Root ReceiptsRoot { get; init; }

    [SszField(4, SinceVersion = "bellatrix")]
    public LogsBloom LogsBloom { get; init; }

    [SszField(5, SinceVersion = "bellatrix")]
    public Bytes32 PrevRandao { get; init; }

    [SszField(6, SinceVersion = "bellatrix")]
    public ulong BlockNumber { get; init; }

    [SszField(7, SinceVersion = "bellatrix")]
    public ulong GasLimit { get; init; }

    [SszField(8, SinceVersion = "bellatrix")]
    public ulong GasUsed { get; init; }

    [SszField(9, SinceVersion = "bellatrix")]
    public ulong Timestamp { get; init; }

    [SszField(10, SinceVersion = "bellatrix")]
    public Bytes32 ExtraData { get; init; }

    [SszField(11, SinceVersion = "bellatrix")]
    public Bytes32 BaseFeePerGas { get; init; }

    [SszField(12, SinceVersion = "bellatrix")]
    public Bytes32 BlockHash { get; init; }

    [SszField(13, SinceVersion = "bellatrix")]
    public Root TransactionsRoot { get; init; }

    [SszField(14, SinceVersion = "capella")]
    public Root WithdrawalsRoot { get; init; }

    [SszField(15, SinceVersion = "deneb")]
    public ulong BlobGasUsed { get; init; }

    [SszField(16, SinceVersion = "deneb")]
    public ulong ExcessBlobGas { get; init; }

    [SszField(17, SinceVersion = "deneb")]
    public Root ParentBeaconBlockRoot { get; init; }

    [SszField(18, SinceVersion = "electra")]
    public Root ExecutionRequestsRoot { get; init; }
}

[SszContainer]
public sealed record ExecutionPayload
{
    [SszField(0, SinceVersion = "bellatrix")]
    public Bytes32 ParentHash { get; init; }

    [SszField(1, SinceVersion = "bellatrix")]
    public ExecutionAddress FeeRecipient { get; init; }

    [SszField(2, SinceVersion = "bellatrix")]
    public Root StateRoot { get; init; }

    [SszField(3, SinceVersion = "bellatrix")]
    public Root ReceiptsRoot { get; init; }

    [SszField(4, SinceVersion = "bellatrix")]
    public LogsBloom LogsBloom { get; init; }

    [SszField(5, SinceVersion = "bellatrix")]
    public Bytes32 PrevRandao { get; init; }

    [SszField(6, SinceVersion = "bellatrix")]
    public ulong BlockNumber { get; init; }

    [SszField(7, SinceVersion = "bellatrix")]
    public ulong GasLimit { get; init; }

    [SszField(8, SinceVersion = "bellatrix")]
    public ulong GasUsed { get; init; }

    [SszField(9, SinceVersion = "bellatrix")]
    public ulong Timestamp { get; init; }

    [SszField(10, SinceVersion = "bellatrix")]
    public Bytes32 ExtraData { get; init; }

    [SszField(11, SinceVersion = "bellatrix")]
    public Bytes32 BaseFeePerGas { get; init; }

    [SszField(12, SinceVersion = "bellatrix")]
    public Bytes32 BlockHash { get; init; }

    [SszField(13, SinceVersion = "bellatrix")]
    [SszBound(MAX_TRANSACTION_BYTES)]
    public SszList<Transaction> Transactions { get; init; } = default!;

    [SszField(14, SinceVersion = "capella")]
    [SszBound(MAX_WITHDRAWALS_PER_PAYLOAD)]
    public SszList<Withdrawal> Withdrawals { get; init; } = default!;

    [SszField(15, SinceVersion = "deneb")]
    public ulong BlobGasUsed { get; init; }

    [SszField(16, SinceVersion = "deneb")]
    public ulong ExcessBlobGas { get; init; }

    [SszField(17, SinceVersion = "deneb")]
    public Root ParentBeaconBlockRoot { get; init; }

    [SszField(18, SinceVersion = "electra")]
    public ExecutionRequests ExecutionRequests { get; init; } = default!;
}

[SszContainer(Version = "electra")]
public sealed record ExecutionRequests
{
    [SszField(0, SinceVersion = "electra")]
    [SszBound(MAX_DEPOSIT_REQUESTS_PER_PAYLOAD)]
    public SszList<DepositRequest> Deposits { get; init; } = default!;

    [SszField(1, SinceVersion = "electra")]
    [SszBound(MAX_WITHDRAWAL_REQUESTS_PER_PAYLOAD)]
    public SszList<WithdrawalRequest> Withdrawals { get; init; } = default!;

    [SszField(2, SinceVersion = "electra")]
    [SszBound(MAX_CONSOLIDATION_REQUESTS_PER_PAYLOAD)]
    public SszList<ConsolidationRequest> Consolidations { get; init; } = default!;
}

[SszContainer(Version = "electra")]
public sealed record DepositRequest
{
    [SszField(0)]
    public ExecutionAddress SourceAddress { get; init; }

    [SszField(1)]
    public ExecutionAddress Pubkey { get; init; }

    [SszField(2)]
    public Gwei Amount { get; init; }
}

[SszContainer(Version = "electra")]
public sealed record WithdrawalRequest
{
    [SszField(0)]
    public ValidatorIndex ValidatorIndex { get; init; }

    [SszField(1)]
    public Gwei Amount { get; init; }
}

[SszContainer(Version = "electra")]
public sealed record ConsolidationRequest
{
    [SszField(0)]
    public ValidatorIndex SourceValidatorIndex { get; init; }

    [SszField(1)]
    public ValidatorIndex TargetValidatorIndex { get; init; }
}

[SszContainer]
public sealed record BLSToExecutionChange
{
    [SszField(0)]
    public ValidatorIndex ValidatorIndex { get; init; }

    [SszField(1)]
    public BlsPublicKey FromBlsPubkey { get; init; }

    [SszField(2)]
    public ExecutionAddress ToExecutionAddress { get; init; }
}

[SszContainer]
public sealed record SignedBLSToExecutionChange
{
    [SszField(0)]
    public BLSToExecutionChange Message { get; init; } = default!;

    [SszField(1)]
    public BlsSignature Signature { get; init; }
}

[SszContainer(Version = "deneb")]
public sealed record PendingBalanceToExecutionChange
{
    [SszField(0)]
    public ValidatorIndex ValidatorIndex { get; init; }

    [SszField(1)]
    public ExecutionAddress ExecutionAddress { get; init; }
}

[SszContainer(Version = "electra")]
public sealed record PendingConsolidation
{
    [SszField(0)]
    public ValidatorIndex Source { get; init; }

    [SszField(1)]
    public ValidatorIndex Target { get; init; }
}

[SszContainer(Version = "deneb")]
public sealed record BlobSidecar
{
    [SszField(0)]
    public Slot Slot { get; init; }

    [SszField(1)]
    public ValidatorIndex ProposerIndex { get; init; }

    [SszField(2)]
    public Root BeaconBlockRoot { get; init; }

    [SszField(3)]
    public uint Index { get; init; }

    [SszField(4)]
    public Blob Blob { get; init; }

    [SszField(5)]
    public KzgCommitment KzgCommitment { get; init; }

    [SszField(6)]
    public KzgProof KzgProof { get; init; }

    [SszField(7)]
    public SignedBeaconBlockHeader SignedBlockHeader { get; init; } = default!;
}

[SszContainer(Version = "deneb")]
public sealed record SignedBlobSidecar
{
    [SszField(0)]
    public BlobSidecar Message { get; init; } = default!;

    [SszField(1)]
    public BlsSignature Signature { get; init; }
}

[SszContainer(Progressive = true, Version = "phase0")]
public sealed partial record BeaconBlock
{
    [SszField(0)]
    public Slot Slot { get; init; }

    [SszField(1)]
    public ValidatorIndex ProposerIndex { get; init; }

    [SszField(2)]
    public Root ParentRoot { get; init; }

    [SszField(3)]
    public Root StateRoot { get; init; }

    [SszField(4)]
    public BeaconBlockBody Body { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "phase0")]
public sealed partial record SignedBeaconBlock
{
    [SszField(0)]
    public BeaconBlock Message { get; init; } = default!;

    [SszField(1)]
    public BlsSignature Signature { get; init; }
}

[SszContainer(Progressive = true, Version = "phase0")]
public sealed partial record BeaconBlockBody
{
    [SszField(0)]
    public BlsSignature RandaoReveal { get; init; }

    [SszField(1)]
    public Eth1Data Eth1Data { get; init; } = default!;

    [SszField(2)]
    public Graffiti Graffiti { get; init; }

    [SszField(3)]
    [SszBound(MAX_PROPOSER_SLASHINGS)]
    public SszList<ProposerSlashing> ProposerSlashings { get; init; } = default!;

    [SszField(4)]
    [SszBound(MAX_ATTESTER_SLASHINGS)]
    public SszList<AttesterSlashing> AttesterSlashings { get; init; } = default!;

    [SszField(5)]
    [SszBound(MAX_ATTESTATIONS)]
    public SszList<Attestation> Attestations { get; init; } = default!;

    [SszField(6)]
    [SszBound(MAX_DEPOSITS)]
    public SszList<Deposit> Deposits { get; init; } = default!;

    [SszField(7)]
    [SszBound(MAX_VOLUNTARY_EXITS)]
    public SszList<SignedVoluntaryExit> VoluntaryExits { get; init; } = default!;

    [SszField(8, SinceVersion = "altair")]
    public SyncAggregate SyncAggregate { get; init; } = default!;

    [SszField(9, SinceVersion = "bellatrix")]
    public ExecutionPayload ExecutionPayload { get; init; } = default!;

    [SszField(10, SinceVersion = "capella")]
    [SszBound(MAX_BLS_TO_EXECUTION_CHANGES)]
    public SszList<SignedBLSToExecutionChange> BlsToExecutionChanges { get; init; } = default!;

    [SszField(11, SinceVersion = "deneb")]
    [SszBound(MAX_BLOB_COMMITMENTS_PER_BLOCK)]
    public SszList<KzgCommitment> BlobKzgCommitments { get; init; } = default!;

    [SszField(12, SinceVersion = "electra")]
    public ExecutionRequests ExecutionRequests { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "phase0")]
public sealed partial record BeaconState
{
    [SszField(0)]
    public ulong GenesisTime { get; init; }

    [SszField(1)]
    public Root GenesisValidatorsRoot { get; init; }

    [SszField(2)]
    public Slot Slot { get; init; }

    [SszField(3)]
    public Fork Fork { get; init; } = default!;

    [SszField(4)]
    public BeaconBlockHeader LatestBlockHeader { get; init; } = default!;

    [SszField(5)]
    [SszBound(SLOTS_PER_HISTORICAL_ROOT)]
    public SszVector<Root> BlockRoots { get; init; } = default!;

    [SszField(6)]
    [SszBound(SLOTS_PER_HISTORICAL_ROOT)]
    public SszVector<Root> StateRoots { get; init; } = default!;

    [SszField(7)]
    [SszBound(EPOCHS_PER_HISTORICAL_VECTOR)]
    public SszList<Root> HistoricalRoots { get; init; } = default!;

    [SszField(8)]
    public Eth1Data Eth1Data { get; init; } = default!;

    [SszField(9)]
    [SszBound(MAX_ETH1_DATA_VOTES)]
    public SszList<Eth1Data> Eth1DataVotes { get; init; } = default!;

    [SszField(10)]
    public ulong Eth1DepositIndex { get; init; }

    [SszField(11)]
    [SszBound(int.MaxValue)]
    public SszList<Validator> Validators { get; init; } = default!;

    [SszField(12)]
    [SszBound(int.MaxValue)]
    public SszList<Gwei> Balances { get; init; } = default!;

    [SszField(13)]
    [SszBound(EPOCHS_PER_HISTORICAL_VECTOR)]
    public SszVector<Bytes32> RandaoMixes { get; init; } = default!;

    [SszField(14)]
    [SszBound(EPOCHS_PER_SLASHINGS_VECTOR)]
    public SszVector<Gwei> Slashings { get; init; } = default!;

    [SszField(15, UntilVersion = "altair")]
    [SszBound(MAX_PENDING_ATTESTATIONS)]
    public SszList<PendingAttestation> PreviousEpochAttestations { get; init; } = default!;

    [SszField(16, UntilVersion = "altair")]
    [SszBound(MAX_PENDING_ATTESTATIONS)]
    public SszList<PendingAttestation> CurrentEpochAttestations { get; init; } = default!;

    [SszField(17)]
    public byte JustificationBits { get; init; }

    [SszField(18)]
    public Checkpoint PreviousJustifiedCheckpoint { get; init; } = default!;

    [SszField(19)]
    public Checkpoint CurrentJustifiedCheckpoint { get; init; } = default!;

    [SszField(20)]
    public Checkpoint FinalizedCheckpoint { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "altair")]
public sealed partial record BeaconState
{
    [SszField(21, SinceVersion = "altair")]
    [SszBound(int.MaxValue)]
    public SszList<ParticipationFlags> PreviousEpochParticipation { get; init; } = default!;

    [SszField(22, SinceVersion = "altair")]
    [SszBound(int.MaxValue)]
    public SszList<ParticipationFlags> CurrentEpochParticipation { get; init; } = default!;

    [SszField(23, SinceVersion = "altair")]
    [SszBound(int.MaxValue)]
    public SszList<ulong> InactivityScores { get; init; } = default!;

    [SszField(24, SinceVersion = "altair")]
    public SyncCommittee CurrentSyncCommittee { get; init; } = default!;

    [SszField(25, SinceVersion = "altair")]
    public SyncCommittee NextSyncCommittee { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "bellatrix")]
public sealed partial record BeaconState
{
    [SszField(26, SinceVersion = "bellatrix")]
    public ExecutionPayloadHeader LatestExecutionPayloadHeader { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "capella")]
public sealed partial record BeaconState
{
    [SszField(27, SinceVersion = "capella")]
    public WithdrawalIndex NextWithdrawalIndex { get; init; }

    [SszField(28, SinceVersion = "capella")]
    public ValidatorIndex NextWithdrawalValidatorIndex { get; init; }

    [SszField(29, SinceVersion = "capella")]
    [SszBound(int.MaxValue)]
    public SszList<HistoricalSummary> HistoricalSummaries { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "deneb")]
public sealed partial record BeaconState
{
    [SszField(30, SinceVersion = "deneb")]
    [SszBound(MAX_PENDING_BALANCE_TO_EXECUTION_CHANGES)]
    public SszList<PendingBalanceToExecutionChange> PendingBalanceToExecutionChanges { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "electra")]
public sealed partial record BeaconState
{
    [SszField(31, SinceVersion = "electra")]
    [SszBound(MAX_PENDING_CONSOLIDATIONS)]
    public SszList<PendingConsolidation> PendingConsolidations { get; init; } = default!;

    [SszField(32, SinceVersion = "electra")]
    [SszBound(MAX_PENDING_PARTIAL_WITHDRAWALS)]
    public SszList<Withdrawal> PendingPartialWithdrawals { get; init; } = default!;
}
