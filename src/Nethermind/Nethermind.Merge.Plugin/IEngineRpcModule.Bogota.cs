// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
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
    Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1(Hash256? parentBlockHash = null);

    [JsonRpcMethod(
        Description = "Builds an inclusion list from the local mempool, with its EIP-8288 recursiveStark.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<GetInclusionListV2Result>> engine_getInclusionListV2(Hash256? parentBlockHash = null);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status (including inclusion-list compliance) and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status, hash of the last valid block, and the execution witness when the payload is valid.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions);

    [JsonRpcMethod(
        Description = "Verifies the payload against each counted inclusion list, whose obligations follow from its EIP-8288 recursiveStark, and returns the verification status (including inclusion-list compliance) and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV7(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, InclusionListV1[]? inclusionLists);

    [JsonRpcMethod(
        Description = "Verifies the payload against each counted inclusion list, whose obligations follow from its EIP-8288 recursiveStark, and returns the verification status, hash of the last valid block, and the execution witness when the payload is valid.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV7(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, InclusionListV1[]? inclusionLists);

    [JsonRpcMethod(
        Description = "Applies fork choice and starts building a new block if payload attributes are present.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV2Result>> engine_forkchoiceUpdatedV6(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        [JsonRpcParameter(ConverterType = typeof(BlobCellBitArrayConverter))] BitArray? custodyColumns = null);

    [JsonRpcMethod(
        Description = "Applies fork choice and starts building a new block if payload attributes are present.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV2Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        [JsonRpcParameter(ConverterType = typeof(BlobCellBitArrayConverter))] BitArray? custodyColumns = null);
}
