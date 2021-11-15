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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public class EngineRpcModule : IEngineRpcModule
    {
        private readonly IHandler<PreparePayloadRequest, PreparePayloadResult> _preparePayloadHandler;
        private readonly IAsyncHandler<ulong, BlockRequestResult?> _getPayloadHandler;
        private readonly IAsyncHandler<byte[], BlockRequestResult?> _getPayloadHandlerV1;
        private readonly IHandler<BlockRequestResult, ExecutePayloadResult> _executePayloadHandler;
        private readonly IHandler<BlockRequestResult, ExecutePayloadV1Result> _executePayloadV1Handler;
        private readonly IHandler<ForkChoiceUpdatedRequest, string> _forkChoiceUpdateHandler;
        private readonly IForkchoiceUpdatedV1Handler _forkchoiceUpdatedV1Handler;
        private readonly IHandler<ExecutionStatusResult> _executionStatusHandler;
        private readonly ITransitionProcessHandler _transitionProcessHandler;
        private readonly SemaphoreSlim _locker = new(1, 1);
        private readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private readonly ILogger _logger;

        public EngineRpcModule(
            IHandler<PreparePayloadRequest, PreparePayloadResult> preparePayloadHandler,
            IAsyncHandler<ulong, BlockRequestResult?> getPayloadHandler,
            IAsyncHandler<byte[], BlockRequestResult?> getPayloadHandlerV1,
            IHandler<BlockRequestResult, ExecutePayloadResult> executePayloadHandler,
            IHandler<BlockRequestResult, ExecutePayloadV1Result> executePayloadV1Handler,
            ITransitionProcessHandler transitionProcessHandler,
            IHandler<ForkChoiceUpdatedRequest, string> forkChoiceUpdateHandler,
            IForkchoiceUpdatedV1Handler forkchoiceUpdatedV1Handler,
            IHandler<ExecutionStatusResult> executionStatusHandler,
            ILogManager logManager)
        {
            _preparePayloadHandler = preparePayloadHandler;
            _getPayloadHandler = getPayloadHandler;
            _getPayloadHandlerV1 = getPayloadHandlerV1;
            _executePayloadHandler = executePayloadHandler;
            _executePayloadV1Handler = executePayloadV1Handler;
            _transitionProcessHandler = transitionProcessHandler;
            _forkChoiceUpdateHandler = forkChoiceUpdateHandler;
            _forkchoiceUpdatedV1Handler = forkchoiceUpdatedV1Handler;
            _executionStatusHandler = executionStatusHandler;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<PreparePayloadResult> engine_preparePayload(
            PreparePayloadRequest preparePayloadRequest)
        {
                return _preparePayloadHandler.Handle(preparePayloadRequest);
        }

        public async Task<ResultWrapper<BlockRequestResult?>> engine_getPayload(ulong payloadId)
        {
            return await (_getPayloadHandler.HandleAsync(payloadId));
        }

        public async Task<ResultWrapper<ExecutePayloadResult>> engine_executePayload(
            BlockRequestResult executionPayload)
        {
            if (await _locker.WaitAsync(Timeout))
            {
                try
                {
                    return _executePayloadHandler.Handle(executionPayload);
                }
                finally
                {
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_executePayload)} timeout.");
                return ResultWrapper<ExecutePayloadResult>.Success(new ExecutePayloadResult()
                {
                    BlockHash = executionPayload.BlockHash, EnumStatus = VerificationStatus.Invalid
                });
            }
        }

        public async Task<ResultWrapper<string>> engine_consensusValidated(
            ConsensusValidatedRequest consensusValidatedRequest)
        {
            return ResultWrapper<string>.Success(null);
        }

        public async Task<ResultWrapper<string>> engine_forkchoiceUpdated(
            ForkChoiceUpdatedRequest forkChoiceUpdatedRequest)
        {
            if (await _locker.WaitAsync(Timeout))
            {
                try
                {
                    return _forkChoiceUpdateHandler.Handle(forkChoiceUpdatedRequest);
                }
                finally
                {
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_forkchoiceUpdated)} timeout.");
                return ResultWrapper<string>.Fail($"{nameof(engine_forkchoiceUpdated)} timeout.", ErrorCodes.Timeout);
            }
        }

        public ResultWrapper<string> engine_terminalTotalDifficultyUpdated(UInt256 terminalTotalDifficulty)
        {
            _transitionProcessHandler.TerminalTotalDifficulty = terminalTotalDifficulty;
            return ResultWrapper<string>.Success(null);
        }

        public ResultWrapper<string> engine_terminalPoWBlockOverride(Keccak blockHash)
        {
            _transitionProcessHandler.SetTerminalPoWHash(blockHash);
            return ResultWrapper<string>.Success(null);
        }

        public Task<ResultWrapper<Block?>> engine_getPowBlock(Keccak blockHash)
        {
            // probably this method won't be needed
            throw new NotImplementedException();
        }

        public ResultWrapper<string> engine_syncCheckpointSet(BlockRequestResult executionPayloadHeader)
        {
            return ResultWrapper<string>.Success(null);
        }

        public ResultWrapper<string> engine_syncStatus(SyncStatus sync, Keccak blockHash, UInt256 blockNumber)
        {
            return ResultWrapper<string>.Success(null);
        }

        public ResultWrapper<string> engine_consensusStatus(UInt256 transitionTotalDifficulty,
            Keccak terminalPowBlockHash,
            Keccak finalizedBlockHash,
            Keccak confirmedBlockHash, Keccak headBlockHash)
        {
            return ResultWrapper<string>.Success(null);
        }

        public ResultWrapper<ExecutionStatusResult> engine_executionStatus()
        {
            return _executionStatusHandler.Handle();
        }
        
        public async Task<ResultWrapper<BlockRequestResult?>> engine_getPayloadV1(byte[] payloadId, int? id = null)
        {
            return await (_getPayloadHandlerV1.HandleAsync(payloadId));
        }
        
                
        public async Task<ResultWrapper<ExecutePayloadV1Result>> engine_executePayloadV1(BlockRequestResult executionPayload, int? id = null)
        {
            if (await _locker.WaitAsync(Timeout))
            {
                try
                {
                    return _executePayloadV1Handler.Handle(executionPayload);
                }
                finally
                {
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_executePayloadV1)} timeout.");
                return ResultWrapper<ExecutePayloadV1Result>.Fail($"{nameof(engine_executePayloadV1)} timeout.", ErrorCodes.Timeout);
            }
        }
        
        

        public async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null, int? id = null)
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
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_forkchoiceUpdated)} timeout.");
                return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail($"{nameof(engine_forkchoiceUpdated)} timeout.", ErrorCodes.Timeout);
            }
        }
    }
}
