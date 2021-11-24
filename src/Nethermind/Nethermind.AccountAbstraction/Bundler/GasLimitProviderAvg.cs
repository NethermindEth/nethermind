using Nethermind.Blockchain;
using System.Linq;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class GasLimitProviderAvg : IGasLimitProvider
    {
        IBlockTree _blockTree;

        public GasLimitProviderAvg(IBlockTree blockTree)
        {
            _blockTree = blockTree;
        }

        public ulong GetGasLimit()
        {
            var txs = _blockTree.Head!.Transactions;
            return (ulong)(txs.Select(tx => tx.GasLimit).Sum()) / (ulong)txs.Length;
        }
    }
}
