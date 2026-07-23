// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Builds an inclusion list from the local mempool.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1();

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status (including inclusion-list compliance) and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions);

    [JsonRpcMethod(
        Description = "Applies fork choice and starts building a new block if payload attributes are present.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV5(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null, byte[]? custodyColumns = null);
}
