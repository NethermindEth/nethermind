using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class GasPriceOracle : IGasPriceOracle
    {
        public UInt256? FallbackGasPrice { get; private set; }
        public List<UInt256> TxGasPriceList { get; private set; }
        private ISpecProvider SpecProvider { get; }
        private UInt256? LastGasPrice { get; set; }
        private Block? LastHeadBlock { get; set; }
        private UInt256? IgnoreUnder => GetIgnoreUnder();
        private int BlockLimit => GetBlockLimit();
        private int SoftTxThreshold => GetBlockLimit() * 2;
        private readonly ITxInsertionManager _txInsertionManager;

        public GasPriceOracle(
            ISpecProvider specProvider, 
            ITxInsertionManager? txInsertionManager = null)
        {
            TxGasPriceList = new List<UInt256>();
            SpecProvider = specProvider;
            _txInsertionManager = txInsertionManager ?? new GasPriceEstimateTxInsertionManager(this, IgnoreUnder, specProvider);
        }

        protected internal virtual UInt256 GetIgnoreUnder()
        {
            return UInt256.Zero;
        }

        protected internal virtual int GetBlockLimit()
        {
            return EthGasPriceConstants.DefaultBlocksLimit;
        }
        public ResultWrapper<UInt256?> GasPriceEstimate(Block? headBlock, IBlockFinder blockFinder)
        {
            LastGasPrice = GetLastGasPrice();
            bool shouldReturnSameGasPrice = ShouldReturnSameGasPrice( LastHeadBlock, headBlock, LastGasPrice);
            if (shouldReturnSameGasPrice)
            {
                return ResultWrapper<UInt256?>.Success(LastGasPrice);
            }

            LastHeadBlock = headBlock;
            TxGasPriceList = CreateSortedTxGasPriceList(headBlock, blockFinder);
            
            UInt256? gasPriceEstimate = GetGasPriceAtPercentile(TxGasPriceList);
            gasPriceEstimate = UInt256.Min((UInt256) gasPriceEstimate!, EthGasPriceConstants._maxGasPrice);
            LastGasPrice = gasPriceEstimate;
            return ResultWrapper<UInt256?>.Success((UInt256) gasPriceEstimate!);
        }

        protected internal virtual UInt256? GetLastGasPrice()
        {
            return LastGasPrice;
        }
        
        public virtual bool ShouldReturnSameGasPrice(Block? lastHead, Block? currentHead, UInt256? lastGasPrice)
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
        
        private List<UInt256> CreateSortedTxGasPriceList(Block? headBlock, IBlockFinder blockFinder)
        {
            FallbackGasPrice = LastGasPrice ?? EthGasPriceConstants.DefaultGasPrice;

            return GetSortedTxGasPriceList(headBlock, blockFinder);
        }

        protected internal virtual List<UInt256> GetSortedTxGasPriceList(Block? headBlock, IBlockFinder blockFinder)
        {
            AddTxGasPricesToList(headBlock, blockFinder);

            return TxGasPriceList.OrderBy(gasPrice => gasPrice).ToList();
        }

        private void AddTxGasPricesToList(Block? headBlock, IBlockFinder blockFinder)
        {
            long currentBlockNumber = headBlock!.Number;
            int blocksToGoBack = BlockLimit;
            int softTxThreshold = SoftTxThreshold;
            int txGasPriceListCount = 0;
            while (blocksToGoBack > 0 && currentBlockNumber > -1) 
            {
                int txsAdded = _txInsertionManager.AddValidTxFromBlockAndReturnCount(blockFinder.FindBlock(currentBlockNumber));
                txGasPriceListCount += txsAdded;
                if (txsAdded > 1 || txGasPriceListCount + blocksToGoBack >= softTxThreshold)
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
