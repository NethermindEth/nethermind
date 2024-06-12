// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV4(ExecutionPayloadV4 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot);

    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload and fees with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<GetPayloadV4Result?>> engine_getPayloadV4(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Returns an array of execution payload bodies for the list of provided block hashes.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(IList<Hash256> blockHashes);

    [JsonRpcMethod(
        Description = "Returns an array of execution payload bodies for the provided number range",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(long start, long count);
}
