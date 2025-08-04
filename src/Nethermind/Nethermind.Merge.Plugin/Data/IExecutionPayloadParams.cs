// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

public interface IExecutionPayloadParams
{
    ExecutionPayload ExecutionPayload { get; }
    byte[][]? ExecutionRequests { get; set; }
    byte[][]? InclusionListTransactions { get; set; }
    ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error);
}

public enum ValidationResult : byte { Success, Fail, Invalid };

public class ExecutionPayloadParams(
    byte[][]? executionRequests = null,
    byte[][]? inclusionListTransactions = null)
{
    /// <summary>
    /// Gets or sets <see cref="ExecutionRequets"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7685">EIP-7685</see>.
    /// </summary>
    public byte[][]? ExecutionRequests { get; set; } = executionRequests;

    /// <summary>
    /// Gets or sets <see cref="InclusionListTransactions"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    public byte[][]? InclusionListTransactions { get; set; } = inclusionListTransactions;

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

        if (spec.InclusionListsEnabled)
        {
            if (InclusionListTransactions is null)
            {
                error = "Inclusion list must be set";
                return ValidationResult.Fail;
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
    byte[][]? inclusionListTransactions = null)
    : ExecutionPayloadParams(executionRequests, inclusionListTransactions), IExecutionPayloadParams where TVersionedExecutionPayload : ExecutionPayload
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

        TransactionDecodingResult transactionDecodingResult = executionPayload.TryGetTransactions();
        if (transactionDecodingResult.Error is not null)
        {
            error = transactionDecodingResult.Error;
            return ValidationResult.Invalid;

        }

        static IEnumerable<byte[]?> FlattenHashesFromTransactions(Transaction[] transactions) =>
            transactions
                .Where(t => t.BlobVersionedHashes is not null)
                .SelectMany(t => t.BlobVersionedHashes!);

        if (!FlattenHashesFromTransactions(transactionDecodingResult.Transactions).SequenceEqual(blobVersionedHashes, Bytes.NullableEqualityComparer))
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
