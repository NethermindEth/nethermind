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
        private Block? _lastHeadBlock;
        private EarlyExitManager _earlyExitManager;
        private bool _eip1559Enabled;
        private readonly UInt256? _ignoreUnder;
        private readonly int _blockLimit;
        private readonly int _softTxThreshold;
        private readonly UInt256 _baseFee;
        private readonly TxInsertionManager _txInsertionManager;

        public GasPriceOracle(bool eip1559Enabled = false, UInt256? ignoreUnder = null, 
            int? blockLimit = null, UInt256? baseFee = null)
        {
            TxGasPriceList = new List<UInt256>();
            _eip1559Enabled = eip1559Enabled;
            _ignoreUnder = ignoreUnder ?? UInt256.Zero;
            _blockLimit = blockLimit ?? GasPriceConfig.DefaultBlocksLimit;
            _softTxThreshold = GasPriceConfig.SoftTxLimit;
            _baseFee = baseFee ?? GasPriceConfig.DefaultBaseFee;
            _txInsertionManager = new TxInsertionManager(this, _ignoreUnder, _eip1559Enabled, _baseFee);
            _earlyExitManager = new EarlyExitManager();
        }

        public ResultWrapper<UInt256?> GasPriceEstimate(Block? headBlock, Dictionary<long, Block> blockNumToBlockMap)
        {
            Tuple<bool, ResultWrapper<UInt256?>> headChangedResult;
            headChangedResult = _earlyExitManager.CheckChangeInHeadBlock(ref _lastHeadBlock, headBlock, LastGasPrice);
            if (HeadBlockDidNotChange(headChangedResult))
            {
                return headChangedResult.Item2;
            }
            
            SetDefaultGasPrice();
            
            AddTxGasPricesToList(headBlock, blockNumToBlockMap);
            
            TxGasPriceList = TxGasPriceList.OrderBy(gasPrice => gasPrice).ToList();
            
            UInt256? gasPriceEstimate = GasPriceAtPercentile();

            gasPriceEstimate = UInt256.Min((UInt256) gasPriceEstimate!, GasPriceConfig._maxGasPrice);

            LastGasPrice = gasPriceEstimate;
            
            return ResultWrapper<UInt256?>.Success((UInt256) gasPriceEstimate!);
        }

        private static bool HeadBlockDidNotChange(Tuple<bool, ResultWrapper<UInt256?>> earlyExitResult)
        {
            return earlyExitResult.Item1 == false;
        }

        private void SetDefaultGasPrice()
        {
            DefaultGasPrice = LastGasPrice ?? GasPriceConfig.DefaultGasPrice;
        }
        
        private void AddTxGasPricesToList(Block? headBlock, Dictionary<long, Block> blockNumToBlockMap)
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
