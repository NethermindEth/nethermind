// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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
        private readonly LruCache<ValueHash256, BlockFeeHistorySearchInfo> _feeHistoryCache
            = new(MaxBlockCount * 2, "BlockFeeHistoryCache", false);


        private readonly struct BlockFeeHistorySearchInfo(
            long blockNumber,
            UInt256 baseFeePerGas,
            BlockHeader blockHeader,
            long gasUsed,
            ulong? blobGasUsed,
            long gasLimit,
            Hash256? blockParentHash,
            Transaction[] blockTransactions,
            List<(long GasUsed, UInt256 PremiumPerGas)>? rewardsInBlocks)
        {
            public long BlockNumber { get; } = blockNumber;
            public UInt256 BaseFeePerGas { get; } = baseFeePerGas;
            public BlockHeader BlockHeader { get; } = blockHeader;
            public Transaction[] BlockTransactions { get; } = blockTransactions;
            public long GasUsed { get; } = gasUsed;
            public long GasLimit { get; } = gasLimit;
            public ulong? BlobGasUsed { get; } = blobGasUsed;
            public Hash256? BlockParentHash { get; } = blockParentHash;
            public List<(long GasUsed, UInt256 PremiumPerGas)>? RewardsInBlocks { get; } = rewardsInBlocks;
        }

        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(Hash256 blockHash)
        {
            if (_feeHistoryCache.Contains(blockHash)) return _feeHistoryCache.Get(blockHash);

            Block? block = blockFinder.FindBlock(blockHash);
            if (block is null) return null; // do not cache. might be a new unavailable block!
            // findBlock by number might be more efficient! since newer blocks have higher numbers
            // [how to deal with genesis block? since we find by hash and not block number? ignore because its unlikely to reach?]

            BlockFeeHistorySearchInfo historyInfo = new(block.Number, block.BaseFeePerGas, block.Header, block.GasUsed,
                block.BlobGasUsed, block.GasLimit, block.Header.ParentHash, block.Transactions, GetRewardsInBlock(block));
            _feeHistoryCache.Set(block.Hash, historyInfo);

            return historyInfo;
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

            // assumes blockParameter would always contain the hash! This is because a block params identity (=/$) involve
            // all of hash and block number
            BlockFeeHistorySearchInfo? historyInfo = GetHistorySearchInfo(newestBlock.BlockHash!);

            if (historyInfo is null)
            {
                return ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available",
                    ErrorCodes.ResourceUnavailable);
            }

            BlockFeeHistorySearchInfo info = historyInfo.Value;

            long oldestBlockNumber = info.BlockNumber;
            Stack<UInt256> baseFeePerGas = new((int)(blockCount + 1));
            baseFeePerGas.Push(BaseFeeCalculator.Calculate(info.BlockHeader,
                specProvider.GetSpecFor1559(info.BlockNumber + 1)));
            Stack<UInt256> baseFeePerBlobGas = new((int)(blockCount + 1));
            BlobGasCalculator.TryCalculateBlobGasPricePerUnit(info.BlockHeader, out UInt256 blobGas);
            baseFeePerBlobGas.Push(blobGas == UInt256.MaxValue ? 0 : blobGas);

            Stack<double> gasUsedRatio = new((int)blockCount);
            Stack<double> blobGasUsedRatio = new((int)blockCount);

            Stack<UInt256[]>? rewards = rewardPercentiles is null || rewardPercentiles.Length == 0
                ? null
                : new Stack<UInt256[]>((int)blockCount);

            while (historyInfo is not null && blockCount > 0)
            {
                info = historyInfo.Value;
                oldestBlockNumber = info.BlockNumber;
                baseFeePerGas.Push(info.BaseFeePerGas);
                BlobGasCalculator.TryCalculateBlobGasPricePerUnit(info.BlockHeader, out blobGas);
                baseFeePerBlobGas.Push(blobGas == UInt256.MaxValue ? 0 : blobGas);
                gasUsedRatio.Push(info.GasUsed / (double)info.GasLimit);
                blobGasUsedRatio.Push((info.BlobGasUsed ?? 0) / (double)Eip4844Constants.MaxBlobGasPerBlock);
                if (rewards is not null)
                {
                    List<UInt256> rewardsInBlock = CalculateRewardsPercentiles(info, rewardPercentiles);
                    if (rewardsInBlock is not null)
                    {
                        rewards.Push(rewardsInBlock.ToArray());
                    }
                }

                blockCount--;
                // assuming block has a parent with hash (genesis block does not)
                historyInfo = GetHistorySearchInfo(info.BlockParentHash!);
            }

            return ResultWrapper<FeeHistoryResults>.Success(new(oldestBlockNumber, baseFeePerGas.ToArray(),
                gasUsedRatio.ToArray(), baseFeePerBlobGas.ToArray(), blobGasUsedRatio.ToArray(), rewards?.ToArray()));
        }

        private List<UInt256>? CalculateRewardsPercentiles(BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles)
        {
            if (blockInfo.BlockTransactions.Length == 0)
            {
                return Enumerable.Repeat(UInt256.Zero, rewardPercentiles.Length).ToList();
            }

            var rewardsInBlock = blockInfo.RewardsInBlocks;
            return rewardsInBlock is null
                ? null
                : CalculatePercentileValues(blockInfo, rewardPercentiles, rewardsInBlock);
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

        private static List<UInt256> CalculatePercentileValues(BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles, IReadOnlyList<(long GasUsed, UInt256 PremiumPerGas)> rewardsInBlock)
        {
            long sumGasUsed = rewardsInBlock[0].GasUsed;
            int txIndex = 0;
            List<UInt256> percentileValues = new(rewardPercentiles.Length);

            for (int i = 0; i < rewardPercentiles.Length; i++)
            {
                double percentile = rewardPercentiles[i];
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
