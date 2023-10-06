// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
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

public class ExecutionPayloadV3Params : IExecutionPayloadParams
{
    private readonly ExecutionPayloadV3 _executionPayload;
    private readonly byte[]?[] _blobVersionedHashes;

    public ExecutionPayloadV3Params(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes)
    {
        _executionPayload = executionPayload;
        _blobVersionedHashes = blobVersionedHashes;
    }

    public ExecutionPayload ExecutionPayload => _executionPayload;

    public ValidationResult ValidateParams(IReleaseSpec spec, int version, out string? error)
    {
        Transaction[]? transactions = null;
        try
        {
            transactions = _executionPayload.GetTransactions();
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

        if (FlattenHashesFromTransactions(transactions).SequenceEqual(_blobVersionedHashes, Bytes.NullableEqualityComparer))
        {
            error = null;
            return ValidationResult.Success;
        }

        error = "Blob versioned hashes do not match";
        return ValidationResult.Invalid;
    }
}
