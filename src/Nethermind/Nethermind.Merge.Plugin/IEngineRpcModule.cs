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
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin
{
    [RpcModule(ModuleType.Engine)]
    public interface IEngineRpcModule : IRpcModule
    {
        [JsonRpcMethod(
            Description =
                "Responds with information on the state of the execution client to either engine_consensusStatus or any other call if consistency failure has occurred.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<ExecutionStatusResult> engine_executionStatus();
        
        [JsonRpcMethod(
            Description =
                "Returns the most recent version of an execution payload with respect to the transaction set contained by the mempool.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<BlockRequestResult?>> engine_getPayloadV1(byte[] payloadId);
        
        [JsonRpcMethod(
            Description =
                "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(BlockRequestResult executionPayload);
        
        [JsonRpcMethod(
            Description =
                "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null);
        
        [JsonRpcMethod(
            Description =
                "Returns an array of execution payload bodies for the list of provided block hashes.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<ExecutionPayloadBodyV1Result[]>> engine_getPayloadBodiesV1(Keccak[] blockHashes);

        [JsonRpcMethod(
            Description =
                "Returns PoS transition configuration.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(
            TransitionConfigurationV1 beaconTransitionConfiguration);
    }
}
