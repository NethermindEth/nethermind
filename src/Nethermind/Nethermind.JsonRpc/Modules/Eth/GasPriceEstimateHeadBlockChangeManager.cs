using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    //Checks edge cases where the Head block did not change for previous call of eth_gasPrice from an instance of EthRpcModule
    public class GasPriceEstimateHeadBlockChangeManager : IHeadBlockChangeManager
    {
        public bool ShouldReturnSameGasPrice(Block? lastHead, Block? currentHead, UInt256? lastGasPrice)
        { 
            if (lastGasPrice != null && lastHead != null && lastHead!.Hash == currentHead!.Hash)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
