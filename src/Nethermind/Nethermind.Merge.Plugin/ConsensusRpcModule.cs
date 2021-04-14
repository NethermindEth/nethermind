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
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin
{
    public class ConsensusRpcModule : IConsensusRpcModule
    {
        private readonly IHandler<AssembleBlockRequest, BlockRequestResult> _assembleBlockHandler;
        private readonly IHandler<BlockRequestResult, NewBlockResult>_newBlockHandler;
        private readonly IHandler<Keccak, SuccessResult> _setHeadHandler;
        private readonly IHandler<Keccak, SuccessResult> _finaliseBlockHandler;
        private readonly IBlockTree _blockTree;

        // temp
        public ConsensusRpcModule() {}
        
        public ConsensusRpcModule(
            IHandler<AssembleBlockRequest, BlockRequestResult> assembleBlockHandler,
            IHandler<BlockRequestResult, NewBlockResult> newBlockHandler,
            IHandler<Keccak, SuccessResult> setHeadHandler,
            IHandler<Keccak, SuccessResult> finaliseBlockHandler,
            IBlockTree blockTree)
        {
            _assembleBlockHandler = assembleBlockHandler;
            _newBlockHandler = newBlockHandler;
            _setHeadHandler = setHeadHandler;
            _finaliseBlockHandler = finaliseBlockHandler;
            _blockTree = blockTree;
        }
        
        public ResultWrapper<BlockRequestResult> consensus_assembleBlock(AssembleBlockRequest request)
        {
            return _assembleBlockHandler.Handle(request);
        }

        public ResultWrapper<NewBlockResult> consensus_newBlock(BlockRequestResult requestResult)
        {
            return _newBlockHandler.Handle(requestResult);
        }

        public ResultWrapper<SuccessResult> consensus_setHead(Keccak blockHash)
        {
            return _setHeadHandler.Handle(blockHash);
        }

        public ResultWrapper<SuccessResult> consensus_finaliseBlock(Keccak blockHash)
        {
            return _finaliseBlockHandler.Handle(blockHash);
        }
    }
}
