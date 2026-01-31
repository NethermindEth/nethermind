// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload and fees with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV4 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests);

    [JsonRpcMethod(
        Description = "Returns an array of block access lists for the list of provided block hashes.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<IEnumerable<byte[]?>>> engine_getBALSByHashV1(Hash256[] blockHashes);

    [JsonRpcMethod(
        Description = "Returns an array of block access lists for the provided number range.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<IEnumerable<byte[]>?>> engine_getBALSByRangeV1(long start, long count);
}
