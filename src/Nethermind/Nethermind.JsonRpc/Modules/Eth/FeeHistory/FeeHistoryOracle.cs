// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using NonBlocking;

namespace Nethermind.JsonRpc.Modules.Eth.FeeHistory
{
    public class FeeHistoryOracle : IFeeHistoryOracle, IDisposable
    {
        private const int MaxBlockCount = 1024;
        private readonly int _oldestBlockDistanceFromHeadAllowedInCache;
        private long _lastCleanupHeadBlockNumber = 0;
        private Task? _cleanupTask = null;
        private readonly ConcurrentDictionary<Hash256AsKey, BlockFeeHistorySearchInfo> _feeHistoryCache = new();
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;

        public FeeHistoryOracle(IBlockTree blockTree, IReceiptStorage receiptStorage, ISpecProvider specProvider, int? maxDistanceFromHead = null)
        {
            _blockTree = blockTree;
            _receiptStorage = receiptStorage;
            _specProvider = specProvider;
            _oldestBlockDistanceFromHeadAllowedInCache = maxDistanceFromHead ?? MaxBlockCount + 4;
            blockTree.BlockAddedToMain += OnBlockAddedToMain;
        }

        private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            Task.Run(() =>
            {
                if (e.PreviousBlock is not null)
                {
                    _feeHistoryCache.TryRemove(e.PreviousBlock.Hash, out _);
                }

                if (ShouldCache(e.Block))
                {
                    SaveHistorySearchInfo(e.Block);
                    TryRunCleanup();
                }
            });
        }

        private readonly record struct RewardInfo(long GasUsed, UInt256 PremiumPerGas);

        private readonly record struct BlockFeeHistorySearchInfo(
            long BlockNumber,
            UInt256 BlockBaseFeePerGas,
            UInt256 BaseFeePerGasEst,
            UInt256 BaseFeePerBlobGas,
            double GasUsedRatio,
            double BlobGasUsedRatio,
            Hash256? ParentHash,
            long GasUsed,
            int BlockTransactionsLength,
            List<RewardInfo> RewardsInBlocks);

        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.BlockHash
                && _feeHistoryCache.TryGetValue(blockParameter.BlockHash!, out BlockFeeHistorySearchInfo i))
            {
                return i;
            }

            Block block = _blockTree.FindBlock(blockParameter);
            return block is null
                ? null
                : _feeHistoryCache.TryGetValue(block.Hash!, out BlockFeeHistorySearchInfo info)
                    ? info
                    : SaveHistorySearchInfo(block);
        }

        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(Hash256 blockHash, long blockNumber)
        {
            if (!_feeHistoryCache.TryGetValue(blockHash, out BlockFeeHistorySearchInfo info))
            {
                const BlockTreeLookupOptions options = BlockTreeLookupOptions.ExcludeTxHashes |
                                                       BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                                                       BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

                Block? block = _blockTree.FindBlock(blockHash, options, blockNumber);
                return block is null ? null : SaveHistorySearchInfo(block);
            }

            return info;
        }

        // only saves block younger than the Oldest Block From the Head Allowed In Cache.
        // As time passes and the head progresses only older least used blocks are auto removed from the cache
        private BlockFeeHistorySearchInfo? SaveHistorySearchInfo(Block block)
        {
            BlockFeeHistorySearchInfo BlockFeeHistorySearchInfoFromBlock(Block b)
            {
                BlobGasCalculator.TryCalculateBlobGasPricePerUnit(b.Header, out UInt256 blobGas);
                return new(
                    b.Number,
                    b.BaseFeePerGas,
                    BaseFeeCalculator.Calculate(b.Header, _specProvider.GetSpecFor1559(b.Number + 1)),
                    blobGas == UInt256.MaxValue ? 0 : blobGas,
                    b.GasUsed / (double)b.GasLimit,
                    (b.BlobGasUsed ?? 0) / (double)Eip4844Constants.MaxBlobGasPerBlock,
                    b.ParentHash,
                    b.GasUsed,
                    b.Transactions.Length,
                    GetRewardsInBlock(b));
            }

            BlockFeeHistorySearchInfo historyInfo = BlockFeeHistorySearchInfoFromBlock(block);

            if (ShouldCache(block))
            {
                _feeHistoryCache[block.Hash!] = historyInfo;
            }

            return historyInfo;
        }

        private bool ShouldCache(Block block) =>
            _blockTree.Head is null || block.Number >= _blockTree.Head.Number - _oldestBlockDistanceFromHeadAllowedInCache;

        public ResultWrapper<FeeHistoryResults> GetFeeHistory(
            int blockCount,
            BlockParameter newestBlock,
            double[]? rewardPercentiles = null)
        {
            ResultWrapper<FeeHistoryResults> initialCheckResult =
                Validate(ref blockCount, newestBlock, rewardPercentiles);
            if (initialCheckResult.Result.ResultType == ResultType.Failure)
            {
                return initialCheckResult;
            }

            BlockFeeHistorySearchInfo? historyInfo = GetHistorySearchInfo(newestBlock);

            if (historyInfo is null)
            {
                return ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available",
                    ErrorCodes.ResourceUnavailable);
            }

            BlockFeeHistorySearchInfo info = historyInfo.Value;

            // Assumes if blockCount is ever greater than the BlockNumber then BlockNumber must fall within integer size limits
            var effectiveBlockCount = info.BlockNumber < blockCount - 1 ? (int)info.BlockNumber + 1 : blockCount;
            var tempBlockCount = effectiveBlockCount + 1;
            ArrayPoolList<UInt256> baseFeePerGas = new(tempBlockCount, tempBlockCount);
            ArrayPoolList<UInt256> baseFeePerBlobGas = new(tempBlockCount, tempBlockCount);
            ArrayPoolList<double> gasUsedRatio = new(effectiveBlockCount, effectiveBlockCount);
            ArrayPoolList<double> blobGasUsedRatio = new(effectiveBlockCount, effectiveBlockCount);
            ArrayPoolList<ArrayPoolList<UInt256>>? rewards = rewardPercentiles?.Length > 0
                ? new ArrayPoolList<ArrayPoolList<UInt256>>(effectiveBlockCount, effectiveBlockCount)
                : null;

            long oldestBlockNumber = info.BlockNumber;
            baseFeePerGas[effectiveBlockCount] = info.BaseFeePerGasEst;
            baseFeePerBlobGas[effectiveBlockCount] = info.BaseFeePerBlobGas;

            while (historyInfo is not null && effectiveBlockCount-- > 0)
            {
                info = historyInfo.Value;
                oldestBlockNumber = info.BlockNumber;
                baseFeePerGas[effectiveBlockCount] = info.BlockBaseFeePerGas;
                baseFeePerBlobGas[effectiveBlockCount] = info.BaseFeePerBlobGas;
                gasUsedRatio[effectiveBlockCount] = info.GasUsedRatio;
                blobGasUsedRatio[effectiveBlockCount] = info.BlobGasUsedRatio;
                if (rewards is not null)
                {
                    ArrayPoolList<UInt256>? rewardsInBlock = CalculateRewardsPercentiles(info, rewardPercentiles);
                    if (rewardsInBlock is not null)
                    {
                        rewards[effectiveBlockCount] = rewardsInBlock;
                    }
                }
                historyInfo = info.ParentHash is null ? null : GetHistorySearchInfo(info.ParentHash, info.BlockNumber - 1);
            }

            TryRunCleanup();

            return ResultWrapper<FeeHistoryResults>.Success(new(oldestBlockNumber, baseFeePerGas,
                gasUsedRatio, baseFeePerBlobGas, blobGasUsedRatio, rewards));
        }

        private void TryRunCleanup()
        {
            long headNumber = _blockTree.Head?.Number ?? 0;
            long lastCleanupHeadBlockNumber = _lastCleanupHeadBlockNumber;
            if (lastCleanupHeadBlockNumber != headNumber
                && _feeHistoryCache.Count > 2 * MaxBlockCount // let's let the cache grow a bit and do less cleanup
                && _cleanupTask is null
                && Interlocked.CompareExchange(ref _lastCleanupHeadBlockNumber, headNumber, lastCleanupHeadBlockNumber) == lastCleanupHeadBlockNumber)
            {
                _cleanupTask = Task.Run(CleanupCache);
            }
        }

        private void CleanupCache()
        {
            foreach (KeyValuePair<Hash256AsKey, BlockFeeHistorySearchInfo> historyInfo in _feeHistoryCache)
            {
                if (historyInfo.Value.BlockNumber < _lastCleanupHeadBlockNumber - _oldestBlockDistanceFromHeadAllowedInCache)
                {
                    _feeHistoryCache.TryRemove(historyInfo);
                }
            }

            _cleanupTask = null;
        }

        private static ArrayPoolList<UInt256>? CalculateRewardsPercentiles(
            BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles)
        {
            return blockInfo.BlockTransactionsLength == 0
                ? new ArrayPoolList<UInt256>(rewardPercentiles.Length, Enumerable.Repeat(UInt256.Zero, rewardPercentiles.Length))
                : CalculatePercentileValues(blockInfo, rewardPercentiles, blockInfo.RewardsInBlocks);
        }

        private List<RewardInfo> GetRewardsInBlock(Block block)
        {
            IEnumerable<long> CalculateGasUsed(TxReceipt[] txReceipts)
            {
                long previousGasUsedTotal = 0;
                foreach (TxReceipt receipt in txReceipts)
                {
                    long gasUsedTotal = receipt.GasUsedTotal;
                    yield return gasUsedTotal - previousGasUsedTotal;
                    previousGasUsedTotal = gasUsedTotal;
                }
            }

            TxReceipt[] receipts = _receiptStorage.Get(block, false);
            Transaction[] txs = block.Transactions;
            using ArrayPoolList<long> gasUsed = new(txs.Length, receipts.Length == block.Transactions.Length
                ? CalculateGasUsed(receipts)
                // If no receipts available, approximate on GasLimit
                // We could just go with null here too and just don't return percentiles
                : txs.Select(tx => tx.GasLimit));

            List<RewardInfo> rewardInfos = new(txs.Length);
            for (int i = 0; i < txs.Length; i++)
            {
                txs[i].TryCalculatePremiumPerGas(block.BaseFeePerGas, out UInt256 premiumPerGas);
                rewardInfos.Add(new RewardInfo(gasUsed[i], premiumPerGas));
            }

            rewardInfos.Sort((i1, i2) => i1.PremiumPerGas.CompareTo(i2.PremiumPerGas));

            return rewardInfos;
        }

        private static ArrayPoolList<UInt256> CalculatePercentileValues(
            BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles,
            List<RewardInfo> rewardsInBlock)
        {
            long sumGasUsed = rewardsInBlock[0].GasUsed;
            int txIndex = 0;
            ArrayPoolList<UInt256> percentileValues = new(rewardPercentiles.Length);

            foreach (var percentile in rewardPercentiles)
            {
                double thresholdGasUsed = (ulong)(blockInfo.GasUsed * percentile / 100);
                while (txIndex < rewardsInBlock.Count && sumGasUsed < thresholdGasUsed)
                {
                    txIndex++;
                    sumGasUsed += rewardsInBlock[txIndex].GasUsed;
                }

                percentileValues.Add(rewardsInBlock[txIndex].PremiumPerGas);
            }

            return percentileValues;
        }

        private static ResultWrapper<FeeHistoryResults> Validate(ref int blockCount, BlockParameter newestBlock, double[]? rewardPercentiles)
        {
            if (newestBlock.Type == BlockParameterType.BlockHash)
            {
                return ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Is not correct block number", ErrorCodes.InvalidParams);
            }

            switch (blockCount)
            {
                case < 1:
                    return ResultWrapper<FeeHistoryResults>.Fail($"blockCount: Value {blockCount} is less than 1", ErrorCodes.InvalidParams);
                case > MaxBlockCount:
                    blockCount = MaxBlockCount;
                    break;
            }

            if (rewardPercentiles is null) return ResultWrapper<FeeHistoryResults>.Success(null);
            double previousPercentile = -1;
            for (int i = 0; i < rewardPercentiles.Length; i++)
            {
                double currentPercentile = rewardPercentiles[i];
                if (currentPercentile > 100 || currentPercentile < 0)
                {
                    return ResultWrapper<FeeHistoryResults>.Fail("rewardPercentiles: Some values are below 0 or greater than 100.",
                        ErrorCodes.InvalidParams);
                }
                if (currentPercentile <= previousPercentile)
                {
                    return ResultWrapper<FeeHistoryResults>.Fail(
                        $"rewardPercentiles: Value at index {i}: {currentPercentile} is less than or equal to the value at previous index {i - 1}: {rewardPercentiles[i - 1]}.",
                        ErrorCodes.InvalidParams);
                }

                previousPercentile = currentPercentile;
            }

            return ResultWrapper<FeeHistoryResults>.Success(null);
        }

        public void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        }
    }
}
