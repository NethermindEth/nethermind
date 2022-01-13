//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice
{
    public class GasPriceOracle : IGasPriceOracle
    {
        private readonly IBlockFinder _blockFinder;
        private readonly UInt256 _minGasPrice;
        internal GasPriceEstimationCache _gasPriceEstimation;
        private MaxPriorityFeePerGasEstimationCache _maxPriorityFeePerGasEstimation;
        private UInt256 FallbackGasPrice(in UInt256? baseFeePerGas = null) => _gasPriceEstimation.LastGasPrice ?? GetMinimumGasPrice(baseFeePerGas ?? UInt256.Zero); 
        private ISpecProvider SpecProvider { get; }
        internal UInt256 IgnoreUnder { get; set; } = EthGasPriceConstants.DefaultIgnoreUnder;
        internal int BlockLimit { get; set; } = EthGasPriceConstants.DefaultBlocksLimit;
        private int SoftTxThreshold => BlockLimit * 2;
        private readonly UInt256 _defaultMinGasPriceMultiplier = 110;

        public GasPriceOracle(
            IBlockFinder blockFinder,
            ISpecProvider specProvider,
            UInt256? minGasPrice = null)
        {
            _blockFinder = blockFinder;
            _minGasPrice = minGasPrice ?? new MiningConfig().MinGasPrice;
            SpecProvider = specProvider;
        }

        public UInt256 GetGasPriceEstimate()
        {
            Block? headBlock = _blockFinder.Head;
            if (headBlock is null)
            {
                return FallbackGasPrice();
            }
            
            if (_gasPriceEstimation.LastGasPrice is not null && _gasPriceEstimation.LastHeadBlock!.Hash == headBlock!.Hash)
            {
                return _gasPriceEstimation.LastGasPrice.Value;
            }

            _gasPriceEstimation.LastHeadBlock = headBlock;
            IEnumerable<UInt256> txGasPrices = GetSortedGasPricesFromRecentBlocks(headBlock.Number);
            UInt256 gasPriceEstimate = GetGasPriceAtPercentile(txGasPrices.ToList()) ?? GetMinimumGasPrice(headBlock.BaseFeePerGas);
            gasPriceEstimate = UInt256.Min(gasPriceEstimate!, EthGasPriceConstants.MaxGasPrice);
            _gasPriceEstimation.LastGasPrice = gasPriceEstimate;
            return gasPriceEstimate!;
        }
        
        public UInt256 GetMaxPriorityGasFeeEstimate()
        {
            Block? headBlock = _blockFinder.Head;
            if (headBlock is null)
            {
                return EthGasPriceConstants.FallbackMaxPriorityFeePerGas;;
            }
            
            if (_maxPriorityFeePerGasEstimation.MaxPriorityFeePerGas is not null && _maxPriorityFeePerGasEstimation.LastHeadBlock!.Hash == headBlock!.Hash)
            {
                return _maxPriorityFeePerGasEstimation.MaxPriorityFeePerGas.Value;
            }

            _maxPriorityFeePerGasEstimation.LastHeadBlock = headBlock;
            IEnumerable<UInt256> gasPricesWithFee = GetSortedMaxPriorityFeePerGasFromRecentBlocks(headBlock.Number);
            UInt256 gasPriceEstimate = GetGasPriceAtPercentile(gasPricesWithFee.ToList()) ?? _maxPriorityFeePerGasEstimation.MaxPriorityFeePerGas ?? GetMinimumGasPrice(headBlock.BaseFeePerGas);
            gasPriceEstimate = UInt256.Min(gasPriceEstimate!, EthGasPriceConstants.MaxGasPrice);
            _maxPriorityFeePerGasEstimation.MaxPriorityFeePerGas = gasPriceEstimate;
            return gasPriceEstimate!;
        }

        private UInt256 GetMinimumGasPrice(in UInt256 baseFeePerGas) => (_minGasPrice + baseFeePerGas) * _defaultMinGasPriceMultiplier / 100ul;

        internal IEnumerable<UInt256>GetSortedGasPricesFromRecentBlocks(long blockNumber) 
            => GetGasPricesFromRecentBlocks(blockNumber, BlockLimit, (t, e, b) => t.CalculateEffectiveGasPrice(e, b))
                .OrderBy(gasPrice => gasPrice);
            
        private IEnumerable<UInt256>GetSortedMaxPriorityFeePerGasFromRecentBlocks(long blockNumber) 
            => GetGasPricesFromRecentBlocks(blockNumber, EthGasPriceConstants.DefaultBlocksLimitMaxPriorityFeePerGas, (t, e, b) => t.CalculateMaxPriorityFeePerGas(e, b))
                .OrderBy(gasPrice => gasPrice);

        internal IEnumerable<UInt256> GetGasPricesFromRecentBlocks(long blockNumber, int numberOfBlocks, Func<Transaction, bool, UInt256, UInt256> func)
        {
            IEnumerable<Block> GetBlocks(long currentBlockNumber)
            {
                while (currentBlockNumber >= 0)
                {
                    yield return _blockFinder.FindBlock(currentBlockNumber)!;
                    currentBlockNumber--;
                }
            }
            
            return GetGasPricesFromRecentBlocks(GetBlocks(blockNumber), numberOfBlocks, func);
        }

        private IEnumerable<UInt256> GetGasPricesFromRecentBlocks(IEnumerable<Block> blocks, int blocksToGoBack, Func<Transaction, bool, UInt256, UInt256> getGasEstimateFromTransaction)
        {
            int txCount = 0;
            
            foreach (Block currentBlock in blocks)
            {
                Transaction[] currentBlockTransactions = currentBlock.Transactions;
                int txFromCurrentBlock = 0;
                bool eip1559Enabled = SpecProvider.GetSpec(currentBlock.Number).IsEip1559Enabled;
                UInt256 baseFee = currentBlock.BaseFeePerGas;
                IEnumerable<UInt256> effectiveGasPrices = currentBlockTransactions.Where(tx => tx.SenderAddress != currentBlock.Beneficiary)
                        .Select(tx => getGasEstimateFromTransaction(tx, eip1559Enabled, baseFee))
                        .Where(g => g >= IgnoreUnder)
                        .OrderBy(g => g);
    
                foreach (UInt256 gasPrice in effectiveGasPrices)
                {
                    yield return gasPrice;
                    txFromCurrentBlock++;
                    txCount++;

                    if (txFromCurrentBlock >= EthGasPriceConstants.TxLimitFromABlock)
                    {
                        break;
                    }
                }

                if (txFromCurrentBlock == 0)
                {
                    yield return FallbackGasPrice(currentBlock.BaseFeePerGas);
                }

                if (txFromCurrentBlock > 1 || txCount + blocksToGoBack >= SoftTxThreshold)
                {
                    blocksToGoBack--;
                }

                if (blocksToGoBack < 1)
                {
                    break;
                }
            }
        }

        private UInt256? GetGasPriceAtPercentile(List<UInt256> txGasPriceList)
        {
            int roundedIndex = GetRoundedIndexAtPercentile(txGasPriceList.Count);
            
            return roundedIndex < 0 
                ? null 
                : txGasPriceList[roundedIndex];
        }

        private static int GetRoundedIndexAtPercentile(int count)
        {
            int lastIndex = count - 1;
            float percentileOfLastIndex = lastIndex * ((float)EthGasPriceConstants.PercentileOfSortedTxs / 100);
            int roundedIndex = (int) Math.Round(percentileOfLastIndex);
            return roundedIndex;
        }

        internal struct GasPriceEstimationCache
        {
            public UInt256? LastGasPrice { get; set; }
            public Block? LastHeadBlock { get; set; } 
            
            public GasPriceEstimationCache(UInt256? lastGasPrice, Block? lastHeadBlock)
            {
                LastGasPrice = lastGasPrice;
                LastHeadBlock = lastHeadBlock;
            }
            
            public GasPriceEstimationCache(UInt256? lastGasPrice)
            {
                LastGasPrice = lastGasPrice;
                LastHeadBlock = null;
            }
        }
        
        internal struct MaxPriorityFeePerGasEstimationCache
        {
            public UInt256? MaxPriorityFeePerGas { get; set; }
            public Block? LastHeadBlock { get; set; } 
            
            public MaxPriorityFeePerGasEstimationCache(UInt256? lastGasPrice, Block? lastHeadBlock)
            {
                MaxPriorityFeePerGas = lastGasPrice;
                LastHeadBlock = lastHeadBlock;
            }
            
            public MaxPriorityFeePerGasEstimationCache(UInt256? lastGasPrice)
            {
                MaxPriorityFeePerGas = lastGasPrice;
                LastHeadBlock = null;
            }
        }
    }
}
