using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Providers.Plugins.JsonRpc
{
    public interface IJsonRpcClient
    {
        string PostAsync(string data);
    }
}