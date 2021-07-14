using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class GasPriceOracle : IGasPriceOracle
    {
        public UInt256? FallbackGasPrice { get; private set; }
        public List<UInt256> TxGasPriceList { get; private set; }
        public ISpecProvider SpecProvider { get; private set; }
        protected UInt256? LastGasPrice { get; private set; }
        private Block? LastHeadBlock { get; set; }
        private readonly UInt256? _ignoreUnder;
        private readonly int _blockLimit;
        private readonly int _softTxThreshold;
        private readonly UInt256 _baseFee;
        private readonly ITxInsertionManager _txInsertionManager;
        private readonly IHeadBlockChangeManager _headBlockChangeManager;

        public GasPriceOracle(
            ISpecProvider specProvider,
            UInt256? ignoreUnder = null, 
            int? blockLimit = null, 
            UInt256? baseFee = null,
            ITxInsertionManager? txInsertionManager = null,
            IHeadBlockChangeManager? headBlockChangeManager = null)
        {
            TxGasPriceList = new List<UInt256>();
            SpecProvider = specProvider;
            _ignoreUnder = ignoreUnder ?? UInt256.Zero;
            _blockLimit = blockLimit ?? EthGasPriceConstants.DefaultBlocksLimit;
            _softTxThreshold = (int) (blockLimit != null ? blockLimit * 2 : EthGasPriceConstants.SoftTxLimit);
            _baseFee = baseFee ?? EthGasPriceConstants.DefaultBaseFee;
            _txInsertionManager = txInsertionManager ?? new GasPriceEstimateTxInsertionManager(this, _ignoreUnder, _baseFee, specProvider);
            _headBlockChangeManager = headBlockChangeManager ?? new GasPriceEstimateHeadBlockChangeManager();
        }

        public ResultWrapper<UInt256?> GasPriceEstimate(Block? headBlock, IDictionary<long, Block> blockNumToBlockMap)
        {
            LastGasPrice = GetLastGasPrice();
            bool shouldReturnSameGasPrice = _headBlockChangeManager.ShouldReturnSameGasPrice( LastHeadBlock, headBlock, LastGasPrice);
            if (shouldReturnSameGasPrice)
            {
                return ResultWrapper<UInt256?>.Success(LastGasPrice);
            }

            LastHeadBlock = headBlock;
            TxGasPriceList = CreateSortedTxGasPriceList(headBlock, blockNumToBlockMap);
            
            UInt256? gasPriceEstimate = GetGasPriceAtPercentile(TxGasPriceList);
            gasPriceEstimate = UInt256.Min((UInt256) gasPriceEstimate!, EthGasPriceConstants._maxGasPrice);
            LastGasPrice = gasPriceEstimate;
            return ResultWrapper<UInt256?>.Success((UInt256) gasPriceEstimate!);
        }

        protected virtual UInt256? GetLastGasPrice()
        {
            return LastGasPrice;
        }
        
        private List<UInt256> CreateSortedTxGasPriceList(Block? headBlock, IDictionary<long, Block> blockNumToBlockMap)
        {
            FallbackGasPrice = LastGasPrice ?? EthGasPriceConstants.DefaultGasPrice;

            return GetSortedTxGasPriceList(headBlock, blockNumToBlockMap);
        }

        protected virtual List<UInt256> GetSortedTxGasPriceList(Block? headBlock, IDictionary<long, Block> blockNumToBlockMap)
        {
            AddTxGasPricesToList(headBlock, blockNumToBlockMap);

            return TxGasPriceList.OrderBy(gasPrice => gasPrice).ToList();
        }

        private void AddTxGasPricesToList(Block? headBlock, IDictionary<long, Block> blockNumToBlockMap)
        {
            long currentBlockNumber = headBlock!.Number;
            int blocksToGoBack = _blockLimit;
            while (blocksToGoBack > 0 && currentBlockNumber > -1) 
            {
                int txsAdded = _txInsertionManager.AddValidTxFromBlockAndReturnCount(blockNumToBlockMap[currentBlockNumber]);
                if (txsAdded > 1 || TxGasPriceList.Count + blocksToGoBack >= _softTxThreshold)
                {
                    blocksToGoBack--;
                }
                
                currentBlockNumber--;
            }
        }
        
        private static UInt256? GetGasPriceAtPercentile(List<UInt256> txGasPriceList)
        {
            int roundedIndex = GetRoundedIndexAtPercentile(txGasPriceList.Count);
            
            if (roundedIndex < 0)
            {
                return EthGasPriceConstants.DefaultGasPrice;
            }
            
            UInt256? gasPriceEstimate = txGasPriceList[roundedIndex];

            return gasPriceEstimate;
        }

        private static int GetRoundedIndexAtPercentile(int count)
        {
            int lastIndex = count - 1;
            float percentileOfLastIndex = lastIndex * ((float)EthGasPriceConstants.PercentileOfSortedTxs / 100);
            int roundedIndex = (int) Math.Round(percentileOfLastIndex);
            return roundedIndex;
        }
    }
}
