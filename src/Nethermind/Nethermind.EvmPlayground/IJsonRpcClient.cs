using System.Threading.Tasks;

namespace Nethermind.EvmPlayground
{
    internal interface IJsonRpcClient
    {
        Task<string> Post(string method, params object[] parameters);
    }
}