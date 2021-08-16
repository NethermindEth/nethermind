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
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice
{
    public class GasPriceOracle : IGasPriceOracle
    {
        private readonly IBlockFinder _blockFinder;
        public UInt256 FallbackGasPrice => LastGasPrice ?? EthGasPriceConstants.DefaultGasPrice;
        private ISpecProvider SpecProvider { get; }
        public UInt256? LastGasPrice { get; set; }
        public Block? LastHeadBlock { get; set; }
        public UInt256 IgnoreUnder { get; set; } = EthGasPriceConstants.DefaultIgnoreUnder;
        public int BlockLimit { get; set; } = EthGasPriceConstants.DefaultBlocksLimit;
        private int SoftTxThreshold => BlockLimit * 2;

        public GasPriceOracle(
            IBlockFinder blockFinder,
            ISpecProvider specProvider)
        {
            _blockFinder = blockFinder;
            SpecProvider = specProvider;
        }

        public UInt256 GetGasPriceEstimate()
        {
            Block? headBlock = _blockFinder.Head;
            if (headBlock is null)
            {
                return FallbackGasPrice;
            }
            
            if (LastGasPrice is not null && LastHeadBlock!.Hash == headBlock!.Hash)
            {
                return LastGasPrice.Value;
            }

            LastHeadBlock = headBlock;
            IEnumerable<UInt256> txGasPrices = GetSortedGasPricesFromRecentBlocks(headBlock.Number);
            UInt256? gasPriceEstimate = GetGasPriceAtPercentile(txGasPrices.ToList());
            gasPriceEstimate = UInt256.Min((UInt256) gasPriceEstimate!, EthGasPriceConstants.MaxGasPrice);
            LastGasPrice = gasPriceEstimate;
            return (UInt256) gasPriceEstimate!;
        }
        private IEnumerable<UInt256> GetSortedGasPricesFromRecentBlocks(long blockNumber) 
            => GetGasPricesFromRecentBlocks(blockNumber).OrderBy(gasPrice => gasPrice);

        public IEnumerable<UInt256> GetGasPricesFromRecentBlocks(long blockNumber)
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
        
        private IEnumerable<UInt256> GetGasPricesFromRecentBlocks(IEnumerable<Block> blocks, int blocksToGoBack)
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
                        .Select(tx => tx.CalculateEffectiveGasPrice(eip1559Enabled, baseFee))
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
                    yield return FallbackGasPrice;
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
        
        private static UInt256 GetGasPriceAtPercentile(List<UInt256> txGasPriceList)
        {
            int roundedIndex = GetRoundedIndexAtPercentile(txGasPriceList.Count);
            
            return roundedIndex < 0 
                ? EthGasPriceConstants.DefaultGasPrice 
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
