// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.FeeHistory
{
    public class FeeHistoryOracle(IBlockFinder blockFinder, IReceiptStorage receiptStorage, ISpecProvider specProvider)
        : IFeeHistoryOracle
    {
        private const int MaxBlockCount = 1024;
        private readonly LruCache<ValueHash256, BlockFeeHistoryInfo> _feeHistoryCache
            = new(MaxBlockCount * 2, "BlockFeeHistoryCache", false);


        private readonly struct BlockFeeHistoryInfo(
            long oldestBlockNumber,
            UInt256[] baseFeePerGas,
            double[] gasUsedRatio,
            UInt256[] baseFeePerBlobGas,
            double[] blobGasUsedRatio,
            UInt256[][]? rewards)
        {
            public long OldestBlockNumber { get; } = oldestBlockNumber;
            public UInt256[] BaseFeePerGas { get; } = baseFeePerGas;
            public UInt256[] BaseFeePerBlobGas { get; } = baseFeePerBlobGas;
            public UInt256[][]? Rewards { get; } = rewards;
            public double[] GasUsedRatio { get; } = gasUsedRatio;
            public double[] BlobGasUsedRatio { get; } = blobGasUsedRatio;
        }

        public ResultWrapper<FeeHistoryResults> GetFeeHistory(long blockCount, BlockParameter newestBlock, double[]? rewardPercentiles = null)
        {
            ResultWrapper<FeeHistoryResults> initialCheckResult = Validate(ref blockCount, newestBlock, rewardPercentiles);
            if (initialCheckResult.Result.ResultType == ResultType.Failure)
            {
                return initialCheckResult;
            }

            BlockFeeHistoryInfo historyInfo;
            if (!_feeHistoryCache.Contains(newestBlock.BlockHash))
            {
                Block? block = blockFinder.FindBlock(newestBlock);
                if (block is null)
                {
                    return ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available", ErrorCodes.ResourceUnavailable);
                }

                long oldestBlockNumber = block!.Number;
                Stack<UInt256> baseFeePerGas = new((int)(blockCount + 1));
                baseFeePerGas.Push(BaseFeeCalculator.Calculate(block!.Header, specProvider.GetSpecFor1559(block!.Number + 1)));
                Stack<UInt256> baseFeePerBlobGas = new((int)(blockCount + 1));
                BlobGasCalculator.TryCalculateBlobGasPricePerUnit(block!.Header, out UInt256 blobGas);
                baseFeePerBlobGas.Push(blobGas == UInt256.MaxValue ? 0 : blobGas);

                Stack<double> gasUsedRatio = new((int)blockCount);
                Stack<double> blobGasUsedRatio = new((int)blockCount);

                Stack<UInt256[]>? rewards = rewardPercentiles is null || rewardPercentiles.Length == 0 ? null : new Stack<UInt256[]>((int)blockCount);

                while (block is not null && blockCount > 0)
                {
                    oldestBlockNumber = block.Number;
                    baseFeePerGas.Push(block.BaseFeePerGas);
                    BlobGasCalculator.TryCalculateBlobGasPricePerUnit(block!.Header, out blobGas);
                    baseFeePerBlobGas.Push(blobGas == UInt256.MaxValue ? 0 : blobGas);
                    gasUsedRatio.Push(block.GasUsed / (double)block.GasLimit);
                    blobGasUsedRatio.Push((block.BlobGasUsed ?? 0) / (double)Eip4844Constants.MaxBlobGasPerBlock);
                    if (rewards is not null)
                    {
                        List<UInt256> rewardsInBlock = CalculateRewardsPercentiles(block, rewardPercentiles);
                        if (rewardsInBlock is not null)
                        {
                            rewards.Push(rewardsInBlock.ToArray());
                        }
                    }

                    blockCount--;
                    block = blockFinder.FindParent(block, BlockTreeLookupOptions.RequireCanonical);
                }

                // maybe take into account that block history creates a sequential path to oldest block
                // implying all younger blocks results would be subsets of the results in older blocks
                // this might be useful in caching (space) and creating results from older [partial] results
                // Also why stacks? instead of arrays/arraylists.
                historyInfo = new BlockFeeHistoryInfo(oldestBlockNumber, baseFeePerGas.ToArray(), gasUsedRatio.ToArray(),
                    baseFeePerBlobGas.ToArray(), blobGasUsedRatio.ToArray(), rewards?.ToArray());
                _feeHistoryCache.Set(newestBlock.BlockHash, historyInfo!);
            }

            historyInfo = _feeHistoryCache.Get(newestBlock.BlockHash);


            FeeHistoryResults feeHistoryResults = new(historyInfo.OldestBlockNumber, historyInfo.BaseFeePerGas,
                historyInfo.GasUsedRatio, historyInfo.BaseFeePerBlobGas, historyInfo!.BlobGasUsedRatio, historyInfo!.Rewards);
            return ResultWrapper<FeeHistoryResults>.Success(feeHistoryResults);
        }

        private List<UInt256>? CalculateRewardsPercentiles(Block block, double[] rewardPercentiles)
        {
            if (block.Transactions.Length == 0)
            {
                return Enumerable.Repeat(UInt256.Zero, rewardPercentiles.Length).ToList();
            }
            var rewardsInBlock = GetRewardsInBlock(block);
            return rewardsInBlock is null ? null : CalculatePercentileValues(block, rewardPercentiles, rewardsInBlock);
        }

        private List<(long GasUsed, UInt256 PremiumPerGas)>? GetRewardsInBlock(Block block)
        {
            TxReceipt[]? receipts = receiptStorage.Get(block);
            Transaction[] txs = block.Transactions;
            List<(long GasUsed, UInt256 PremiumPerGas)> valueTuples = new(txs.Length);
            for (int i = 0; i < txs.Length; i++)
            {
                Transaction tx = txs[i];
                tx.TryCalculatePremiumPerGas(block.BaseFeePerGas, out UInt256 premiumPerGas);
                valueTuples.Add((receipts[i].GasUsed, premiumPerGas));
            }

            valueTuples.Sort((i1, i2) => i1.PremiumPerGas.CompareTo(i2.PremiumPerGas));

            return valueTuples;
        }

        private static List<UInt256> CalculatePercentileValues(Block block, double[] rewardPercentiles, IReadOnlyList<(long GasUsed, UInt256 PremiumPerGas)> rewardsInBlock)
        {
            long sumGasUsed = rewardsInBlock[0].GasUsed;
            int txIndex = 0;
            List<UInt256> percentileValues = new(rewardPercentiles.Length);

            for (int i = 0; i < rewardPercentiles.Length; i++)
            {
                double percentile = rewardPercentiles[i];
                double thresholdGasUsed = (ulong)(block.GasUsed * percentile / 100);
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
