// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

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
        static IEnumerable<byte[]?> FlattenHashesFromTransactions(ExecutionPayloadV3 payload) =>
            payload.GetTransactions()
                .Where(t => t.BlobVersionedHashes is not null)
                .SelectMany(t => t.BlobVersionedHashes!);

        if (FlattenHashesFromTransactions(_executionPayload).SequenceEqual(_blobVersionedHashes, Bytes.NullableEqualityComparer))
        {
            error = null;
            return ValidationResult.Success;
        }

        error = "Blob versioned hashes do not match";
        return ValidationResult.Invalid;
    }
}
