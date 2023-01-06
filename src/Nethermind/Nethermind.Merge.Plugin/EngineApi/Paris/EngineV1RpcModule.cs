// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;
using Nethermind.Merge.Plugin.EngineApi.Paris.Handlers;

namespace Nethermind.Merge.Plugin.EngineApi.Paris
{
    public class EngineV1RpcModule : IEngineV1RpcModule
    {
        private readonly IAsyncHandler<byte[], ExecutionPayloadV1?> _getPayloadHandlerV1;
        private readonly IAsyncHandler<ExecutionPayloadV1, PayloadStatusV1> _newPayloadV1Handler;
        private readonly IAsyncHandler<ForkchoiceUpdatedV1, ForkchoiceUpdatedV1Result> _forkchoiceUpdatedV1Handler;
        private readonly IHandler<ExecutionStatusResult> _executionStatusHandler;
        private readonly IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]> _executionGetPayloadBodiesByHashV1Handler;
        private readonly IGetPayloadBodiesByRangeV1Handler _executionGetPayloadBodiesByRangeV1Handler;
        private readonly IHandler<TransitionConfigurationV1, TransitionConfigurationV1> _transitionConfigurationV1Handler;
        private readonly Locker _locker;
        private readonly ILogger _logger;

        public EngineV1RpcModule(
            IAsyncHandler<byte[], ExecutionPayloadV1?> getPayloadHandlerV1,
            IAsyncHandler<ExecutionPayloadV1, PayloadStatusV1> newPayloadV1Handler,
            IAsyncHandler<ForkchoiceUpdatedV1, ForkchoiceUpdatedV1Result> forkchoiceUpdatedV1Handler,
            IHandler<ExecutionStatusResult> executionStatusHandler,
            IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]> executionGetPayloadBodiesByHashV1Handler,
            IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
            IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationV1Handler,
            Locker locker,
            ILogManager logManager)
        {
            _getPayloadHandlerV1 = getPayloadHandlerV1;
            _newPayloadV1Handler = newPayloadV1Handler;
            _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
            _executionStatusHandler = executionStatusHandler;
            _executionGetPayloadBodiesByHashV1Handler = executionGetPayloadBodiesByHashV1Handler;
            _executionGetPayloadBodiesByRangeV1Handler = executionGetPayloadBodiesByRangeV1Handler;
            _transitionConfigurationV1Handler = transitionConfigurationV1Handler;
            _locker = locker;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<ExecutionStatusResult> engine_executionStatus() => _executionStatusHandler.Handle();

        public Task<ResultWrapper<ExecutionPayloadV1?>> engine_getPayloadV1(byte[] payloadId) =>
            _getPayloadHandlerV1.HandleAsync(payloadId);

        public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(
            TransitionConfigurationV1 transitionConfiguration) => _transitionConfigurationV1Handler.Handle(transitionConfiguration);

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayloadV1 executionPayload) =>
            ModuleHelper.RunHandler(
                _newPayloadV1Handler,
                executionPayload,
                static m => Metrics.NewPayloadExecutionTime = m,
                _locker,
                _logger);

        public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributesV1? payloadAttributes = null) =>
            ModuleHelper.RunHandler(
                _forkchoiceUpdatedV1Handler,
                new ForkchoiceUpdatedV1(forkchoiceState, payloadAttributes),
                static m => Metrics.NewPayloadExecutionTime = m,
                _locker,
                _logger);

        public Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByHashV1(Keccak[] blockHashes) =>
            _executionGetPayloadBodiesByHashV1Handler.HandleAsync(blockHashes);

        public Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByRangeV1(long start, long count) =>
            _executionGetPayloadBodiesByRangeV1Handler.Handle(start, count);
    }
}
