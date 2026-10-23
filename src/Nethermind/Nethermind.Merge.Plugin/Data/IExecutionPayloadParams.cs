// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Merge.Plugin.Data;

public interface IExecutionPayloadParams
{
    ExecutionPayload ExecutionPayload { get; }
    byte[][]? ExecutionRequests { get; set; }
    ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error);

    /// <summary>Validates request parameters and bounds the transaction count before decoding when supported.</summary>
    /// <remarks>The default preserves validation behavior for existing plugin implementations.</remarks>
    /// <param name="latestValidHash">The latest valid ancestor known when the guard rejects the payload; otherwise <c>null</c>.</param>
    internal ValidationResult ValidateParams(IReleaseSpec spec, int version, PayloadTransactionCountGuard? transactionCountGuard, out string? error, out Hash256? latestValidHash)
    {
        latestValidHash = null;
        ValidationResult result = ValidateParams(spec, version, out error);
        return result == ValidationResult.Success && transactionCountGuard is not null &&
            !transactionCountGuard.IsWithinParentGasLimit(ExecutionPayload, spec, out error, out latestValidHash)
            ? ValidationResult.Invalid
            : result;
    }
}

public enum ValidationResult : byte { Success, Fail, Invalid };

public class ExecutionPayloadParams(
    byte[][]? executionRequests = null,
    byte[][][]? inclusionLists = null,
    OmissionClaim[]? omissionClaims = null)
{
    /// <summary>
    /// Gets or sets <see cref="ExecutionRequests"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7685">EIP-7685</see>.
    /// </summary>
    public byte[][]? ExecutionRequests { get; set; } = executionRequests;

    /// <summary>EIP-7805 separate inclusion lists, one per committee position (<see cref="Core.InclusionLists"/>).</summary>
    public byte[][][]? InclusionLists { get; } = inclusionLists;

    /// <summary>The EIP-7805 omission claims the builder committed to, in order and with their duplicates.</summary>
    public OmissionClaim[]? OmissionClaims { get; } = omissionClaims;

    protected ValidationResult ValidateInitialParams(IReleaseSpec spec, out string? error)
    {
        error = null;
        if (spec.RequestsEnabled)
        {
            if (ExecutionRequests is null)
            {
                error = "Execution requests must be set";
                return ValidationResult.Fail;
            }

            // verification of the requests
            byte[][]? requests = ExecutionRequests;
            int previousTypeId = -1;
            for (int i = 0; i < requests.Length; i++)
            {
                byte[]? request = requests[i];
                if (request is null || request.Length <= 1)
                {
                    error = "Execution request data must be longer than 1 byte";
                    return ValidationResult.Fail;
                }

                int requestTypeId = request[0];
                if (requestTypeId <= previousTypeId)
                {
                    error = "Execution requests must not contain duplicates and be ordered by request_type in ascending order";
                    return ValidationResult.Fail;
                }

                previousTypeId = requestTypeId;
            }
        }

        // bogota.md engine_newPayloadV6 point 2. Transaction contents and claims that match nothing are judged by
        // the inclusion check, never here.
        if (spec.InclusionListsEnabled)
        {
            error = Core.InclusionLists.Validate(InclusionLists);
            if (error is not null) return ValidationResult.Fail;

            if (OmissionClaims is null)
            {
                error = "Omission claims must be set";
                return ValidationResult.Fail;
            }

            if (OmissionClaims.Length > Eip8369Constants.MaxOmissionClaims)
            {
                error = $"Omission claims exceed the maximum of {Eip8369Constants.MaxOmissionClaims}";
                return ValidationResult.Fail;
            }

            if (Array.Exists(OmissionClaims, static claim => claim.TransactionHash is null))
            {
                error = "Omission claim transaction hash must be set";
                return ValidationResult.Fail;
            }
        }

        return ValidationResult.Success;
    }
}

public class ExecutionPayloadParams<TVersionedExecutionPayload>(
    TVersionedExecutionPayload executionPayload,
    Hash256?[]? blobVersionedHashes,
    Hash256? parentBeaconBlockRoot,
    byte[][]? executionRequests = null,
    byte[][][]? inclusionLists = null,
    OmissionClaim[]? omissionClaims = null)
    : ExecutionPayloadParams(executionRequests, inclusionLists, omissionClaims), IExecutionPayloadParams where TVersionedExecutionPayload : ExecutionPayload
{
    public TVersionedExecutionPayload ExecutionPayload => executionPayload;

    ExecutionPayload IExecutionPayloadParams.ExecutionPayload => ExecutionPayload;

    public Hash256?[]? BlobVersionedHashes => blobVersionedHashes;

    public Hash256? ParentBeaconBlockRoot => parentBeaconBlockRoot;

    public ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error) =>
        ((IExecutionPayloadParams)this).ValidateParams(spec, version, null, out error, out _);

    /// <inheritdoc/>
    ValidationResult IExecutionPayloadParams.ValidateParams(IReleaseSpec spec, int version, PayloadTransactionCountGuard? transactionCountGuard, out string? error, out Hash256? latestValidHash)
    {
        latestValidHash = null;
        ValidationResult result = ValidateInitialParams(spec, out error);
        if (result != ValidationResult.Success)
        {
            return result;
        }

        result = ValidateEngineApiVersionParams(spec, version, out error);
        if (result != ValidationResult.Success)
        {
            return result;
        }

        if (parentBeaconBlockRoot is null)
        {
            error = "Parent beacon block root must be set";
            return ValidationResult.Fail;
        }

        if (transactionCountGuard is not null && !transactionCountGuard.IsWithinParentGasLimit(executionPayload, spec, out error, out latestValidHash))
        {
            return ValidationResult.Invalid;
        }

        bool isEmptyPreForkV4 = version == EngineApiVersions.NewPayload.V4 &&
            !spec.BlockLevelAccessListsEnabled &&
            executionPayload.BlockAccessList is { Length: 0 };
        if (executionPayload.BlockAccessList is { } encodedBlockAccessList)
        {
            // A pre-fork V4 payload can use empty bytes while its header still carries the
            // forbidden BAL hash. Block reconstruction must classify that as INVALID.
            if (!isEmptyPreForkV4)
            {
                if (!ExecutionPayloadV4.HasCompleteRlpListEnvelope(encodedBlockAccessList))
                {
                    error = "Error decoding block access list: Must be a complete RLP list";
                    return ValidationResult.Invalid;
                }

                bool decoded = executionPayload is ExecutionPayloadV4 executionPayloadV4
                    ? executionPayloadV4.TryDecodeBlockAccessList(out _, out error)
                    : ExecutionPayloadV4.TryDecodeBlockAccessList(encodedBlockAccessList, out _, out error);
                if (!decoded)
                {
                    return ValidationResult.Invalid;
                }
            }
        }

        Result<Transaction[]> transactionDecodingResult = executionPayload.TryGetTransactions();
        if (transactionDecodingResult.IsError)
        {
            error = transactionDecodingResult.Error;
            return ValidationResult.Invalid;
        }

        if (!FlattenedHashesEqual(transactionDecodingResult.Data, blobVersionedHashes))
        {
            error = "Blob versioned hashes do not match";
            return ValidationResult.Invalid;
        }

        executionPayload.ParentBeaconBlockRoot = parentBeaconBlockRoot;

        if (isEmptyPreForkV4)
        {
            Result<Block> reconstructedBlock = executionPayload.TryGetBlock();
            if (reconstructedBlock.IsError)
            {
                error = reconstructedBlock.Error;
                return ValidationResult.Invalid;
            }

            if (reconstructedBlock.Data.CalculateHash() == executionPayload.BlockHash)
            {
                error = "Block access list must not be set before engine_newPayloadV5";
                return ValidationResult.Fail;
            }
        }

        error = null;
        return ValidationResult.Success;
    }

    private ValidationResult ValidateEngineApiVersionParams(IReleaseSpec spec, int version, out string? error)
    {
        if (spec.WithdrawalsEnabled && executionPayload.Withdrawals is null)
        {
            error = "Withdrawals must be set";
            return ValidationResult.Fail;
        }

        if (spec.IsEip4844Enabled && executionPayload.BlobGasUsed is null)
        {
            error = "Blob gas used must be set";
            return ValidationResult.Fail;
        }

        if (spec.IsEip4844Enabled && executionPayload.ExcessBlobGas is null)
        {
            error = "Excess blob gas must be set";
            return ValidationResult.Fail;
        }

        if (version < EngineApiVersions.NewPayload.V4 && executionPayload.BlockAccessList is not null)
        {
            error = "Block access list must not be set before engine_newPayloadV4";
            return ValidationResult.Fail;
        }

        if (version == EngineApiVersions.NewPayload.V4 &&
            executionPayload.BlockAccessList is { Length: > 0 })
        {
            error = "Block access list must not be set before engine_newPayloadV5";
            return ValidationResult.Fail;
        }

        if (version < EngineApiVersions.NewPayload.V5)
        {
            if (executionPayload.SlotNumber is not null)
            {
                error = "Slot number must not be set before engine_newPayloadV5";
                return ValidationResult.Fail;
            }
        }

        if (spec.BlockLevelAccessListsEnabled && executionPayload.BlockAccessList is null)
        {
            error = "Block access list must be set";
            return ValidationResult.Fail;
        }

        if (spec.IsEip7843Enabled && executionPayload.SlotNumber is null)
        {
            error = "Slot number must be set";
            return ValidationResult.Fail;
        }

        // Runs after the fork checks above, which also guard payloads not bound from JSON and keep their messages.
        if (executionPayload.HasUnboundField)
        {
            error = executionPayload.UnboundFieldError;
            return ValidationResult.Fail;
        }

        if (spec.IsEip4844Enabled && blobVersionedHashes is null)
        {
            error = "Blob versioned hashes must not be null";
            return ValidationResult.Fail;
        }

        error = null;
        return ValidationResult.Success;
    }

    private static bool FlattenedHashesEqual(Transaction[] transactions, ReadOnlySpan<Hash256?> expected)
    {
        int expectedIndex = 0;
        for (int txIndex = 0; txIndex < transactions.Length; txIndex++)
        {
            byte[]?[]? hashes = transactions[txIndex].BlobVersionedHashes;
            if (hashes is null || hashes.Length == 0) continue;

            for (int hashIndex = 0; hashIndex < hashes.Length; hashIndex++)
            {
                if (expectedIndex >= expected.Length) return false;
                ReadOnlySpan<byte> expectedBytes = expected[expectedIndex] is { } expectedHash ? expectedHash.Bytes : default;
                if (!hashes[hashIndex].AsSpan().SequenceEqual(expectedBytes))
                {
                    return false;
                }
                expectedIndex++;
            }
        }

        return expectedIndex == expected.Length;
    }
}

/// <summary>An EIP-7805 newPayload request, distinguished by type because <see cref="ExecutionPayloadV4"/>
/// spans two forks and shared handlers have nothing else to tell them apart by.</summary>
public sealed class InclusionListExecutionPayloadParams(
    ExecutionPayloadV4 executionPayload,
    Hash256?[]? blobVersionedHashes,
    Hash256? parentBeaconBlockRoot,
    byte[][]? executionRequests,
    byte[][][]? inclusionLists,
    OmissionClaim[]? omissionClaims)
    : ExecutionPayloadParams<ExecutionPayloadV4>(
        executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionLists, omissionClaims);
