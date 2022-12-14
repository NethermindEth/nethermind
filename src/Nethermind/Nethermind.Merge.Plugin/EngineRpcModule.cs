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
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Data.V2;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public class EngineRpcModule : IEngineRpcModule
    {
        private readonly IAsyncHandler<byte[], ExecutionPayloadV1?> _getPayloadHandlerV1;
        private readonly IAsyncHandler<byte[], GetPayloadV2Result?> _getPayloadHandlerV2;
        private readonly IAsyncHandler<ExecutionPayloadV1, PayloadStatusV1> _newPayloadV1Handler;
        private readonly IForkchoiceUpdatedV1Handler _forkchoiceUpdatedV1Handler;
        private readonly IHandler<ExecutionStatusResult> _executionStatusHandler;
        private readonly IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result[]> _executionPayloadBodiesHandler;
        private readonly IHandler<TransitionConfigurationV1, TransitionConfigurationV1> _transitionConfigurationHandler;
        private readonly SemaphoreSlim _locker = new(1, 1);
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(8);
        private readonly ILogger _logger;

        public EngineRpcModule(
            IAsyncHandler<byte[], ExecutionPayloadV1?> getPayloadHandlerV1,
            IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
            IAsyncHandler<ExecutionPayloadV1, PayloadStatusV1> newPayloadV1Handler,
            IForkchoiceUpdatedV1Handler forkchoiceUpdatedV1Handler,
            IHandler<ExecutionStatusResult> executionStatusHandler,
            IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result[]> executionPayloadBodiesHandler,
            IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
            ILogManager logManager)
        {
            _getPayloadHandlerV1 = getPayloadHandlerV1;
            _getPayloadHandlerV2 = getPayloadHandlerV2;
            _newPayloadV1Handler = newPayloadV1Handler;
            _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
            _executionStatusHandler = executionStatusHandler;
            _executionPayloadBodiesHandler = executionPayloadBodiesHandler;
            _transitionConfigurationHandler = transitionConfigurationHandler;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<ExecutionStatusResult> engine_executionStatus()
        {
            return _executionStatusHandler.Handle();
        }

        public async Task<ResultWrapper<ExecutionPayloadV1?>> engine_getPayloadV1(byte[] payloadId)
        {
            return await (_getPayloadHandlerV1.HandleAsync(payloadId));
        }

        public async Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId)
        {
            return await (_getPayloadHandlerV2.HandleAsync(payloadId));
        }

        public async Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayloadV1 executionPayload)
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
                    if (_logger.IsError) _logger.Error($"{nameof(engine_newPayloadV1)} threw an unexpected exception. {exception}");
                    return ResultWrapper<PayloadStatusV1>.Fail($"{nameof(engine_newPayloadV1)} threw an unexpected exception. {exception}");
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
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_newPayloadV1)} timeout.");
                return ResultWrapper<PayloadStatusV1>.Fail($"{nameof(engine_newPayloadV1)} timeout.", ErrorCodes.Timeout);
            }
        }


        public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
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
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_forkchoiceUpdatedV1)} timeout.");
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail($"{nameof(engine_forkchoiceUpdatedV1)} timeout.", ErrorCodes.Timeout);
            }
        }

        public async Task<ResultWrapper<ExecutionPayloadBodyV1Result[]>> engine_getPayloadBodiesByHashV1(Keccak[] blockHashes)
        {
            return await _executionPayloadBodiesHandler.HandleAsync(blockHashes);
        }

        public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(
            TransitionConfigurationV1 beaconTransitionConfiguration)
        {
            return _transitionConfigurationHandler.Handle(beaconTransitionConfiguration);
        }
    }
}
