using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public interface IGasPriceOracle
    {
        ResultWrapper<UInt256?> GasPriceEstimate(UInt256? ignoreUnder = null);
    }
}
