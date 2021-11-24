using Nethermind.Api;
using System.Linq;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class GasLimitProviderAvg : IGasLimitProvider
    {
        INethermindApi _api;

        public GasLimitProviderAvg(INethermindApi api)
        {
            _api = api;
        }
        public ulong GetGasLimit()
        {
            var txs = _api.BlockTree!.Head!.Transactions;
            return (ulong)(txs.Select(tx => tx.GasLimit).Sum()) / (ulong)txs.Length;
        }
    }
}
