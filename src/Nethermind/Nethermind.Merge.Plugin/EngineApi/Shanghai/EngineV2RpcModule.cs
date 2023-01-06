// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;
using Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai
{
    public class EngineV2RpcModule : IEngineV2RpcModule
    {
        private readonly IAsyncHandler<byte[], GetPayloadV2Result?> _getPayloadV2Handler;
        private readonly IAsyncHandler<ExecutionPayloadV2, PayloadStatusV1> _newPayloadV2Handler;
        private readonly IAsyncHandler<ForkchoiceUpdatedV2, ForkchoiceUpdatedV1Result> _forkchoiceUpdatedV2Handler;
        private readonly Locker _locker;
        private readonly ILogger _logger;

        public EngineV2RpcModule(
            IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadV2Handler,
            IAsyncHandler<ExecutionPayloadV2, PayloadStatusV1> newPayloadV2Handler,
            IAsyncHandler<ForkchoiceUpdatedV2, ForkchoiceUpdatedV1Result> forkchoiceUpdatedV2Handler,
            Locker locker,
            ILogManager logManager)
        {
            _getPayloadV2Handler = getPayloadV2Handler;
            _newPayloadV2Handler = newPayloadV2Handler;
            _forkchoiceUpdatedV2Handler = forkchoiceUpdatedV2Handler;
            _locker = locker;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId) =>
            await _getPayloadV2Handler.HandleAsync(payloadId);

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayloadV2 executionPayload) =>
            ModuleHelper.RunHandler(
                _newPayloadV2Handler,
                executionPayload,
                static m => Metrics.NewPayloadExecutionTime = m,
                _locker,
                _logger);

        public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, PayloadAttributesV2? payloadAttributes = null) =>
            ModuleHelper.RunHandler(
                _forkchoiceUpdatedV2Handler,
                new ForkchoiceUpdatedV2(forkchoiceState, payloadAttributes),
        static m => Metrics.NewPayloadExecutionTime = m,
                _locker,
                _logger);
    }
}
