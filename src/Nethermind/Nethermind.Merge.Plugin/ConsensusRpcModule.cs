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
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public class ConsensusRpcModule : IConsensusRpcModule
    {
        private readonly IHandlerAsync<AssembleBlockRequest, BlockRequestResult> _assembleBlockHandler;
        private readonly IHandler<BlockRequestResult, NewBlockResult>_newBlockHandler;
        private readonly IHandler<Keccak, Result> _setHeadHandler;
        private readonly IHandler<Keccak, Result> _finaliseBlockHandler;

        // temp
        public ConsensusRpcModule() {}
        
        public ConsensusRpcModule(
            IHandlerAsync<AssembleBlockRequest, BlockRequestResult> assembleBlockHandler,
            IHandler<BlockRequestResult, NewBlockResult> newBlockHandler,
            IHandler<Keccak, Result> setHeadHandler,
            IHandler<Keccak, Result> finaliseBlockHandler)
        {
            _assembleBlockHandler = assembleBlockHandler;
            _newBlockHandler = newBlockHandler;
            _setHeadHandler = setHeadHandler;
            _finaliseBlockHandler = finaliseBlockHandler;
        }
        
        public Task<ResultWrapper<BlockRequestResult>> consensus_assembleBlock(AssembleBlockRequest request)
        {
            return _assembleBlockHandler.HandleAsync(request);
        }

        public ResultWrapper<NewBlockResult> consensus_newBlock(BlockRequestResult requestResult)
        {
            return _newBlockHandler.Handle(requestResult);
        }

        public ResultWrapper<Result> consensus_setHead(Keccak blockHash)
        {
            return _setHeadHandler.Handle(blockHash);
        }

        public ResultWrapper<Result> consensus_finaliseBlock(Keccak blockHash)
        {
            return _finaliseBlockHandler.Handle(blockHash);
        }
    }
}
