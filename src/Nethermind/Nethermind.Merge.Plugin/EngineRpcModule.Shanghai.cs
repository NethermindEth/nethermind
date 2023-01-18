// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public partial class EngineRpcModule : IEngineRpcModule
    {
        private readonly IAsyncHandler<byte[], GetPayloadV2Result?> _getPayloadHandlerV2;

        public async Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId) =>
            await _getPayloadHandlerV2.HandleAsync(payloadId);

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload executionPayload)
            => NewPayload(executionPayload, 2);

        public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
            => ForkchoiceUpdated(forkchoiceState, payloadAttributes, 2);
    }
}
