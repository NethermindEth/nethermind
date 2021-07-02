using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EarlyExitManager
    {
        private IBlockFinder? _blockFinder;
        private UInt256? _lastGasPrice; 
        public EarlyExitManager(IBlockFinder? blockFinder, UInt256? lastGasPrice)
        {
            _blockFinder = blockFinder;
            _lastGasPrice = lastGasPrice;
        }

        public Tuple<bool, ResultWrapper<UInt256?>> EarlyExitResult(ref Block lastHeadBlock)
        {
            Block? headBlock = GetHeadBlock();
            Block? genesisBlock = GetGenesisBlock();
            ResultWrapper<UInt256?> resultWrapper;

            resultWrapper = HandleMissingHeadOrGenesisBlockCase(headBlock, genesisBlock);
            if (ResultWrapperWasNotSuccessful(resultWrapper))
            {
                return BoolAndWrapperTuple(true, resultWrapper);
            }

            resultWrapper = HandleNoHeadBlockChange(lastHeadBlock, headBlock);
            if (ResultWrapperWasSuccessful(resultWrapper))
            {
                return BoolAndWrapperTuple(true, resultWrapper);
            }
            SetLastHeadBlock(ref lastHeadBlock, headBlock);
            return BoolAndWrapperTuple(false, resultWrapper);
        }

        private Block? GetHeadBlock()
        {
            return _blockFinder!.FindHeadBlock();
        }
        
        private Block? GetGenesisBlock()
        {
            return _blockFinder!.FindGenesisBlock();
        }
        
        private static bool ResultWrapperWasNotSuccessful(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result != Result.Success;
        }
        
        private ResultWrapper<UInt256?> HandleMissingHeadOrGenesisBlockCase(Block? headBlock, Block? genesisBlock)
        {
            if (BlockDoesNotExist(headBlock))
            {
                return ResultWrapper<UInt256?>.Fail("The head block had a null value.");
            }
            else if (BlockDoesNotExist(genesisBlock))
            {
                return ResultWrapper<UInt256?>.Fail("The genesis block had a null value.");
            }
            else
            {
                return ResultWrapper<UInt256?>.Success(UInt256.Zero);
            }
        }

        private static bool BlockDoesNotExist(Block? block)
        {
            return block == null;
        }

        private ResultWrapper<UInt256?> HandleNoHeadBlockChange(Block? lastHeadBlock, Block? headBlock)
        {
            ResultWrapper<UInt256?> resultWrapper;
            
            if (LastGasPriceExists() && LastHeadBlockExists(lastHeadBlock) && LastHeadIsSameAsCurrentHead(lastHeadBlock, headBlock))
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

        private bool LastHeadIsSameAsCurrentHead(Block? lastHeadBlock, Block? headBlock)
        {
            return headBlock!.Hash == lastHeadBlock!.Hash;
        }

        private void SetLastHeadBlock(ref Block? lastHeadBlock, Block? headBlock)
        {
            lastHeadBlock = headBlock;
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
