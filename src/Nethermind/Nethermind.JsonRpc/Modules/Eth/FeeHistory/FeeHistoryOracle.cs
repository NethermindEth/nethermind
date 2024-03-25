// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using NonBlocking;

namespace Nethermind.JsonRpc.Modules.Eth.FeeHistory
{
    public class FeeHistoryOracle(IBlockFinder blockFinder, IReceiptStorage receiptStorage, ISpecProvider specProvider, int? maxDistanceFromHead = null)
        : IFeeHistoryOracle
    {
        private const int MaxBlockCount = 1024;
        private readonly int _oldestBlockDistanceFromHeadAllowedInCache = maxDistanceFromHead ?? MaxBlockCount + 16;
        private long _lastHeadBlockNumber = 0;
        private Task? _cleanupTask = null;
        private readonly ConcurrentDictionary<ValueHash256, BlockFeeHistorySearchInfo> _feeHistoryCache = new();

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
            List<RewardInfo>? RewardsInBlocks);

        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(BlockParameter blockParameter)
        {
            Block? block;
            if (blockParameter.Type != BlockParameterType.BlockHash)
            {
                block = blockFinder.FindBlock(blockParameter);
                return block is null ? null : BlockFeeHistorySearchInfoFromBlock(block);
            }

            if (_feeHistoryCache.TryGetValue(blockParameter.BlockHash!, out BlockFeeHistorySearchInfo info)) return info;
            block = blockFinder.FindBlock(blockParameter);
            return block is null ? null : SaveHistorySearchInfo(block);

        }
        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(Hash256 blockHash, long blockNumber)
        {
            if (_feeHistoryCache.TryGetValue(blockHash, out BlockFeeHistorySearchInfo info)) return info;
            Block? block = blockFinder.FindBlock(blockHash, BlockTreeLookupOptions.RequireCanonical, blockNumber);
            return block is null ? null : SaveHistorySearchInfo(block);
        }

        // only saves block younger than the Oldest Block From the Head Allowed In Cache.
        // As time passes and the head progresses only older least used blocks are auto removed from the cache
        private BlockFeeHistorySearchInfo? SaveHistorySearchInfo(Block block)
        {
            BlockFeeHistorySearchInfo historyInfo = BlockFeeHistorySearchInfoFromBlock(block);

            if (blockFinder.Head is null || block.Number >= blockFinder.Head.Number - _oldestBlockDistanceFromHeadAllowedInCache)
            {
                _feeHistoryCache[block.Hash!] = historyInfo;
            }

            return historyInfo;
        }

        private BlockFeeHistorySearchInfo BlockFeeHistorySearchInfoFromBlock(Block block)
        {
            BlobGasCalculator.TryCalculateBlobGasPricePerUnit(block.Header, out UInt256 blobGas);
            return new(
                block.Number,
                block.BaseFeePerGas,
                BaseFeeCalculator.Calculate(block.Header, specProvider.GetSpecFor1559(block.Number + 1)),
                blobGas == UInt256.MaxValue ? 0 : blobGas,
                block.GasUsed / (double)block.GasLimit,
                (block.BlobGasUsed ?? 0) / (double)Eip4844Constants.MaxBlobGasPerBlock,
                block.ParentHash,
                block.GasUsed,
                block.Transactions.Length,
                GetRewardsInBlock(block));
        }

        public ResultWrapper<FeeHistoryResults> GetFeeHistory(long blockCount, BlockParameter newestBlock,
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

            Stack<UInt256> baseFeePerGas = new((int)(blockCount + 1));
            Stack<UInt256> baseFeePerBlobGas = new((int)(blockCount + 1));
            Stack<double> gasUsedRatio = new((int)blockCount);
            Stack<double> blobGasUsedRatio = new((int)blockCount);
            Stack<UInt256[]>? rewards = rewardPercentiles is null || rewardPercentiles.Length == 0
                ? null
                : new Stack<UInt256[]>((int)blockCount);

            long oldestBlockNumber = info.BlockNumber;
            baseFeePerGas.Push(info.BaseFeePerGasEst);
            baseFeePerBlobGas.Push(info.BaseFeePerBlobGas);

            while (historyInfo is not null && blockCount > 0)
            {
                info = historyInfo.Value;
                oldestBlockNumber = info.BlockNumber;
                baseFeePerGas.Push(info.BlockBaseFeePerGas);
                baseFeePerBlobGas.Push(info.BaseFeePerBlobGas);
                gasUsedRatio.Push(info.GasUsedRatio);
                blobGasUsedRatio.Push(info.BlobGasUsedRatio);
                if (rewards is not null)
                {
                    List<UInt256> rewardsInBlock = CalculateRewardsPercentiles(info, rewardPercentiles);
                    if (rewardsInBlock is not null)
                    {
                        rewards.Push(rewardsInBlock.ToArray());
                    }
                }
                blockCount--;
                historyInfo = info.ParentHash is null ? null : GetHistorySearchInfo(info.ParentHash, info.BlockNumber - 1);
            }

            long headNumber = blockFinder.Head?.Number ?? 0;
            if (_lastHeadBlockNumber != headNumber && _cleanupTask is null)
            {
                Task newTask = new(CleanupCache);
                if (Interlocked.CompareExchange(ref _cleanupTask, newTask, null) is null)
                {
                    _lastHeadBlockNumber = headNumber;
                    newTask.Start();
                }
            }

            return ResultWrapper<FeeHistoryResults>.Success(new(oldestBlockNumber, baseFeePerGas.ToArray(),
                gasUsedRatio.ToArray(), baseFeePerBlobGas.ToArray(), blobGasUsedRatio.ToArray(), rewards?.ToArray()));
        }

        private void CleanupCache()
        {
            foreach (KeyValuePair<ValueHash256,BlockFeeHistorySearchInfo> historyInfo in _feeHistoryCache)
            {
                if (historyInfo.Value.BlockNumber < _lastHeadBlockNumber - _oldestBlockDistanceFromHeadAllowedInCache)
                {
                    _feeHistoryCache.TryRemove(historyInfo);
                }
            }

            _cleanupTask = null;
        }

        private List<UInt256>? CalculateRewardsPercentiles(BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles)
        {
            if (blockInfo.BlockTransactionsLength == 0)
            {
                return Enumerable.Repeat(UInt256.Zero, rewardPercentiles.Length).ToList();
            }

            List<RewardInfo>? rewardsInBlock = blockInfo.RewardsInBlocks;
            return rewardsInBlock is null
                ? null
                : CalculatePercentileValues(blockInfo, rewardPercentiles, rewardsInBlock);
        }

        private List<RewardInfo>? GetRewardsInBlock(Block block)
        {
            TxReceipt[]? receipts = receiptStorage.Get(block, false);
            Transaction[] txs = block.Transactions;
            List<RewardInfo> valueTuples = new(txs.Length);
            long gasUsedTotalBeforeReceipt = 0;
            for (int i = 0; i < txs.Length; i++)
            {
                Transaction tx = txs[i];
                tx.TryCalculatePremiumPerGas(block.BaseFeePerGas, out UInt256 premiumPerGas);
                long gasUsedTotal = receipts[i].GasUsedTotal;
                valueTuples.Add(new RewardInfo(gasUsedTotal - gasUsedTotalBeforeReceipt, premiumPerGas));
                gasUsedTotalBeforeReceipt += gasUsedTotal;
            }

            valueTuples.Sort((i1, i2) => i1.PremiumPerGas.CompareTo(i2.PremiumPerGas));

            return valueTuples;
        }

        private static List<UInt256> CalculatePercentileValues(BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles, IReadOnlyList<RewardInfo> rewardsInBlock)
        {
            long sumGasUsed = rewardsInBlock[0].GasUsed;
            int txIndex = 0;
            List<UInt256> percentileValues = new(rewardPercentiles.Length);

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

        private static ResultWrapper<FeeHistoryResults> Validate(ref long blockCount, BlockParameter newestBlock, double[]? rewardPercentiles)
        {
            if (newestBlock.Type == BlockParameterType.BlockHash)
            {
                return ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Is not correct block number", ErrorCodes.InvalidParams);
            }

            if (blockCount < 1)
            {
                return ResultWrapper<FeeHistoryResults>.Fail($"blockCount: Value {blockCount} is less than 1", ErrorCodes.InvalidParams);
            }

            if (blockCount > MaxBlockCount)
            {
                blockCount = MaxBlockCount;
            }

            if (rewardPercentiles is not null)
            {
                double previousPercentile = -1;
                for (int i = 0; i < rewardPercentiles.Length; i++)
                {
                    double currentPercentile = rewardPercentiles[i];
                    if (currentPercentile > 100 || currentPercentile < 0)
                    {
                        return ResultWrapper<FeeHistoryResults>.Fail("rewardPercentiles: Some values are below 0 or greater than 100.",
                            ErrorCodes.InvalidParams);
                    }
                    else if (currentPercentile <= previousPercentile)
                    {
                        return ResultWrapper<FeeHistoryResults>.Fail(
                            $"rewardPercentiles: Value at index {i}: {currentPercentile} is less than or equal to the value at previous index {i - 1}: {rewardPercentiles[i - 1]}.",
                            ErrorCodes.InvalidParams);
                    }
                    else
                    {
                        previousPercentile = currentPercentile;
                    }
                }
            }

            return ResultWrapper<FeeHistoryResults>.Success(null);
        }
    }
}
