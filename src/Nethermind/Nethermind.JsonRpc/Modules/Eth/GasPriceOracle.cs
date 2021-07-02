using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class GasPriceOracle : IGasPriceOracle
    {
        public UInt256? DefaultGasPrice { get; private set; }
        public List<UInt256> TxGasPriceList { get; private set; }
        private Block? _lastHeadBlock;
        private UInt256? _lastGasPrice;
        private readonly UInt256? _ignoreUnder;
        private IBlockFinder? _blockFinder;
        private readonly int _blockLimit;
        private readonly int _softTxThreshold;
        private bool _eip1559Enabled;
        private readonly UInt256 _baseFee;
        private readonly ValidTxAdder _addValidTx;

        public GasPriceOracle(bool eip1559Enabled = false, UInt256? ignoreUnder = null, 
            int? blockLimit = null, UInt256? baseFee = null)
        {
            TxGasPriceList = new List<UInt256>();
            _eip1559Enabled = eip1559Enabled;
            _ignoreUnder = ignoreUnder ?? UInt256.Zero;
            _blockLimit = blockLimit ?? GasPriceConfig.DefaultBlocksLimit;
            _softTxThreshold = GasPriceConfig.SoftTxLimit;
            _baseFee = baseFee ?? GasPriceConfig.DefaultBaseFee;
            _addValidTx = new ValidTxAdder(this, _ignoreUnder, _eip1559Enabled, _baseFee);
        }

        public ResultWrapper<UInt256?> GasPriceEstimate(IBlockFinder blockFinder)
        {
            if (_blockFinder == null)
            {
                _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            }

            Tuple<bool, ResultWrapper<UInt256?>> earlyExitResult = EarlyExitResult();
            if (ExitingEarly(earlyExitResult))
            {
                return earlyExitResult.Item2;
            }
            
            SetDefaultGasPrice();
            
            AddTxGasPricesToList();
            
            TxGasPriceList = TxGasPriceList.OrderBy(gasPrice => gasPrice).ToList();
            
            UInt256? gasPriceEstimate = GasPriceAtPercentile();

            gasPriceEstimate = FindMinOfThisAndMaxPrice(gasPriceEstimate);

            SetLastGasPrice(gasPriceEstimate);
            
            return ResultWrapper<UInt256?>.Success((UInt256) gasPriceEstimate!);
        }

        private static bool ExitingEarly(Tuple<bool, ResultWrapper<UInt256?>> earlyExitResult)
        {
            return earlyExitResult.Item1 == true;
        }

        private Tuple<bool, ResultWrapper<UInt256?>> EarlyExitResult()
        {
            Block? headBlock = GetHeadBlock();
            Block? genesisBlock = GetGenesisBlock();
            ResultWrapper<UInt256?> resultWrapper;

            resultWrapper = HandleMissingHeadOrGenesisBlockCase(headBlock, genesisBlock);
            if (ResultWrapperWasNotSuccessful(resultWrapper))
            {
                return BoolAndWrapperTuple(true, resultWrapper);
            }

            resultWrapper = HandleNoHeadBlockChange(headBlock);
            if (ResultWrapperWasSuccessful(resultWrapper))
            {
                return BoolAndWrapperTuple(true, resultWrapper);
            }
            SetLastHeadBlock(headBlock);
            return BoolAndWrapperTuple(false, resultWrapper);
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
        
        private ResultWrapper<UInt256?> HandleNoHeadBlockChange(Block? headBlock)
        {
            ResultWrapper<UInt256?> resultWrapper;
            
            if (LastGasPriceExists() && LastHeadBlockExists() && LastHeadIsSameAsCurrentHead(headBlock))
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

        private bool LastHeadBlockExists()
        {
            return _lastHeadBlock != null;
        }
        
        private bool LastHeadIsSameAsCurrentHead(Block? headBlock)
        {
            return headBlock!.Hash == _lastHeadBlock!.Hash;
        }
        
        private void SetDefaultGasPrice()
        {
            DefaultGasPrice = _lastGasPrice ?? GasPriceConfig.DefaultGasPrice;
        }
        
        private void AddTxGasPricesToList()
        {
            long currentBlockNumber = GetHeadBlock()!.Number;
            int blocksToGoBack = _blockLimit;
            while (MoreBlocksToGoBack(blocksToGoBack) && CurrentBlockNumberIsValid(currentBlockNumber)) 
            {
                Block? block = FindBlockAtNumber(currentBlockNumber);
                if (BlockExists(block))
                {
                    int txsAdded = _addValidTx.AddValidTxAndReturnCount(block!);
                    if (txsAdded > 1 || BonusBlockLimitReached(blocksToGoBack))
                    {
                        blocksToGoBack--;
                    }
                }
                else
                {
                    ThrowBlockNotFoundException(currentBlockNumber);
                }
                currentBlockNumber--;
            }
        }

        private Block? GetHeadBlock()
        {
            return _blockFinder!.FindHeadBlock();
        }

        private static bool MoreBlocksToGoBack(long blocksToGoBack)
        {
            return blocksToGoBack > 0;
        }
        
        private static bool CurrentBlockNumberIsValid(long currBlockNumber)
        {
            return currBlockNumber > -1;
        }
        
        private Block? FindBlockAtNumber(long blockNumber)
        {
            return _blockFinder!.FindBlock(blockNumber);
        }
        
        private static bool BlockExists(Block? foundBlock)
        {
            return foundBlock != null;
        }

        private bool BonusBlockLimitReached(int blocksToGoBack)
        {
            return TxGasPriceList.Count + blocksToGoBack >= _softTxThreshold;
        }
        
        private void ThrowBlockNotFoundException(long blockNumber)
        {
            throw new Exception($"Block {blockNumber} was not found.");
        }


        private UInt256? GasPriceAtPercentile()
        {
            int roundedIndex = GetRoundedIndexAtPercentile(TxGasPriceList.Count);

            UInt256? gasPriceEstimate = GetElementAtIndex(TxGasPriceList, roundedIndex);

            return gasPriceEstimate;
        }
        
        private static UInt256 GetElementAtIndex(List<UInt256> txGasPriceList, int roundedIndex)
        {
            return txGasPriceList[roundedIndex];
        }
        
        private static UInt256? FindMinOfThisAndMaxPrice(UInt256? gasPriceEstimate)
        {
            if (gasPriceEstimate > GasPriceConfig._maxGasPrice)
            {
                gasPriceEstimate = GasPriceConfig._maxGasPrice;
            }

            return gasPriceEstimate;
        }

        private void SetLastGasPrice(UInt256? lastGasPrice)
        {
            _lastGasPrice = lastGasPrice;
        }
        
        private static int GetRoundedIndexAtPercentile(int count)
        {
            int lastIndex = count - 1;
            float percentileOfLastIndex = lastIndex * ((float)GasPriceConfig.Percentile / 100);
            int roundedIndex = (int) Math.Round(percentileOfLastIndex);
            return roundedIndex;
        }

        private void SetLastHeadBlock(Block? headBlock)
        {
            _lastHeadBlock = headBlock;
        }

        private static Tuple<bool, ResultWrapper<UInt256?>> BoolAndWrapperTuple(bool boolean, ResultWrapper<UInt256?> resultWrapper)
        {
            return new(boolean, resultWrapper);
        }

        private Block? GetGenesisBlock()
        {
            return _blockFinder!.FindGenesisBlock();
        }

        private static bool ResultWrapperWasSuccessful(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result == Result.Success;
        }
        
        private static bool ResultWrapperWasNotSuccessful(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result != Result.Success;
        }
        
    }
}
