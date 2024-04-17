// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV4Result?> _getPayloadHandlerV4;

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Prague);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV4(ExecutionPayloadV4 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot) =>
        NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot), EngineApiVersions.Prague);

    public async Task<ResultWrapper<GetPayloadV4Result?>> engine_getPayloadV4(byte[] payloadId) =>
        await _getPayloadHandlerV4.HandleAsync(payloadId);
}
