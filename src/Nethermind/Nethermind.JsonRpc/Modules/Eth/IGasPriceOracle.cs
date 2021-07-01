using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public interface IGasPriceOracle
    {
        public UInt256? DefaultGasPrice { get; }
        public List<UInt256> TxGasPriceList { get; }
        ResultWrapper<UInt256?> GasPriceEstimate(IBlockFinder blockFinder);
    }
}
