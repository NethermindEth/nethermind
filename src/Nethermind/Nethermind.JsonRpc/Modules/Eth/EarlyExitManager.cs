using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    //Checks edge cases where:
    //    1. Head or Genesis block is missing
    //    2. The Head block did not change from previous call of eth_gasPrice from an instance of EthRpcModule
    public class EarlyExitManager
    {
        private readonly Block? _headBlock;
        private readonly UInt256? _lastGasPrice; 
        public EarlyExitManager(Block? headBlock, UInt256? lastGasPrice)
        {
            _headBlock = headBlock;
            _lastGasPrice = lastGasPrice;
        }

        public Tuple<bool, ResultWrapper<UInt256?>> NoHeadBlockChangeResult(ref Block lastHeadBlock)
        {
            ResultWrapper<UInt256?> resultWrapper;

            resultWrapper = HandleNoHeadBlockChange(lastHeadBlock);
            if (ResultWrapperWasSuccessful(resultWrapper))
            {
                return BoolAndWrapperTuple(true, resultWrapper);
            }
            SetLastHeadBlock(ref lastHeadBlock);
            return BoolAndWrapperTuple(false, resultWrapper);
        }

        private ResultWrapper<UInt256?> HandleNoHeadBlockChange(Block? lastHeadBlock)
        {
            ResultWrapper<UInt256?> resultWrapper;
            
            if (LastGasPriceExists() && LastHeadBlockExists(lastHeadBlock) && LastHeadIsSameAsCurrentHead(lastHeadBlock))
            {
                resultWrapper = ResultWrapper<UInt256?>.Success(_lastGasPrice);
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

        private bool LastGasPriceExists()
        {
            return _lastGasPrice != null;
        }

        private bool LastHeadBlockExists(Block? lastHeadBlock)
        {
            return lastHeadBlock != null;
        }

        private bool LastHeadIsSameAsCurrentHead(Block? lastHeadBlock)
        {
            return _headBlock!.Hash == lastHeadBlock!.Hash;
        }

        private void SetLastHeadBlock(ref Block? lastHeadBlock)
        {
            lastHeadBlock = _headBlock;
        }

        private static Tuple<bool, ResultWrapper<UInt256?>> BoolAndWrapperTuple(bool boolean, ResultWrapper<UInt256?> resultWrapper)
        {
            return new(boolean, resultWrapper);
        }

        private static bool ResultWrapperWasSuccessful(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result == Result.Success;
        }
    }
}
