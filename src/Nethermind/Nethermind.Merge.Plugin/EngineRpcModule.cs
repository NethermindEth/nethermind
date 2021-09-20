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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Org.BouncyCastle.Asn1.Cms;
using Result = Nethermind.Merge.Plugin.Data.Result;

namespace Nethermind.Merge.Plugin
{
    public class EngineRpcModule : IEngineRpcModule
    {
        private readonly IHandlerAsync<AssembleBlockRequest, BlockRequestResult?> _assembleBlockHandler;
        private readonly IHandler<BlockRequestResult, NewBlockResult> _newBlockHandler;
        private readonly IHandler<Keccak, Result> _setHeadHandler;
        private readonly IHandler<Keccak, Result> _finaliseBlockHandler;
        private readonly SemaphoreSlim _locker = new(1, 1);
        private readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private readonly ILogger _logger;

        public EngineRpcModule(
            IHandlerAsync<AssembleBlockRequest, BlockRequestResult?> assembleBlockHandler,
            IHandler<BlockRequestResult, NewBlockResult> newBlockHandler,
            IHandler<Keccak, Result> setHeadHandler,
            IHandler<Keccak, Result> finaliseBlockHandler,
            ILogManager logManager)
        {
            _assembleBlockHandler = assembleBlockHandler;
            _newBlockHandler = newBlockHandler;
            _setHeadHandler = setHeadHandler;
            _finaliseBlockHandler = finaliseBlockHandler;
            _logger = logManager.GetClassLogger();
        }

        public Task<ResultWrapper<BlockRequestResult?>> engine_assembleBlock(AssembleBlockRequest request)
        {
            return _assembleBlockHandler.HandleAsync(request);
        }

        public async Task<ResultWrapper<NewBlockResult>> engine_newBlock(BlockRequestResult requestResult)
        {
            if (await _locker.WaitAsync(Timeout))
            {
                try
                {
                    return _newBlockHandler.Handle(requestResult);
                }
                finally
                {
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_newBlock)} timeout.");
                return ResultWrapper<NewBlockResult>.Success(new NewBlockResult {Valid = false});
            }
        }

        public async Task<ResultWrapper<Result>> engine_setHead(Keccak blockHash)
        {
            if (await _locker.WaitAsync(Timeout))
            {
                try
                {
                    return _setHeadHandler.Handle(blockHash);
                }
                finally
                {
                    _locker.Release();
                }
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(engine_setHead)} timeout.");
                return ResultWrapper<Result>.Success(Result.Fail);
            }
        }

        public Task<ResultWrapper<Result>> engine_finaliseBlock(Keccak blockHash) => 
            Task.FromResult(_finaliseBlockHandler.Handle(blockHash));

        public Task engine_preparePayload(Keccak parentHash, UInt256 timestamp, Keccak random, Address coinbase, uint payloadId)
        {
            throw new NotImplementedException();
        }

        public Task<ResultWrapper<BlockRequestResult?>> engine_getPayload(uint payloadId)
        {
            throw new NotImplementedException();
        }

        public Task<ResultWrapper<ExecutePayloadResult>> engine_executePayload(BlockRequestResult executionPayload)
        {
            throw new NotImplementedException();
        }

        public Task engine_consensusValidated(Keccak parentHash, VerificationStatus status)
        {
            throw new NotImplementedException();
        }

        public Task engine_forkchoiceUpdated(Keccak headBlockHash, Keccak finalizedBlockHash, Keccak confirmedBlockHash)
        {
            throw new NotImplementedException();
        }

        public Task engine_terminalTotalDifficultyUpdated(UInt256 terminalTotalDifficulty)
        {
            throw new NotImplementedException();
        }

        public Task engine_terminalPoWBlockOverride(Keccak blockHash)
        {
            throw new NotImplementedException();
        }

        public Task<ResultWrapper<Block?>> engine_getPowBlock(Keccak blockHash)
        {
            throw new NotImplementedException();
        }

        public Task engine_syncCheckpointSet(BlockRequestResult executionPayloadHeader)
        {
            throw new NotImplementedException();
        }

        public Task engine_syncStatus(SyncStatus sync, Keccak blockHash, UInt256 blockNumber)
        {
            throw new NotImplementedException();
        }

        public Task engine_consensusStatus(UInt256 transitionTotalDifficulty, Keccak terminalPowBlockHash,
            Keccak finalizedBlockHash,
            Keccak confirmedBlockHash, Keccak headBlockHash)
        {
            throw new NotImplementedException();
        }

        public Task engine_executionStatus(Keccak finalizedBlockHash, Keccak confirmedBlockHash, Keccak headBlockHash)
        {
            throw new NotImplementedException();
        }
    }
}
