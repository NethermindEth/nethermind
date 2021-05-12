using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Dsl.JsonRpc
{
    [RpcModule(ModuleType.Dsl)]
    public interface IDslRpcModule : IRpcModule
    {
        ResultWrapper<int> dsl_addScript(string script);
    }
}