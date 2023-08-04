// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<IList<Keccak>, IEnumerable<ExecutionPayloadBodyV1Result?>> _executionGetPayloadBodiesByHashV1Handler;
    private readonly IGetPayloadBodiesByRangeV1Handler _executionGetPayloadBodiesByRangeV1Handler;
    private readonly IAsyncHandler<byte[], GetPayloadV2Result?> _getPayloadHandlerV2;
    public const int ShanghaiVersion = 2;

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => ForkchoiceUpdated(forkchoiceState, payloadAttributes, ShanghaiVersion);

    public Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId)
        => _getPayloadHandlerV2.HandleAsync(payloadId);

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> engine_getPayloadBodiesByHashV1(IList<Keccak> blockHashes)
        => _executionGetPayloadBodiesByHashV1Handler.HandleAsync(blockHashes);

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> engine_getPayloadBodiesByRangeV1(long start, long count)
        => _executionGetPayloadBodiesByRangeV1Handler.Handle(start, count);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload executionPayload)
        => NewPayload(executionPayload, ShanghaiVersion);
}
