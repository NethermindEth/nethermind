// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Data.V2;

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
            Description = "Returns the most recent version of an execution payload with respect to the transaction set contained by the mempool.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<ExecutionPayloadV1?>> engine_getPayloadV1(byte[] payloadId);

        [JsonRpcMethod(
            Description =
                "Returns the most recent version of an execution payload and fees with respect to the transaction set contained by the mempool.",
            IsSharable = true,
            IsImplemented = true)]
        public Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId);

        [JsonRpcMethod(
            Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayloadV1 executionPayload);

        [JsonRpcMethod(
            Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null);

        [JsonRpcMethod(
            Description = "Returns an array of execution payload bodies for the list of provided block hashes.",
            IsSharable = true,
            IsImplemented = true)]
        Task<ResultWrapper<ExecutionPayloadBodyV1Result[]>> engine_getPayloadBodiesByHashV1(Keccak[] blockHashes);

        [JsonRpcMethod(
            Description = "Returns PoS transition configuration.",
            IsSharable = true,
            IsImplemented = true)]
        ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(TransitionConfigurationV1 beaconTransitionConfiguration);
    }
}
