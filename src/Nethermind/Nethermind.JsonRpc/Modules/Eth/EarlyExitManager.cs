using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    //Checks edge cases where the Head block did not change from previous call of eth_gasPrice from an instance of EthRpcModule
    public class EarlyExitManager
    {
        public Tuple<bool, ResultWrapper<UInt256?>> CheckChangeInHeadBlock(ref Block? lastHead, Block? currentHead, UInt256? lastGasPrice)
        {
            ResultWrapper<UInt256?> resultWrapper;

            resultWrapper = HandleNoHeadBlockChange(lastHead, currentHead, lastGasPrice);
            if (HeadBlockDidNotChange(resultWrapper))
            {
                return BoolAndWrapperTuple(false, resultWrapper);
            }
            else
            {
                SetLastHeadBlock(ref lastHead, currentHead);
                return BoolAndWrapperTuple(true, resultWrapper);
            }
        }

        private ResultWrapper<UInt256?> HandleNoHeadBlockChange(Block? lastHead, Block? currentHead, UInt256? lastGasPrice)
        {
            ResultWrapper<UInt256?> resultWrapper;
            
            if (LastGasPriceExists(lastGasPrice) && LastHeadBlockExists(lastHead) && LastHeadIsSameAsCurrentHead(lastHead, currentHead))
            {
                resultWrapper = ResultWrapper<UInt256?>.Success(lastGasPrice);
#if DEBUG
                resultWrapper.ErrorCode = GasPriceConfig.NoHeadBlockChangeErrorCode;
#endif
                return resultWrapper;
            }
            else
            {
                return ResultWrapper<UInt256?>.Fail("");
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

        private void SetLastHeadBlock(ref Block? lastHead, Block? currentHead)
        {
            lastHead = currentHead;
        }

        private static Tuple<bool, ResultWrapper<UInt256?>> BoolAndWrapperTuple(bool boolean, ResultWrapper<UInt256?> resultWrapper)
        {
            return new(boolean, resultWrapper);
        }

        private static bool HeadBlockDidNotChange(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result == Result.Success;
        }
    }
}
