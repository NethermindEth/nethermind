// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;

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
    byte[]?[] blobVersionedHashes,
    Hash256? parentBeaconBlockRoot,
    byte[][]? executionRequests = null,
    ILogger? logger = null)
    : ExecutionPayloadParams(executionRequests), IExecutionPayloadParams where TVersionedExecutionPayload : ExecutionPayload
{
    private readonly ILogger? _logger = logger;
    public TVersionedExecutionPayload ExecutionPayload => executionPayload;

    ExecutionPayload IExecutionPayloadParams.ExecutionPayload => ExecutionPayload;

    public ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
        ValidationResult result = ValidateInitialParams(spec, out error);
        if (result != ValidationResult.Success)
        {
            return result;
        }

        TransactionDecodingResult transactionDecodingResult = executionPayload.TryGetTransactions();
        if (transactionDecodingResult.Error is not null)
        {
            error = transactionDecodingResult.Error;
            return ValidationResult.Invalid;
        }

        if (!FlattenedHashesEqual(transactionDecodingResult.Transactions, blobVersionedHashes))
        {
            error = "Blob versioned hashes do not match";
            return ValidationResult.Invalid;
        }

        // Use parentBeaconBlockRoot parameter if provided, otherwise fall back to executionPayload.ParentBeaconBlockRoot
        // This handles cases where op-node sends valid requests with parentBeaconBlockRoot in the payload itself

        // Log when executionPayload has a non-null ParentBeaconBlockRoot
        if (executionPayload.ParentBeaconBlockRoot is not null)
        {
            if (_logger is ILogger logger && logger.IsInfo)
            {
                logger.Info($"Execution payload has non-null ParentBeaconBlockRoot: {executionPayload.ParentBeaconBlockRoot}");
            }

            // Log warning when executionPayload.ParentBeaconBlockRoot doesn't match the input parentBeaconBlockRoot
            if (parentBeaconBlockRoot is not null && executionPayload.ParentBeaconBlockRoot != parentBeaconBlockRoot)
            {
                if (_logger is ILogger loggerWarn && loggerWarn.IsWarn)
                {
                    loggerWarn.Warn($"Execution payload ParentBeaconBlockRoot ({executionPayload.ParentBeaconBlockRoot}) does not match input parentBeaconBlockRoot ({parentBeaconBlockRoot})");
                }
            }
        }

        Hash256? finalParentBeaconBlockRoot = parentBeaconBlockRoot ?? executionPayload.ParentBeaconBlockRoot;

        // Log error when finalParentBeaconBlockRoot is null
        if (finalParentBeaconBlockRoot is null)
        {
            if (_logger is ILogger loggerError && loggerError.IsError)
            {
                loggerError.Error("finalParentBeaconBlockRoot is null");
            }
            error = "Parent beacon block root must be set";
            return ValidationResult.Fail;
        }

        executionPayload.ParentBeaconBlockRoot = finalParentBeaconBlockRoot;

        error = null;
        return ValidationResult.Success;
    }

    private static bool FlattenedHashesEqual(Transaction[] transactions, ReadOnlySpan<byte[]?> expected)
    {
        int expectedIndex = 0;
        for (int txIndex = 0; txIndex < transactions.Length; txIndex++)
        {
            byte[]?[]? hashes = transactions[txIndex].BlobVersionedHashes;
            if (hashes is null || hashes.Length == 0) continue;

            for (int hashIndex = 0; hashIndex < hashes.Length; hashIndex++)
            {
                if (expectedIndex >= expected.Length) return false;
                if (!hashes[hashIndex].AsSpan().SequenceEqual(expected[expectedIndex]))
                {
                    return false;
                }
                expectedIndex++;
            }
        }

        return expectedIndex == expected.Length;
    }
}
