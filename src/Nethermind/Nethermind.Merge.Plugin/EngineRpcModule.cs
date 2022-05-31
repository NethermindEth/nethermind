//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public class EngineRpcModule : IEngineRpcModule
    {
        private readonly IAsyncHandler<byte[], BlockRequestResult?> _getPayloadHandlerV1;
        private readonly IAsyncHandler<BlockRequestResult, PayloadStatusV1> _newPayloadV1Handler;
        private readonly IForkchoiceUpdatedV1Handler _forkchoiceUpdatedV1Handler;
        private readonly IHandler<ExecutionStatusResult> _executionStatusHandler;
        private readonly IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result[]> _executionPayloadBodiesHandler;
        private readonly IHandler<TransitionConfigurationV1, TransitionConfigurationV1> _transitionConfigurationHandler;
        private readonly SemaphoreSlim _locker = new(1, 1);
        private readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
        private readonly ILogger _logger;

        public EngineRpcModule(
            IAsyncHandler<byte[], BlockRequestResult?> getPayloadHandlerV1,
            IAsyncHandler<BlockRequestResult, PayloadStatusV1> newPayloadV1Handler,
            IForkchoiceUpdatedV1Handler forkchoiceUpdatedV1Handler,
            IHandler<ExecutionStatusResult> executionStatusHandler,
            IAsyncHandler<Keccak[], ExecutionPayloadBodyV1Result[]> executionPayloadBodiesHandler,
            IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
            ILogManager logManager)
        {
            _getPayloadHandlerV1 = getPayloadHandlerV1;
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

        public async Task<ResultWrapper<BlockRequestResult?>> engine_getPayloadV1(byte[] payloadId)
        {
            return await (_getPayloadHandlerV1.HandleAsync(payloadId));
        }


        public async Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(
            BlockRequestResult executionPayload)
        {
            if (await _locker.WaitAsync(Timeout))
            {
                try
                {
                    return await _newPayloadV1Handler.HandleAsync(executionPayload);
                }
                finally
                {
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_newPayloadV1)} timeout.");
                return ResultWrapper<PayloadStatusV1>.Fail($"{nameof(engine_newPayloadV1)} timeout.",
                    ErrorCodes.Timeout);
            }
        }


        public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(
            ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        {
            if (await _locker.WaitAsync(Timeout))
            {
                try
                {
                    return await _forkchoiceUpdatedV1Handler.Handle(forkchoiceState, payloadAttributes);
                }
                finally
                {
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_forkchoiceUpdatedV1)} timeout.");
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail($"{nameof(engine_forkchoiceUpdatedV1)} timeout.",
                    ErrorCodes.Timeout);
            }
        }

        public async Task<ResultWrapper<ExecutionPayloadBodyV1Result[]>> engine_getPayloadBodiesV1(Keccak[] blockHashes)
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
