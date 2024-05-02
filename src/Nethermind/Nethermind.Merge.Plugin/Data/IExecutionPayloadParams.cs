// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error);
}

public enum ValidationResult : byte { Success, Fail, Invalid };

public class ExecutionPayloadParams<TVersionedExecutionPayload>(
    TVersionedExecutionPayload executionPayload,
    byte[]?[] blobVersionedHashes,
    Hash256? parentBeaconBlockRoot)
    : IExecutionPayloadParams where TVersionedExecutionPayload : ExecutionPayload
{
    public TVersionedExecutionPayload ExecutionPayload => executionPayload;

    ExecutionPayload IExecutionPayloadParams.ExecutionPayload => ExecutionPayload;

    public ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
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
