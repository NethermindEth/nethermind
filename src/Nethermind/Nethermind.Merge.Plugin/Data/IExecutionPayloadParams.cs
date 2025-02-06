// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

public interface IExecutionPayloadParams
{
    ExecutionPayload ExecutionPayload { get; }
    byte[][]? ExecutionRequests { get; set; }
    ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error);
}

public enum ValidationResult : byte { Success, Fail, Invalid };

public class ExecutionPayloadParams<TVersionedExecutionPayload>(
    TVersionedExecutionPayload executionPayload,
    byte[]?[] blobVersionedHashes,
    Hash256? parentBeaconBlockRoot,
    byte[][]? executionRequests = null)
    : IExecutionPayloadParams where TVersionedExecutionPayload : ExecutionPayload
{
    public TVersionedExecutionPayload ExecutionPayload => executionPayload;

    /// <summary>
    /// Gets or sets <see cref="ExecutionRequets"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7685">EIP-7685</see>.
    /// </summary>
    public byte[][]? ExecutionRequests { get; set; } = executionRequests;

    ExecutionPayload IExecutionPayloadParams.ExecutionPayload => ExecutionPayload;

    public ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
        if (spec.RequestsEnabled)
        {
            if (ExecutionRequests is null)
            {
                error = "Execution requests must be set";
                return ValidationResult.Fail;
            }

            if (ExecutionRequests.Length > ExecutionRequestExtensions.MaxRequestsCount)
            {
                error = $"Execution requests must have less than {ExecutionRequestExtensions.MaxRequestsCount} items";
                return ValidationResult.Fail;
            }

            // verification of the requests
            for (int i = 0; i < ExecutionRequests.Length; i++)
            {
                if (ExecutionRequests[i] == null || ExecutionRequests[i].Length <= 1)
                {
                    error = "Execution request data must be longer than 1 byte";
                    return ValidationResult.Fail;
                }

                if (i > 0 && ExecutionRequests[i][0] <= ExecutionRequests[i - 1][0])
                {
                    error = "Execution requests must not contain duplicates and be ordered by request_type in ascending order";
                    return ValidationResult.Fail;
                }

                if (!Enum.IsDefined(typeof(ExecutionRequestType), ExecutionRequests[i][0]))
                {
                    error = $"Invalid execution request type: {ExecutionRequests[i][0]}";
                    return ValidationResult.Fail;
                }
            }

        }
        Transaction[]? transactions;
        try
        {
            transactions = executionPayload.GetTransactions();
        }
        catch (RlpException rlpException)
        {
            error = rlpException.Message;
            return ValidationResult.Invalid;
        }

        static IEnumerable<byte[]?> FlattenHashesFromTransactions(Transaction[] transactions) =>
            transactions
                .Where(t => t.BlobVersionedHashes is not null)
                .SelectMany(t => t.BlobVersionedHashes!);

        if (!FlattenHashesFromTransactions(transactions).SequenceEqual(blobVersionedHashes, Bytes.NullableEqualityComparer))
        {
            error = "Blob versioned hashes do not match";
            return ValidationResult.Invalid;
        }

        if (parentBeaconBlockRoot is null)
        {
            error = "Parent beacon block root must be set";
            return ValidationResult.Fail;
        }

        executionPayload.ParentBeaconBlockRoot = new Hash256(parentBeaconBlockRoot);

        error = null;
        return ValidationResult.Success;
    }
}
