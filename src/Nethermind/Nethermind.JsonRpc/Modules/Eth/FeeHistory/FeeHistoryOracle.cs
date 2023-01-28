// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.FeeHistory
{
    public class FeeHistoryOracle : IFeeHistoryOracle
    {
        private const int MaxBlockCount = 1024;
        private readonly IBlockFinder _blockFinder;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;

        public FeeHistoryOracle(IBlockFinder blockFinder, IReceiptStorage receiptStorage, ISpecProvider specProvider)
        {
            _blockFinder = blockFinder;
            _receiptStorage = receiptStorage;
            _specProvider = specProvider;
        }

        public ResultWrapper<FeeHistoryResults> GetFeeHistory(long blockCount, BlockParameter newestBlock, double[]? rewardPercentiles = null)
        {
            ResultWrapper<FeeHistoryResults> initialCheckResult = Validate(ref blockCount, newestBlock, rewardPercentiles);
            if (initialCheckResult.Result.ResultType == ResultType.Failure)
            {
                return initialCheckResult;
            }

            Block? block = _blockFinder.FindBlock(newestBlock);
            if (block is null)
            {
                return ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available", ErrorCodes.ResourceUnavailable);
            }

            long oldestBlockNumber = block!.Number;
            Stack<UInt256> baseFeePerGas = new((int)(blockCount + 1));
            baseFeePerGas.Push(BaseFeeCalculator.Calculate(block!.Header, _specProvider.GetSpecFor1559(block!.Number + 1)));
            Stack<double> gasUsedRatio = new((int)blockCount);

            Stack<UInt256[]>? rewards = rewardPercentiles is null || rewardPercentiles.Any() is false ? null : new Stack<UInt256[]>((int)blockCount);

            while (block is not null && blockCount > 0)
            {
                oldestBlockNumber = block.Number;
                baseFeePerGas.Push(block.BaseFeePerGas);
                gasUsedRatio.Push(block.GasUsed / (double)block.GasLimit);
                if (rewards is not null)
                {
                    List<UInt256> rewardsInBlock = CalculateRewardsPercentiles(block, rewardPercentiles);
                    if (rewardsInBlock is not null)
                    {
                        rewards.Push(rewardsInBlock.ToArray());
                    }
                }

                blockCount--;
                block = _blockFinder.FindParent(block, BlockTreeLookupOptions.RequireCanonical);
            }

            FeeHistoryResults feeHistoryResults = new(oldestBlockNumber, baseFeePerGas.ToArray(), gasUsedRatio.ToArray(), rewards?.ToArray());
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
            TxReceipt[]? receipts = _receiptStorage.Get(block);
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

        private ResultWrapper<FeeHistoryResults> Validate(ref long blockCount, BlockParameter newestBlock, double[]? rewardPercentiles)
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
