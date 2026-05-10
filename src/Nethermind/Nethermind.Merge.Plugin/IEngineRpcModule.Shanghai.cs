// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Applies fork choice and starts building a new block if payload attributes are present.",
        IsSharable = true,
        IsImplemented = true)]
    [SszRestMethod("POST", EngineApiVersions.Fcu.V2, SszRestPaths.Forkchoice, SszRestRequest.ForkchoiceUpdatedV2, SszRestResponse.ForkchoiceUpdated)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload and fees with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    [SszRestMethod("GET", EngineApiVersions.GetPayload.V2, SszRestPaths.Payloads, SszRestRequest.PayloadId, SszRestResponse.GetPayloadV2, acceptsPathExtra: true, extraPathName: "payload_id", noStore: true)]
    Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Returns an array of execution payload bodies for the list of provided block hashes.",
        IsSharable = true,
        IsImplemented = true)]
    [SszRestMethod("POST", EngineApiVersions.PayloadBodiesByHash.V1, SszRestPaths.PayloadBodiesByHash, SszRestRequest.PayloadBodiesByHash, SszRestResponse.PayloadBodiesV1)]
    ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>> engine_getPayloadBodiesByHashV1(IReadOnlyList<Hash256> blockHashes);

    [JsonRpcMethod(
        Description = "Returns an array of execution payload bodies for the provided number range",
        IsSharable = true,
        IsImplemented = true)]
    [SszRestMethod("POST", EngineApiVersions.PayloadBodiesByRange.V1, SszRestPaths.PayloadBodiesByRange, SszRestRequest.PayloadBodiesByRange, SszRestResponse.PayloadBodiesV1)]
    Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> engine_getPayloadBodiesByRangeV1(long start, long count);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    [SszRestMethod("POST", EngineApiVersions.NewPayload.V2, SszRestPaths.Payloads, SszRestRequest.NewPayloadV2, SszRestResponse.PayloadStatus)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload executionPayload);
}
