// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Docs.Ssz.Execution;

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

public static class ExecutionConstants
{
    public const int MAX_ACCESS_LIST_STORAGE_KEYS = 64;
    public const int MAX_ACCESS_LIST_ENTRIES = 64;
    public const int MAX_CALLDATA_BYTES = 1 << 20; // 1 MiB upper bound for documentation
    public const int MAX_BLOB_HASHES_PER_TRANSACTION = 6;
    public const int MAX_BLOB_COMMITMENTS_PER_PAYLOAD = 4096;
    public const int MAX_BLOBS_PER_PAYLOAD = 4096;
    public const int MAX_PROOFS_PER_PAYLOAD = 4096;
    public const int MAX_WITHDRAWALS_PER_PAYLOAD = 16;
    public const int MAX_EXTRA_DATA_BYTES = 32;
}

[SszFixedBytes(32)]
public readonly record struct Hash32(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(32)]
public readonly record struct UInt256(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(32)]
public readonly record struct BlobVersionedHash(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(32)]
public readonly record struct StorageKey(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(32)]
public readonly record struct SignatureScalar(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(1)]
public readonly record struct SignatureYParity(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(20)]
public readonly record struct ExecutionAddress(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(32)]
public readonly record struct Root(Hash32 Value);

[SszFixedBytes(32)]
public readonly record struct VersionedHash(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(131072)]
public readonly record struct Blob(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(48)]
public readonly record struct KzgCommitment(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(48)]
public readonly record struct KzgProof(ReadOnlyMemory<byte> Bytes);

[SszFixedBytes(ExecutionConstants.MAX_EXTRA_DATA_BYTES)]
public readonly record struct ExtraData(ReadOnlyMemory<byte> Bytes);

public readonly record struct TransactionCalldata(ReadOnlyMemory<byte> Bytes);

public readonly record struct Wei(UInt256 Value);

public readonly record struct TransactionNonce(ulong Value);

public readonly record struct GasLimit(ulong Value);

public readonly record struct GasPrice(UInt256 Value);

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SszOptionalAttribute : Attribute
{
}

public sealed record AccessListEntry
{
    [SszField(0)]
    public ExecutionAddress Address { get; init; }

    [SszField(1)]
    [SszBound(ExecutionConstants.MAX_ACCESS_LIST_STORAGE_KEYS)]
    public SszList<StorageKey> StorageKeys { get; init; } = default!;
}

public sealed record AccessList
{
    [SszField(0)]
    [SszBound(ExecutionConstants.MAX_ACCESS_LIST_ENTRIES)]
    public SszList<AccessListEntry> Entries { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "frontier")]
public sealed partial record ExecutionTransaction
{
    [SszField(0)]
    public TransactionNonce Nonce { get; init; }

    [SszField(1)]
    public GasPrice GasPrice { get; init; }

    [SszField(2)]
    public GasLimit GasLimit { get; init; }

    [SszField(3)]
    public ExecutionAddress? To { get; init; }

    [SszField(4)]
    public Wei Value { get; init; }

    [SszField(5)]
    [SszBound(ExecutionConstants.MAX_CALLDATA_BYTES)]
    public TransactionCalldata Input { get; init; }

    [SszField(6)]
    public SignatureYParity YParity { get; init; }

    [SszField(7)]
    public SignatureScalar R { get; init; }

    [SszField(8)]
    public SignatureScalar S { get; init; }
}

[SszContainer(Progressive = true, Version = "berlin")]
public sealed partial record ExecutionTransaction
{
    [SszField(9, SinceVersion = "berlin")]
    public AccessList? AccessList { get; init; }
}

[SszContainer(Progressive = true, Version = "london")]
public sealed partial record ExecutionTransaction
{
    [SszField(10, SinceVersion = "london")]
    public UInt256? MaxPriorityFeePerGas { get; init; }

    [SszField(11, SinceVersion = "london")]
    public UInt256? MaxFeePerGas { get; init; }
}

[SszContainer(Progressive = true, Version = "cancun")]
public sealed partial record ExecutionTransaction
{
    [SszField(12, SinceVersion = "cancun")]
    [SszBound(ExecutionConstants.MAX_BLOB_HASHES_PER_TRANSACTION)]
    public SszList<BlobVersionedHash>? BlobVersionedHashes { get; init; }

    [SszField(13, SinceVersion = "cancun")]
    public UInt256? MaxFeePerBlobGas { get; init; }
}

[SszContainer(Progressive = true, Version = "future")]
public sealed partial record ExecutionTransaction
{
    [SszField(14, SinceVersion = "future")]
    public Wei? AuthorizationValue { get; init; }

    [SszField(15, SinceVersion = "future")]
    public AccessList? AuthorizationList { get; init; }
}

[SszContainer]
public sealed record Withdrawal
{
    [SszField(0)]
    public ulong Index { get; init; }

    [SszField(1)]
    public ExecutionAddress ValidatorAddress { get; init; }

    [SszField(2)]
    public ExecutionAddress Destination { get; init; }

    [SszField(3)]
    public Wei Amount { get; init; }
}

[SszContainer]
public sealed record BlobProofsV2
{
    [SszField(0)]
    public KzgProof AggregateProof { get; init; }

    [SszField(1)]
    [SszBound(ExecutionConstants.MAX_PROOFS_PER_PAYLOAD)]
    public SszList<KzgProof> IndividualProofs { get; init; } = default!;
}

[SszContainer]
public sealed record BlobsBundleV1
{
    [SszField(0)]
    [SszBound(ExecutionConstants.MAX_BLOB_COMMITMENTS_PER_PAYLOAD)]
    public SszList<Blob> Blobs { get; init; } = default!;

    [SszField(1)]
    [SszBound(ExecutionConstants.MAX_BLOB_COMMITMENTS_PER_PAYLOAD)]
    public SszList<KzgCommitment> Commitments { get; init; } = default!;

    [SszField(2)]
    [SszBound(ExecutionConstants.MAX_BLOB_COMMITMENTS_PER_PAYLOAD)]
    public SszList<KzgProof> Proofs { get; init; } = default!;
}

[SszContainer(Version = "electra")]
public sealed record BlobsAndProofsV2
{
    [SszField(0)]
    public BlobsBundleV1 Bundle { get; init; } = default!;

    [SszField(1)]
    public BlobProofsV2 Proofs2 { get; init; } = default!;
}

[SszContainer]
public sealed record ExecutionPayload
{
    [SszField(0)]
    public Hash32 ParentHash { get; init; }

    [SszField(1)]
    public ExecutionAddress FeeRecipient { get; init; }

    [SszField(2)]
    public Root StateRoot { get; init; }

    [SszField(3)]
    public Hash32 ReceiptsRoot { get; init; }

    [SszField(4)]
    public Hash32 LogsBloom { get; init; }

    [SszField(5)]
    public Hash32 PrevRandao { get; init; }

    [SszField(6)]
    public ulong BlockNumber { get; init; }

    [SszField(7)]
    public GasLimit GasLimit { get; init; }

    [SszField(8)]
    public GasLimit GasUsed { get; init; }

    [SszField(9)]
    public ulong Timestamp { get; init; }

    [SszField(10)]
    public ExtraData ExtraData { get; init; }

    [SszField(11)]
    public UInt256 BaseFeePerGas { get; init; }

    [SszField(12)]
    public Hash32 BlockHash { get; init; }

    [SszField(13)]
    [SszBound(int.MaxValue)]
    public SszList<ExecutionTransaction> Transactions { get; init; } = default!;

    [SszField(14)]
    [SszBound(ExecutionConstants.MAX_WITHDRAWALS_PER_PAYLOAD)]
    public SszList<Withdrawal>? Withdrawals { get; init; }

    [SszField(15, SinceVersion = "deneb")]
    public ulong BlobGasUsed { get; init; }

    [SszField(16, SinceVersion = "deneb")]
    public ulong ExcessBlobGas { get; init; }

    [SszField(17, SinceVersion = "deneb")]
    public Root ParentBeaconBlockRoot { get; init; }

    [SszField(18, SinceVersion = "electra")]
    public Root ExecutionRequestsRoot { get; init; }
}

[SszContainer(Progressive = true, Version = "bellatrix")]
public sealed partial record ExecutionPayloadEnvelope
{
    [SszField(0)]
    public ExecutionPayload Payload { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "capella")]
public sealed partial record ExecutionPayloadEnvelope
{
    [SszField(1, SinceVersion = "capella")]
    [SszBound(ExecutionConstants.MAX_WITHDRAWALS_PER_PAYLOAD)]
    public SszList<Withdrawal> Withdrawals { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "deneb")]
public sealed partial record ExecutionPayloadEnvelope
{
    [SszField(2, SinceVersion = "deneb")]
    public BlobsBundleV1? BlobsBundle { get; init; }
}

[SszContainer(Progressive = true, Version = "electra")]
public sealed partial record ExecutionPayloadEnvelope
{
    [SszField(3, SinceVersion = "electra")]
    public BlobsAndProofsV2? BlobsBundleV2 { get; init; }
}

[SszContainer]
public sealed record PayloadAttributes
{
    [SszField(0)]
    public ulong Timestamp { get; init; }

    [SszField(1)]
    public Hash32 PrevRandao { get; init; }

    [SszField(2)]
    public ExecutionAddress SuggestedFeeRecipient { get; init; }

    [SszField(3)]
    public ExtraData ExtraData { get; init; }

    [SszField(4)]
    [SszBound(ExecutionConstants.MAX_WITHDRAWALS_PER_PAYLOAD)]
    public SszList<Withdrawal>? Withdrawals { get; init; }

    [SszField(5, SinceVersion = "bellatrix")]
    public Root? ParentBeaconBlockRoot { get; init; }

    [SszField(6, SinceVersion = "deneb")]
    public BlobsBundleV1? BlobsBundle { get; init; }

    [SszField(7, SinceVersion = "electra")]
    public BlobsAndProofsV2? BlobsBundleV2 { get; init; }
}

[SszContainer]
public sealed record ForkchoiceState
{
    [SszField(0)]
    public Hash32 HeadBlockHash { get; init; }

    [SszField(1)]
    public Hash32 SafeBlockHash { get; init; }

    [SszField(2)]
    public Hash32 FinalizedBlockHash { get; init; }
}

[SszContainer(Progressive = true, Version = "bellatrix")]
public sealed partial record ForkchoiceUpdatedRequest
{
    [SszField(0)]
    public ForkchoiceState State { get; init; } = default!;

    [SszField(1)]
    public PayloadAttributes? Attributes { get; init; }
}

[SszContainer(Progressive = true, Version = "deneb")]
public sealed partial record ForkchoiceUpdatedRequest
{
    [SszField(2, SinceVersion = "deneb")]
    public BlobsBundleV1? BlobsBundle { get; init; }
}

[SszContainer(Progressive = true, Version = "electra")]
public sealed partial record ForkchoiceUpdatedRequest
{
    [SszField(3, SinceVersion = "electra")]
    public BlobsAndProofsV2? BlobsBundleV2 { get; init; }
}

[SszContainer(Progressive = true, Version = "bellatrix")]
public sealed partial record NewPayloadRequest
{
    [SszField(0)]
    public ExecutionPayloadEnvelope Envelope { get; init; } = default!;
}

[SszContainer(Progressive = true, Version = "deneb")]
public sealed partial record NewPayloadRequest
{
    [SszField(1, SinceVersion = "deneb")]
    public BlobsBundleV1? BlobsBundle { get; init; }
}

[SszContainer(Progressive = true, Version = "electra")]
public sealed partial record NewPayloadRequest
{
    [SszField(2, SinceVersion = "electra")]
    public BlobsAndProofsV2? BlobsBundleV2 { get; init; }
}
