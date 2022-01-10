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
        private UInt256 FallbackGasPrice(in UInt256? baseFeePerGas = null) => LastGasPrice ?? GetMinimumGasPrice(baseFeePerGas ?? UInt256.Zero);
        private ISpecProvider SpecProvider { get; }
        internal UInt256? LastGasPrice { get; set; }
        internal Block? LastHeadBlock { get; set; }
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
            
            if (LastGasPrice is not null && LastHeadBlock!.Hash == headBlock!.Hash)
            {
                return LastGasPrice.Value;
            }

            LastHeadBlock = headBlock;
            IEnumerable<UInt256> txGasPrices = GetSortedGasPricesFromRecentBlocks(headBlock.Number).Select(tuple => tuple.Item1);
            UInt256 gasPriceEstimate = GetGasPriceAtPercentile(txGasPrices.ToList()) ?? GetMinimumGasPrice(headBlock.BaseFeePerGas);
            gasPriceEstimate = UInt256.Min(gasPriceEstimate!, EthGasPriceConstants.MaxGasPrice);
            LastGasPrice = gasPriceEstimate;
            return gasPriceEstimate!;
        }
        
        public UInt256 GetGasPriceEstimateWithBaseFee()
        {
            Block? headBlock = _blockFinder.Head;
            if (headBlock is null)
            {
                return FallbackGasPrice();
            }
            
            if (LastGasPrice is not null && LastHeadBlock!.Hash == headBlock!.Hash)
            {
                return LastGasPrice.Value;
            }

            LastHeadBlock = headBlock;
            IEnumerable<UInt256> gasPricesWithFee = GetSortedGasPricesWithFeeFromRecentBlocks(headBlock.Number);
            UInt256 gasPriceEstimate = GetGasPriceWithBaseFeeAtPercentile(gasPricesWithFee.ToList()) ?? LastGasPrice ?? GetMinimumGasPrice(headBlock.BaseFeePerGas);
            gasPriceEstimate = UInt256.Min(gasPriceEstimate!, EthGasPriceConstants.MaxGasPrice);
            LastGasPrice = gasPriceEstimate;
            return gasPriceEstimate!;
        }

        private UInt256 GetMinimumGasPrice(in UInt256 baseFeePerGas) => (_minGasPrice + baseFeePerGas) * _defaultMinGasPriceMultiplier / 100ul;

        private IEnumerable<Tuple<UInt256, UInt256>>GetSortedGasPricesFromRecentBlocks(long blockNumber) 
            => GetGasPricesFromRecentBlocks(blockNumber).OrderBy(gasPrice => gasPrice);
            
        private IEnumerable<UInt256>GetSortedGasPricesWithFeeFromRecentBlocks(long blockNumber) 
            => GetGasPricesFromRecentBlocks2(blockNumber).OrderBy(gasPrice=> gasPrice);

        internal IEnumerable<Tuple<UInt256, UInt256>> GetGasPricesFromRecentBlocks(long blockNumber)
        {
            IEnumerable<Block> GetBlocks(long currentBlockNumber)
            {
                while (currentBlockNumber >= 0)
                {
                    yield return _blockFinder.FindBlock(currentBlockNumber)!;
                    currentBlockNumber--;
                }
            }
            
            return GetGasPricesFromRecentBlocks(GetBlocks(blockNumber), BlockLimit);
        }
        
        internal IEnumerable<UInt256> GetGasPricesFromRecentBlocks2(long blockNumber)
        {
            IEnumerable<Block> GetBlocks(long currentBlockNumber)
            {
                for(int i = 0; i < 100; ++i)
                {
                    yield return _blockFinder.FindBlock(currentBlockNumber - i)!;
                }
            }
            
            return GetGasPricesFromRecentBlocks2(GetBlocks(blockNumber), BlockLimit);
        }
        
        private IEnumerable<Tuple<UInt256, UInt256>> GetGasPricesFromRecentBlocks(IEnumerable<Block> blocks, int blocksToGoBack)
        {
            int txCount = 0;
            
            foreach (Block currentBlock in blocks)
            {
                Transaction[] currentBlockTransactions = currentBlock.Transactions;
                int txFromCurrentBlock = 0;
                bool eip1559Enabled = SpecProvider.GetSpec(currentBlock.Number).IsEip1559Enabled;

                UInt256 baseFee = currentBlock.BaseFeePerGas;
                IEnumerable<UInt256> effectiveGasPrices = 
                    currentBlockTransactions.Where(tx => tx.SenderAddress != currentBlock.Beneficiary)
                        .Where(tx => tx.GasPrice > 0)
                        .Select(tx => tx.CalculateEffectiveGasPrice(eip1559Enabled, baseFee))
                        .Where(g => g >= IgnoreUnder)
                        .OrderBy(g => g);

                foreach (UInt256 gasPrice in effectiveGasPrices)
                {
                    yield return new Tuple<UInt256, UInt256>(gasPrice, baseFee);
                    txFromCurrentBlock++;
                    txCount++;

                    if (txFromCurrentBlock >= EthGasPriceConstants.TxLimitFromABlock)
                    {
                        break;
                    }
                }

                if (txFromCurrentBlock == 0)
                {
                    yield return new Tuple<UInt256, UInt256>(FallbackGasPrice(currentBlock.BaseFeePerGas), baseFee);
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
        
        private IEnumerable<UInt256> GetGasPricesFromRecentBlocks2(IEnumerable<Block> blocks, int blocksToGoBack)
        {
            int txCount = 0;
            
            foreach (Block currentBlock in blocks)
            {
                Transaction[] currentBlockTransactions = currentBlock.Transactions;
                int txFromCurrentBlock = 0;
                bool eip1559Enabled = SpecProvider.GetSpec(currentBlock.Number).IsEip1559Enabled;

                
                UInt256 baseFee = currentBlock.BaseFeePerGas;
                IEnumerable<UInt256> effectiveGasPrices = 
                    currentBlockTransactions.Where(tx => tx.SenderAddress != currentBlock.Beneficiary)
                        .Where(tx => tx.GasPrice > 0)
                        .Select(tx => tx.CalculateEffectiveGasPrice2(eip1559Enabled, baseFee))
                        .Where(g => g >= IgnoreUnder)
                        .OrderBy(g => g);

                foreach (UInt256 gasPrice in effectiveGasPrices)
                {
                    yield return gasPrice;
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

        private UInt256? GetGasPriceWithBaseFeeAtPercentile(List<UInt256> txGasPriceList)
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
    }
}
