// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public class EngineRpcModule : IEngineRpcModule
    {
        private readonly IAsyncHandler<byte[], ExecutionPayload?> _getPayloadHandlerV1;
        private readonly IAsyncHandler<byte[], GetPayloadV2Result?> _getPayloadHandlerV2;
        private readonly IAsyncHandler<ExecutionPayload, PayloadStatusV1> _newPayloadV1Handler;
        private readonly IForkchoiceUpdatedHandler _forkchoiceUpdatedV1Handler;
        private readonly IHandler<ExecutionStatusResult> _executionStatusHandler;
        private readonly IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]> _executionGetPayloadBodiesByHashV1Handler;
        private readonly IGetPayloadBodiesByRangeV1Handler _executionGetPayloadBodiesByRangeV1Handler;
        private readonly IHandler<TransitionConfigurationV1, TransitionConfigurationV1> _transitionConfigurationHandler;
        private readonly SemaphoreSlim _locker = new(1, 1);
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(8);
        private readonly ILogger _logger;

        public EngineRpcModule(
            IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
            IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
            IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
            IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
            IHandler<ExecutionStatusResult> executionStatusHandler,
            IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result?[]> executionGetPayloadBodiesByHashV1Handler,
            IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
            IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
            ILogManager logManager)
        {
            _getPayloadHandlerV1 = getPayloadHandlerV1;
            _getPayloadHandlerV2 = getPayloadHandlerV2;
            _newPayloadV1Handler = newPayloadV1Handler;
            _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
            _executionStatusHandler = executionStatusHandler;
            _executionGetPayloadBodiesByHashV1Handler = executionGetPayloadBodiesByHashV1Handler;
            _executionGetPayloadBodiesByRangeV1Handler = executionGetPayloadBodiesByRangeV1Handler;
            _transitionConfigurationHandler = transitionConfigurationHandler;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<ExecutionStatusResult> engine_executionStatus() => _executionStatusHandler.Handle();

        public Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId) =>
            _getPayloadHandlerV1.HandleAsync(payloadId);

        public async Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId) =>
            await _getPayloadHandlerV2.HandleAsync(payloadId);

        public async Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload executionPayload)
        {
            if (executionPayload.Withdrawals != null)
            {
                var error = $"Withdrawals not supported in {nameof(engine_newPayloadV1)}";

                if (_logger.IsWarn) _logger.Warn(error);

                return ResultWrapper<PayloadStatusV1>.Fail(error, ErrorCodes.InvalidParams);
            }

            return await NewPayload(executionPayload, nameof(engine_newPayloadV1));
        }

        public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload executionPayload) =>
            NewPayload(executionPayload, nameof(engine_newPayloadV2));

        public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        {
            if (payloadAttributes?.Withdrawals != null)
            {
                var error = $"Withdrawals not supported in {nameof(engine_forkchoiceUpdatedV1)}";

                if (_logger.IsWarn) _logger.Warn(error);

                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(error, ErrorCodes.InvalidParams);
            }

            return await ForkchoiceUpdated(forkchoiceState, payloadAttributes, nameof(engine_forkchoiceUpdatedV1));
        }

        public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null) =>
            ForkchoiceUpdated(forkchoiceState, payloadAttributes, nameof(engine_forkchoiceUpdatedV2));

        public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(
            TransitionConfigurationV1 beaconTransitionConfiguration) => _transitionConfigurationHandler.Handle(beaconTransitionConfiguration);

        private async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> ForkchoiceUpdated(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes, string methodName)
        {
            if (await _locker.WaitAsync(_timeout))
            {
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    return await _forkchoiceUpdatedV1Handler.Handle(forkchoiceState, payloadAttributes);
                }
                finally
                {
                    watch.Stop();
                    Metrics.ForkchoiceUpdedExecutionTime = watch.ElapsedMilliseconds;
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{methodName} timed out");
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("Timed out", ErrorCodes.Timeout);
            }
        }

        private async Task<ResultWrapper<PayloadStatusV1>> NewPayload(ExecutionPayload executionPayload, string methodName)
        {
            if (await _locker.WaitAsync(_timeout))
            {
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    return await _newPayloadV1Handler.HandleAsync(executionPayload);
                }
                catch (Exception exception)
                {
                    if (_logger.IsError) _logger.Error($"{methodName} failed: {exception}");
                    return ResultWrapper<PayloadStatusV1>.Fail(exception.Message);
                }
                finally
                {
                    watch.Stop();
                    Metrics.NewPayloadExecutionTime = watch.ElapsedMilliseconds;
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{methodName} timed out");
                return ResultWrapper<PayloadStatusV1>.Fail("Timed out", ErrorCodes.Timeout);
            }
        }

        public async Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByHashV1(Keccak[] blockHashes)
        {
            return await _executionGetPayloadBodiesByHashV1Handler.HandleAsync(blockHashes);
        }

        public async Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> engine_getPayloadBodiesByRangeV1(long start, long count)
        {
            return await _executionGetPayloadBodiesByRangeV1Handler.Handle(start, count);
        }
    }
}
