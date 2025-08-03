using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.ContractSearch.Plugin;

public interface IContractSearchRpcModule : IRpcModule
{
    ResultWrapper<ContractSearchResult[]> search_contracts(byte[][] bytecodes);
}
