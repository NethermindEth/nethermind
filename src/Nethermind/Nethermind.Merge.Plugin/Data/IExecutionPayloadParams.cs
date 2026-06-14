// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Data;

public interface IExecutionPayloadParams
{
    ExecutionPayload ExecutionPayload { get; }
    byte[][]? ExecutionRequests { get; set; }
    ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error);
}

public enum ValidationResult : byte { Success, Fail, Invalid };

public class ExecutionPayloadParams(byte[][]? executionRequests = null)
{
    /// <summary>
    /// Gets or sets <see cref="ExecutionRequests"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7685">EIP-7685</see>.
    /// </summary>
    public byte[][]? ExecutionRequests { get; set; } = executionRequests;

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

        return ValidationResult.Success;
    }
}

public class ExecutionPayloadParams<TVersionedExecutionPayload>(
    TVersionedExecutionPayload executionPayload,
    Hash256?[] blobVersionedHashes,
    Hash256? parentBeaconBlockRoot,
    byte[][]? executionRequests = null)
    : ExecutionPayloadParams(executionRequests), IExecutionPayloadParams where TVersionedExecutionPayload : ExecutionPayload
{
    public TVersionedExecutionPayload ExecutionPayload => executionPayload;

    ExecutionPayload IExecutionPayloadParams.ExecutionPayload => ExecutionPayload;

    public ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
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

        if (parentBeaconBlockRoot is null)
        {
            error = "Parent beacon block root must be set";
            return ValidationResult.Fail;
        }

        executionPayload.ParentBeaconBlockRoot = parentBeaconBlockRoot;

        error = null;
        return ValidationResult.Success;
    }

    private ValidationResult ValidateEngineApiVersionParams(IReleaseSpec spec, int version, out string? error)
    {
        if (version < EngineApiVersions.NewPayload.V5)
        {
            if (executionPayload.BlockAccessList is not null)
            {
                error = "Block access list must not be set before engine_newPayloadV5";
                return ValidationResult.Fail;
            }

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
