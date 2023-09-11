using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism;


[RpcModule(ModuleType.Engine)]
public interface IOptimismEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, OptimismPayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload executionPayload);
}
