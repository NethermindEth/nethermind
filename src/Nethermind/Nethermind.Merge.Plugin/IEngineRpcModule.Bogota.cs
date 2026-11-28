// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
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
        Description = "Returns the most recent version of an execution payload, with the build's EIP-7805 omission claims.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<GetPayloadV7Result?>> engine_getPayloadV7(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns its execution status, scheduling the EIP-7805 inclusion check of the separate inclusion lists under the builder's omission claims and returning its identifier.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][][]? inclusionLists, OmissionClaim[]? omissionClaims);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status, hash of the last valid block, and the execution witness when the payload is valid.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][][]? inclusionLists, OmissionClaim[]? omissionClaims);

    [JsonRpcMethod(
        Description = "Returns the current verdict of an inclusion check engine_newPayloadV6 scheduled, or null while it is pending.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<bool?>> engine_getInclusionListVerdictV1(Hash256 inclusionCheckId);

    [JsonRpcMethod(
        Description = "Applies fork choice and starts building a new block if payload attributes are present.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        [JsonRpcParameter(ConverterType = typeof(BlobCellBitArrayConverter))] BitArray? custodyColumns = null);
}
