using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    //Checks edge cases where the Head block did not change from previous call of eth_gasPrice from an instance of EthRpcModule
    public class HeadBlockChangeManager : IHeadBlockChangeManager
    {
        public bool ReturnSameGasPrice(Block? lastHead, Block? currentHead, UInt256? lastGasPrice)
        { 
            if (HeadBlockChanged(lastHead, currentHead, lastGasPrice))
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        private bool HeadBlockChanged(Block? lastHead, Block? currentHead, UInt256? lastGasPrice)
        {
            if (LastGasPriceExists(lastGasPrice) && LastHeadBlockExists(lastHead) && LastHeadIsSameAsCurrentHead(lastHead, currentHead))
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        private bool LastGasPriceExists(UInt256? lastGasPrice)
        {
            return lastGasPrice != null;
        }

        private bool LastHeadBlockExists(Block? lastHead)
        {
            return lastHead != null;
        }

        private bool LastHeadIsSameAsCurrentHead(Block? lastHead, Block? currentHead)
        {
            return lastHead!.Hash == currentHead!.Hash;
        }
    }
}
