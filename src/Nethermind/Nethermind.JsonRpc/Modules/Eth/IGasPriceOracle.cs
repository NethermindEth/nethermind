using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public interface IGasPriceOracle
    {
        public ISpecProvider SpecProvider { get; }
        public UInt256? FallbackGasPrice { get; }
        public List<UInt256> TxGasPriceList { get; }
        ResultWrapper<UInt256?> GasPriceEstimate(Block? headBlock, IBlockFinder blockFinder);
    }
}
