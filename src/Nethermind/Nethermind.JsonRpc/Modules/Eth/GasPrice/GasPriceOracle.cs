// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice
{
    public class GasPriceOracle : IGasPriceOracle
    {
        private readonly IBlockFinder _blockFinder;
        private readonly ILogger _logger;
        private readonly UInt256 _minGasPrice;
        internal PriceCache _gasPriceEstimation;
        internal PriceCache _maxPriorityFeePerGasEstimation;
        private UInt256 FallbackGasPrice(in UInt256? baseFeePerGas = null) => _gasPriceEstimation.LastPrice ?? GetMinimumGasPrice(baseFeePerGas ?? UInt256.Zero);
        private ISpecProvider SpecProvider { get; }
        internal UInt256 IgnoreUnder { get; init; } = EthGasPriceConstants.DefaultIgnoreUnder;
        internal int BlockLimit { get; init; } = EthGasPriceConstants.DefaultBlocksLimit;
        private int SoftTxThreshold => BlockLimit * 2;
        private readonly UInt256 _defaultMinGasPriceMultiplier = 110;

        public GasPriceOracle(
            IBlockFinder blockFinder,
            ISpecProvider specProvider,
            ILogManager logManager,
            UInt256? minGasPrice = null)
        {
            _blockFinder = blockFinder;
            _logger = logManager.GetClassLogger();
            _minGasPrice = minGasPrice ?? new BlocksConfig().MinGasPrice;
            SpecProvider = specProvider;
        }

        public UInt256 GetGasPriceEstimate()
        {
            Block? headBlock = _blockFinder.Head;
            if (headBlock is null)
            {
                return FallbackGasPrice();
            }

            Keccak headBlockHash = headBlock.Hash!;
            if (_gasPriceEstimation.TryGetPrice(headBlockHash, out UInt256? price))
            {
                return price!.Value;
            }

            IEnumerable<UInt256> txGasPrices = GetSortedGasPricesFromRecentBlocks(headBlock.Number);
            UInt256 gasPriceEstimate = GetGasPriceAtPercentile(txGasPrices.ToList()) ?? GetMinimumGasPrice(headBlock.BaseFeePerGas);
            gasPriceEstimate = UInt256.Min(gasPriceEstimate!, EthGasPriceConstants.MaxGasPrice);
            _gasPriceEstimation.Set(headBlockHash, gasPriceEstimate);
            return gasPriceEstimate!;
        }

        internal IEnumerable<UInt256> GetSortedGasPricesFromRecentBlocks(long blockNumber) =>
            GetGasPricesFromRecentBlocks(blockNumber, BlockLimit,
            (transaction, eip1559Enabled, baseFee) => transaction.CalculateEffectiveGasPrice(eip1559Enabled, baseFee));

        public UInt256 GetMaxPriorityGasFeeEstimate()
        {
            Block? headBlock = _blockFinder.Head;
            if (headBlock is null)
            {
                return EthGasPriceConstants.FallbackMaxPriorityFeePerGas;
            }

            Keccak headBlockHash = headBlock.Hash!;
            if (_maxPriorityFeePerGasEstimation.TryGetPrice(headBlockHash, out UInt256? price))
            {
                return price!.Value;
            }

            IEnumerable<UInt256> gasPricesWithFee = GetGasPricesFromRecentBlocks(headBlock.Number,
                EthGasPriceConstants.DefaultBlocksLimitMaxPriorityFeePerGas,
                (transaction, eip1559Enabled, baseFee) => transaction.CalculateMaxPriorityFeePerGas(eip1559Enabled, baseFee));

            UInt256 gasPriceEstimate = GetGasPriceAtPercentile(gasPricesWithFee.ToList()) ?? _maxPriorityFeePerGasEstimation.LastPrice ?? GetMinimumGasPrice(headBlock.BaseFeePerGas);
            gasPriceEstimate = UInt256.Min(gasPriceEstimate!, EthGasPriceConstants.MaxGasPrice);
            _maxPriorityFeePerGasEstimation.Set(headBlockHash, gasPriceEstimate);
            return gasPriceEstimate!;
        }

        private UInt256 GetMinimumGasPrice(in UInt256? baseFeePerGas) => (_minGasPrice + (baseFeePerGas ?? 0)) * _defaultMinGasPriceMultiplier / 100ul;

        private delegate UInt256 CalculateGas(Transaction transaction, bool eip1559, UInt256? baseFee);

        private IEnumerable<UInt256> GetGasPricesFromRecentBlocks(long blockNumber, int numberOfBlocks, CalculateGas calculateGasFromTransaction)
        {
            IEnumerable<Block> GetBlocks(long currentBlockNumber)
            {
                while (currentBlockNumber >= 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"GasPriceOracle - searching for block number {currentBlockNumber}");
                    yield return _blockFinder.FindBlock(currentBlockNumber)!;
                    currentBlockNumber--;
                }
            }

            return GetGasPricesFromRecentBlocks(GetBlocks(blockNumber), numberOfBlocks, calculateGasFromTransaction)
                .OrderBy(gasPrice => gasPrice);
        }

        private IEnumerable<UInt256> GetGasPricesFromRecentBlocks(IEnumerable<Block> blocks, int blocksToGoBack, CalculateGas calculateGasFromTransaction)
        {
            int txCount = 0;

            foreach (Block currentBlock in blocks)
            {
                Transaction[] currentBlockTransactions = currentBlock.Transactions;
                int txFromCurrentBlock = 0;
                bool eip1559Enabled = SpecProvider.GetSpec(currentBlock.Header).IsEip1559Enabled;
                UInt256? baseFee = currentBlock.BaseFeePerGas;
                IEnumerable<UInt256> effectiveGasPrices = currentBlockTransactions.Where(tx => tx.SenderAddress != currentBlock.Beneficiary)
                        .Select(tx => calculateGasFromTransaction(tx, eip1559Enabled, baseFee))
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
                    blocksToGoBack--;
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
            int roundedIndex = (int)Math.Round(percentileOfLastIndex);
            return roundedIndex;
        }

        internal struct PriceCache
        {
            public PriceCache(Keccak? headHash, UInt256? price)
            {
                LastHeadHash = headHash;
                LastPrice = price;
            }

            public UInt256? LastPrice { get; private set; }
            private Keccak? LastHeadHash { get; set; }

            public void Set(Keccak headHash, UInt256 price)
            {
                LastHeadHash = headHash;
                LastPrice = price;
            }

            public bool TryGetPrice(Keccak headHash, out UInt256? price)
            {
                if (headHash == LastHeadHash)
                {
                    price = LastPrice;
                    return true;
                }
                else
                {
                    price = null;
                    return false;
                }
            }
        }
    }
}
