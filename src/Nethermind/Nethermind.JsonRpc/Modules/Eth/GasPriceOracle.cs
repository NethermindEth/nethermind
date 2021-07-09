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
        private UInt256? LastGasPrice { get; set; }
        private Block? LastHeadBlock;
        private readonly bool _eip1559Enabled;
        private readonly UInt256? _ignoreUnder;
        private readonly int _blockLimit;
        private readonly int _softTxThreshold;
        private readonly UInt256 _baseFee;
        private readonly ITxInsertionManager _txInsertionManager;
        private readonly IHeadBlockChangeManager _headBlockChangeManager;

        public GasPriceOracle(
            bool eip1559Enabled = false, 
            UInt256? ignoreUnder = null, 
            int? blockLimit = null, 
            UInt256? baseFee = null, 
            ITxInsertionManager? txInsertionManager = null,
            IHeadBlockChangeManager? headBlockChangeManager = null)
        {
            TxGasPriceList = new List<UInt256>();
            _eip1559Enabled = eip1559Enabled;
            _ignoreUnder = ignoreUnder ?? UInt256.Zero;
            _blockLimit = blockLimit ?? GasPriceConfig.DefaultBlocksLimit;
            _softTxThreshold = GasPriceConfig.SoftTxLimit;
            _baseFee = baseFee ?? GasPriceConfig.DefaultBaseFee;
            _txInsertionManager = txInsertionManager ?? new TxInsertionManager(this, _ignoreUnder, _eip1559Enabled, _baseFee);
            _headBlockChangeManager = headBlockChangeManager ?? new HeadBlockChangeManager();
        }

        public ResultWrapper<UInt256?> GasPriceEstimate(Block? headBlock, IDictionary<long, Block> blockNumToBlockMap)
        {
            bool shouldReturnSameGasPrice;
            shouldReturnSameGasPrice = _headBlockChangeManager.ReturnSameGasPrice( LastHeadBlock, headBlock, LastGasPrice);
            if (shouldReturnSameGasPrice)
            {
                return NoHeadBlockChangeResultWrapper(LastGasPrice);
            }

            LastHeadBlock = headBlock;

            SetDefaultGasPrice();
            
            AddTxGasPricesToList(headBlock, blockNumToBlockMap);
            
            TxGasPriceList = TxGasPriceList.OrderBy(gasPrice => gasPrice).ToList();
            
            UInt256? gasPriceEstimate = GasPriceAtPercentile();

            gasPriceEstimate = UInt256.Min((UInt256) gasPriceEstimate!, GasPriceConfig._maxGasPrice);

            LastGasPrice = gasPriceEstimate;
            
            return ResultWrapper<UInt256?>.Success((UInt256) gasPriceEstimate!);
        }

        private ResultWrapper<UInt256?> NoHeadBlockChangeResultWrapper(UInt256? lastGasPrice)
        {
            ResultWrapper<UInt256?> resultWrapper = ResultWrapper<UInt256?>.Success(lastGasPrice);
            resultWrapper.ErrorCode = GasPriceConfig.NoHeadBlockChangeErrorCode;
            return resultWrapper;
        }

        private static bool HeadBlockDidNotChange(Tuple<bool, ResultWrapper<UInt256?>> earlyExitResult)
        {
            return earlyExitResult.Item1 == false;
        }

        private void SetDefaultGasPrice()
        {
            DefaultGasPrice = LastGasPrice ?? GasPriceConfig.DefaultGasPrice;
        }
        
        private void AddTxGasPricesToList(Block? headBlock, IDictionary<long, Block> blockNumToBlockMap)
        {
            long currentBlockNumber = headBlock!.Number;
            int blocksToGoBack = _blockLimit;
            while (MoreBlocksToGoBack(blocksToGoBack) && CurrentBlockNumberIsValid(currentBlockNumber)) 
            {
                int txsAdded = _txInsertionManager.AddValidTxAndReturnCount(blockNumToBlockMap[currentBlockNumber]);
                if (txsAdded > 1 || BonusBlockLimitReached(blocksToGoBack))
                {
                    blocksToGoBack--;
                }
                
                currentBlockNumber--;
            }
        }

        private static bool MoreBlocksToGoBack(long blocksToGoBack)
        {
            return blocksToGoBack > 0;
        }
        
        private static bool CurrentBlockNumberIsValid(long currBlockNumber)
        {
            return currBlockNumber > -1;
        }

        private bool BonusBlockLimitReached(int blocksToGoBack)
        {
            return TxGasPriceList.Count + blocksToGoBack >= _softTxThreshold;
        }


        private UInt256? GasPriceAtPercentile()
        {
            int roundedIndex = GetRoundedIndexAtPercentile(TxGasPriceList.Count);

            UInt256? gasPriceEstimate = TxGasPriceList[roundedIndex];

            return gasPriceEstimate;
        }

        private static int GetRoundedIndexAtPercentile(int count)
        {
            int lastIndex = count - 1;
            float percentileOfLastIndex = lastIndex * ((float)GasPriceConfig.PercentileOfSortedTxs / 100);
            int roundedIndex = (int) Math.Round(percentileOfLastIndex);
            return roundedIndex;
        }
    }
}
