// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV3Result?> _getPayloadHandlerV3 = getPayloadHandlerV3;
    private readonly IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>> _getBlobsHandler = getBlobsHandler;

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => ForkchoiceUpdatedV3(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V3);

    /// <summary>Rejects a forkchoice state with a null or missing hash (cancun.md <c>engine_forkchoiceUpdatedV3</c> (1),
    /// carried into later versions), then runs <see cref="ForkchoiceUpdated"/>.</summary>
    protected Task<ResultWrapper<ForkchoiceUpdatedV1Result>> ForkchoiceUpdatedV3(
        ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, int version)
        => forkchoiceState is { HeadBlockHash: not null, SafeBlockHash: not null, FinalizedBlockHash: not null }
            ? ForkchoiceUpdated(forkchoiceState, payloadAttributes, version)
            : Task.FromResult(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("Forkchoice state must set headBlockHash, safeBlockHash and finalizedBlockHash", ErrorCodes.InvalidParams));

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(ExecutionPayloadV3 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot) =>
        NewPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot), EngineApiVersions.NewPayload.V3);

    public Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId) =>
        _getPayloadHandlerV3.HandleAsync(payloadId);

    public Task<ResultWrapper<IReadOnlyList<BlobAndProofV1?>>> engine_getBlobsV1(byte[][] blobVersionedHashes) =>
        _getBlobsHandler.HandleAsync(blobVersionedHashes);
}
