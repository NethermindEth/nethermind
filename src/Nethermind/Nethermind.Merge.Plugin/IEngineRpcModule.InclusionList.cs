// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
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
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV4(ExecutionPayloadV4 executionPayload, byte[][]? inclusionList);

    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload and fees with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV4(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Validates the inclusion list and returns the status.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<InclusionListStatusV1>> engine_newInclusionListV1(InclusionListSummaryV1 inclusionListSummary, Transaction[] inclusionListTransactions);

    [JsonRpcMethod(
        Description = "Returns the inclusion list for the given block hash.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<GetInclusionListResultV1>> engine_getInclusionListV1();
}
