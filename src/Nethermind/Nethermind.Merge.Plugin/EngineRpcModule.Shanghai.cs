// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV2Result?> _getPayloadHandlerV2;

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => ForkchoiceUpdated(forkchoiceState, payloadAttributes, 2);

    public async Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId) =>
        await _getPayloadHandlerV2.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload executionPayload)
        => NewPayload(executionPayload, 2);
}
