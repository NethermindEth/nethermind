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

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin
{
    [RpcModule(ModuleType.Consensus)]
    public interface IConsensusRpcModule : IRpcModule
    {
        [JsonRpcMethod(
            Description = "Requests a block to be assembled from the tx pool transactions.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<BlockRequestResult?>> consensus_assembleBlock(AssembleBlockRequest request);
        
        [JsonRpcMethod(
            Description = "Injects a new block from the consensus layer.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<NewBlockResult>> consensus_newBlock(
            BlockRequestResult requestResult);
        
        [JsonRpcMethod(
            Description = "Changes consensus layer head block.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<Result>> consensus_setHead(
            Keccak blockHash);        
        
        [JsonRpcMethod(
            Description = "Marks consensus layer block as finalized.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<Result>> consensus_finaliseBlock(
            Keccak blockHash);
    }
}
