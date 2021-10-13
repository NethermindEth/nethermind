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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin
{
    [RpcModule(ModuleType.Engine)]
    public interface IEngineRpcModule : IRpcModule
    {
        [JsonRpcMethod(
            Description =
                "Builds an execution payload on top of a given parent with transactions selected from the mempool.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<PreparePayloadResult> engine_preparePayload(PreparePayloadRequest preparePayloadRequest);
        
        [JsonRpcMethod(
            Description =
                "Returns the most recent version of an execution payload with respect to the transaction set contained by the mempool.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<BlockRequestResult?>> engine_getPayload(ulong payloadId);

        [JsonRpcMethod(
            Description =
                "Verifies the payload according to the execution environment rule set and returns the status of the verification.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<ExecutePayloadResult>> engine_executePayload(BlockRequestResult executionPayload);
        
        [JsonRpcMethod(
            Description =
                "Communicates that full consensus validation of an execution payload is complete along with its corresponding status.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<string>> engine_consensusValidated(ConsensusValidatedRequest consensusValidatedRequest);
        
        [JsonRpcMethod(
            Description = "Propagates the change in the fork choice to the execution client.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<string>> engine_forkchoiceUpdated(ForkChoiceUpdatedRequest forkChoiceUpdatedRequest);

        [JsonRpcMethod(
            Description = "Propagates an override of the TERMINAL_TOTAL_DIFFICULTY to the execution client.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<string> engine_terminalTotalDifficultyUpdated(UInt256 terminalTotalDifficulty);
        
        [JsonRpcMethod(
            Description = "Propagates the hash of the terminal PoW block.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<string> engine_terminalPoWBlockOverride(Keccak blockHash);
        
        [JsonRpcMethod(
            Description = "Given the hash returns the information of the PoW block.",
            IsSharable = true,
            IsImplemented = false)]
        Task<ResultWrapper<Block?>> engine_getPowBlock(Keccak blockHash);
        
        [JsonRpcMethod(
            Description =
                "Propagates the header of the payload obtained from the state at the weak subjectivity checkpoint.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<string> engine_syncCheckpointSet(BlockRequestResult executionPayloadHeader);
        
        [JsonRpcMethod(
            Description =
                "An execution client responds with this status to any request of the consensus layer while sync is being in progress.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<string> engine_syncStatus(SyncStatus sync, Keccak blockHash, UInt256 blockNumber);
        
        [JsonRpcMethod(
            Description =
                "Sends information on the state of the client to the execution side.",
            IsSharable = true,
            IsImplemented = false)]
        ResultWrapper<string> engine_consensusStatus(UInt256 transitionTotalDifficulty, Keccak terminalPowBlockHash,
            Keccak finalizedBlockHash, Keccak confirmedBlockHash, Keccak headBlockHash);

        [JsonRpcMethod(
            Description =
                "Responds with information on the state of the execution client to either engine_consensusStatus or any other call if consistency failure has occurred.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<ExecutionStatusResult> engine_executionStatus();
    }
}
