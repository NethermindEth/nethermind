using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Dsl.JsonRpc
{
    [RpcModule(ModuleType.Dsl)]
    public interface IDslRpcModule : IRpcModule
    {
        ResultWrapper<int> dsl_addScript(string script);
        ResultWrapper<bool> dsl_removeScript(int index);
        ResultWrapper<string> dsl_inspectScript(int index);
        ResultWrapper<bool> dsl_stopPublisher(int index);
        ResultWrapper<bool> dsl_startPublisher(int index);
    }
}