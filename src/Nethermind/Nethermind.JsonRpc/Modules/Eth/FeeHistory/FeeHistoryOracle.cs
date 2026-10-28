// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        private static readonly ResultWrapper<FeeHistoryResults> _validationPassed = ResultWrapper<FeeHistoryResults>.Success(null);
        private const int MaxBlockCount = 1024;
        private const int RewardPercentilesLengthLimit = 100;
        private readonly ulong _oldestBlockDistanceFromHeadAllowedInCache;
        private ulong _lastCleanupHeadBlockNumber = 0;
        private Task? _cleanupTask = null;
        private readonly ConcurrentDictionary<Hash256AsKey, BlockFeeHistorySearchInfo> _feeHistoryCache = new();
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;

        public FeeHistoryOracle(IBlockTree blockTree, IReceiptStorage receiptStorage, ISpecProvider specProvider, ulong? maxDistanceFromHead = null)
        {
            _blockTree = blockTree;
            _receiptStorage = receiptStorage;
            _specProvider = specProvider;
            _oldestBlockDistanceFromHeadAllowedInCache = maxDistanceFromHead ?? MaxBlockCount + 4;
            blockTree.BlockAddedToMain += OnBlockAddedToMain;
        }

        private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e) => Task.Run(() =>
        {
            if (e.PreviousBlock is not null)
            {
                _feeHistoryCache.TryRemove(e.PreviousBlock.Hash, out _);
            }

            if (ShouldCache(e.Block))
            {
                SaveHistorySearchInfo(e.Block, withRewards: false);
                TryRunCleanup();
            }
        });

        private readonly record struct RewardInfo(ulong GasUsed, UInt256 PremiumPerGas);

        private readonly struct RewardInfoByPremiumAscendingComparer : IComparer<RewardInfo>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare(RewardInfo i1, RewardInfo i2) => i1.PremiumPerGas.CompareTo(i2.PremiumPerGas);
        }

        private readonly record struct BlockFeeHistorySearchInfo(
            ulong BlockNumber,
            UInt256 BlockBaseFeePerGas,
            UInt256 BaseFeePerGasEst,
            UInt256 BaseFeePerBlobGas,
            UInt256 BaseFeePerBlobGasEst,
            double GasUsedRatio,
            double BlobGasUsedRatio,
            Hash256? ParentHash,
            Hash256 BlockHash,
            int BlockTransactionsLength,
            // Filled by the first request for reward percentiles: loading receipts is wasted on head
            // processing and on requests without percentiles.
            StrongBox<BlockRewards?> Rewards);

        /// <param name="GasUsedTotal">Total of the per-tx reward weights. Not the header GasUsed: that is pre-refund under
        /// EIP-7778, and it never matched the GasLimit weights used when receipts are unavailable.</param>
        /// <param name="SortedByPremium">The per-tx reward weights, ascending by premium.</param>
        private sealed record BlockRewards(ulong GasUsedTotal, List<RewardInfo> SortedByPremium);

        private const BlockTreeLookupOptions AncestorLookupOptions = BlockTreeLookupOptions.ExcludeTxHashes |
                                                                     BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                                                                     BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(BlockParameter blockParameter, bool withRewards)
        {
            Hash256? hash = blockParameter.Type == BlockParameterType.BlockHash
                ? blockParameter.BlockHash
                : _blockTree.FindHeader(blockParameter)?.Hash;
            if (hash is not null && _feeHistoryCache.TryGetValue(hash, out BlockFeeHistorySearchInfo i))
            {
                return i;
            }

            Block block = _blockTree.FindBlock(blockParameter);
            return block is null
                ? null
                : _feeHistoryCache.TryGetValue(block.Hash!, out BlockFeeHistorySearchInfo info)
                    ? info
                    : SaveHistorySearchInfo(block, withRewards);
        }

        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(Hash256 blockHash, ulong blockNumber, bool withRewards)
        {
            if (!_feeHistoryCache.TryGetValue(blockHash, out BlockFeeHistorySearchInfo info))
            {
                Block? block = _blockTree.FindBlock(blockHash, AncestorLookupOptions, blockNumber);
                return block is null ? null : SaveHistorySearchInfo(block, withRewards);
            }

            return info;
        }

        // only saves block younger than the Oldest Block From the Head Allowed In Cache.
        // As time passes and the head progresses only older least used blocks are auto removed from the cache
        private BlockFeeHistorySearchInfo? SaveHistorySearchInfo(Block block, bool withRewards)
        {
            BlockFeeHistorySearchInfo BlockFeeHistorySearchInfoFromBlock(Block b)
            {
                IReleaseSpec spec = _specProvider.GetSpec(b.Header);
                BlobGasCalculator.TryCalculateFeePerBlobGas(b.Header, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas);
                ulong maxBlobGasPerBlock = spec.GasCosts.MaxBlobGasPerBlock;
                double blobGasUsedRatio = maxBlobGasPerBlock == 0 ? 0 : (b.BlobGasUsed ?? 0) / (double)maxBlobGasPerBlock;

                // Uses parent-block spec for BlobBaseFeeUpdateFraction, matching Geth's feehistory.go.
                // At hard-fork boundaries this is one block off, but it is intentional for compatibility.
                UInt256 nextFeePerBlobGas =
                    b.Header.ExcessBlobGas.HasValue &&
                    BlobGasCalculator.TryCalculateFeePerBlobGas(BlobGasCalculator.CalculateExcessBlobGas(b.Header, spec) ?? 0, spec.BlobBaseFeeUpdateFraction, out UInt256 fee)
                    ? fee
                    : UInt256.Zero;

                return new(
                    b.Number,
                    b.BaseFeePerGas,
                    BaseFeeCalculator.Calculate(b.Header, _specProvider.GetSpecFor1559(b.Number + 1)),
                    feePerBlobGas == UInt256.MaxValue ? UInt256.Zero : feePerBlobGas,
                    nextFeePerBlobGas,
                    b.GasUsed / (double)b.GasLimit,
                    blobGasUsedRatio,
                    b.ParentHash,
                    b.Hash!,
                    b.Transactions.Length,
                    new StrongBox<BlockRewards?>(withRewards ? GetRewardsInBlock(b, spec) : null));
            }

            BlockFeeHistorySearchInfo historyInfo = BlockFeeHistorySearchInfoFromBlock(block);

            // Keeps an entry already cached for the hash, so its filled rewards are not dropped.
            return ShouldCache(block) ? _feeHistoryCache.GetOrAdd(block.Hash!, historyInfo) : historyInfo;
        }

        private bool ShouldCache(Block block) =>
            _blockTree.Head is null || block.Number >= _blockTree.Head.Number.SaturatingSub(_oldestBlockDistanceFromHeadAllowedInCache);

        public ResultWrapper<FeeHistoryResults> GetFeeHistory(
            ulong blockCount,
            BlockParameter newestBlock,
            double[] rewardPercentiles)
        {
            ArgumentNullException.ThrowIfNull(rewardPercentiles);
            if (blockCount > MaxBlockCount)
                blockCount = MaxBlockCount;

            ResultWrapper<FeeHistoryResults> initialCheckResult = Validate(blockCount, newestBlock, rewardPercentiles);
            if (initialCheckResult.Result.ResultType == ResultType.Failure)
            {
                return initialCheckResult;
            }

            bool withRewards = rewardPercentiles.Length > 0;
            BlockFeeHistorySearchInfo? historyInfo = GetHistorySearchInfo(newestBlock, withRewards);

            if (historyInfo is null)
            {
                BlockHeader? header = _blockTree.FindHeader(newestBlock);
                return header is null
                    ? ResultWrapper<FeeHistoryResults>.Fail("request beyond head block", ErrorCodes.ResourceNotFound)
                    : ResultWrapper<FeeHistoryResults>.Fail(ErrorMessages.PrunedHistoryUnavailable, ErrorCodes.PrunedHistoryUnavailable);
            }

            BlockFeeHistorySearchInfo info = historyInfo.Value;

            // Assumes if blockCount is ever greater than the BlockNumber then BlockNumber must fall within integer size limits
            int effectiveBlockCount = info.BlockNumber < blockCount - 1 ? (int)info.BlockNumber + 1 : (int)blockCount;
            int tempBlockCount = effectiveBlockCount + 1;
            ArrayPoolList<UInt256> baseFeePerGas = new(tempBlockCount, tempBlockCount);
            ArrayPoolList<UInt256> baseFeePerBlobGas = new(tempBlockCount, tempBlockCount);
            ArrayPoolList<double> gasUsedRatio = new(effectiveBlockCount, effectiveBlockCount);
            ArrayPoolList<double> blobGasUsedRatio = new(effectiveBlockCount, effectiveBlockCount);
            ArrayPoolList<ArrayPoolList<UInt256>>? rewards = withRewards
                ? new ArrayPoolList<ArrayPoolList<UInt256>>(effectiveBlockCount, effectiveBlockCount)
                : null;

            ulong oldestBlockNumber = info.BlockNumber;
            baseFeePerGas[effectiveBlockCount] = info.BaseFeePerGasEst;
            baseFeePerBlobGas[effectiveBlockCount] = info.BaseFeePerBlobGasEst;

            while (historyInfo is not null && effectiveBlockCount-- > 0)
            {
                info = historyInfo.Value;
                if (rewards is not null)
                {
                    // A cached block whose body is gone by the time its rewards are needed ends the walk like an unavailable ancestor.
                    if (CalculateRewardsPercentiles(info, rewardPercentiles) is not { } rewardsInBlock)
                    {
                        effectiveBlockCount++;
                        historyInfo = null;
                        break;
                    }

                    rewards[effectiveBlockCount] = rewardsInBlock;
                }
                oldestBlockNumber = info.BlockNumber;
                baseFeePerGas[effectiveBlockCount] = info.BlockBaseFeePerGas;
                baseFeePerBlobGas[effectiveBlockCount] = info.BaseFeePerBlobGas;
                gasUsedRatio[effectiveBlockCount] = info.GasUsedRatio;
                blobGasUsedRatio[effectiveBlockCount] = info.BlobGasUsedRatio;
                // The oldest requested block needs no parent.
                historyInfo = effectiveBlockCount == 0 || info.ParentHash is null ? null : GetHistorySearchInfo(info.ParentHash, info.BlockNumber - 1, withRewards);
            }

            // An unavailable ancestor ends the walk early; the leading slots were never filled,
            // so drop them to keep index 0 aligned with oldestBlock.
            if (historyInfo is null && effectiveBlockCount > 0)
            {
                DropLeading(baseFeePerGas, effectiveBlockCount);
                DropLeading(baseFeePerBlobGas, effectiveBlockCount);
                DropLeading(gasUsedRatio, effectiveBlockCount);
                DropLeading(blobGasUsedRatio, effectiveBlockCount);
                if (rewards is not null) DropLeading(rewards, effectiveBlockCount);
            }

            TryRunCleanup();

            return ResultWrapper<FeeHistoryResults>.Success(new(oldestBlockNumber, baseFeePerGas, gasUsedRatio, baseFeePerBlobGas, blobGasUsedRatio, rewards));
        }

        private static void DropLeading<T>(ArrayPoolList<T> list, int count)
        {
            Span<T> items = list.AsSpan();
            items[count..].CopyTo(items);
            list.Truncate(items.Length - count);
        }

        private void TryRunCleanup()
        {
            ulong headNumber = _blockTree.Head?.Number ?? 0;
            ulong lastCleanupHeadBlockNumber = _lastCleanupHeadBlockNumber;
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

        private ArrayPoolList<UInt256>? CalculateRewardsPercentiles(
            BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles) => blockInfo.BlockTransactionsLength == 0
                ? new ArrayPoolList<UInt256>(rewardPercentiles.Length, rewardPercentiles.Length)
                : GetRewards(blockInfo) is { } rewards
                    ? CalculatePercentileValues(rewardPercentiles, rewards)
                    : null;

        private BlockRewards? GetRewards(BlockFeeHistorySearchInfo blockInfo)
        {
            StrongBox<BlockRewards?> rewards = blockInfo.Rewards;
            if (rewards.Value is null && _blockTree.FindBlock(blockInfo.BlockHash, AncestorLookupOptions, blockInfo.BlockNumber) is { } block)
            {
                rewards.Value = GetRewardsInBlock(block, _specProvider.GetSpec(block.Header));
            }

            return rewards.Value;
        }

        private BlockRewards GetRewardsInBlock(Block block, IReleaseSpec spec)
        {
            Transaction[] txs = block.Transactions;
            using ArrayPoolListRef<ulong> gasUsed = new(txs.Length, txs.Length);
            Span<ulong> gasUsedSpan = gasUsed.AsSpan();
            if (txs.Length > 0 && !TryGetReceiptsGasUsed(block, spec.IsEip8116Enabled, gasUsedSpan))
            {
                // If no receipts available, approximate on GasLimit
                // We could just go with null here too and just don't return percentiles
                for (int i = 0; i < txs.Length; i++)
                {
                    gasUsedSpan[i] = txs[i].GasLimit;
                }
            }

            List<RewardInfo> rewardInfos = [with(txs.Length)];
            ulong gasUsedTotal = 0;
            for (int i = 0; i < txs.Length; i++)
            {
                txs[i].TryCalculatePremiumPerGas(block.BaseFeePerGas, out UInt256 premiumPerGas);
                rewardInfos.Add(new RewardInfo(gasUsedSpan[i], premiumPerGas));
                gasUsedTotal += gasUsedSpan[i];
            }

            CollectionsMarshal.AsSpan(rewardInfos).Sort(default(RewardInfoByPremiumAscendingComparer));

            return new BlockRewards(gasUsedTotal, rewardInfos);
        }

        /// <summary>Reads each receipt's gas used, through the receipts iterator when the storage offers one, so its logs and bloom are not decoded.</summary>
        /// <returns>Whether there is exactly one receipt per transaction.</returns>
        private bool TryGetReceiptsGasUsed(Block block, bool isEip8116Enabled, Span<ulong> gasUsed)
        {
            if (_receiptStorage.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator))
            {
                int count = 0;
                using ReceiptsIterator receipts = iterator;
                while (receipts.TryGetNext(out TxReceiptStructRef receipt))
                {
                    if (count == gasUsed.Length) return false;
                    gasUsed[count++] = receipt.GasUsedTotal;
                }

                if (count != gasUsed.Length) return false;
            }
            else
            {
                TxReceipt[] receipts = _receiptStorage.Get(block, false);
                if (receipts.Length != gasUsed.Length) return false;
                for (int i = 0; i < receipts.Length; i++)
                {
                    gasUsed[i] = receipts[i].GasUsedTotal;
                }
            }

            // EIP-8116 receipts carry each transaction's own gas used; earlier receipts carry the block's running total.
            if (!isEip8116Enabled)
            {
                for (int i = gasUsed.Length - 1; i > 0; i--)
                {
                    gasUsed[i] -= gasUsed[i - 1];
                }
            }

            return true;
        }

        private static ArrayPoolList<UInt256> CalculatePercentileValues(
            double[] rewardPercentiles,
            BlockRewards blockRewards)
        {
            List<RewardInfo> rewardsInBlock = blockRewards.SortedByPremium;
            ulong sumGasUsed = rewardsInBlock[0].GasUsed;
            int txIndex = 0;
            ArrayPoolList<UInt256> percentileValues = [with(rewardPercentiles.Length)];

            foreach (double percentile in rewardPercentiles)
            {
                ulong thresholdGasUsed = (ulong)(blockRewards.GasUsedTotal * percentile / 100);
                while (txIndex + 1 < rewardsInBlock.Count && sumGasUsed < thresholdGasUsed)
                {
                    txIndex++;
                    sumGasUsed += rewardsInBlock[txIndex].GasUsed;
                }

                percentileValues.Add(rewardsInBlock[txIndex].PremiumPerGas);
            }

            return percentileValues;
        }

        private static ResultWrapper<FeeHistoryResults> Validate(ulong blockCount, BlockParameter newestBlock, double[] rewardPercentiles)
        {
            if (newestBlock.Type == BlockParameterType.BlockHash)
            {
                return ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Is not correct block number", ErrorCodes.InvalidParams);
            }

            if (blockCount < 1)
            {
                return ResultWrapper<FeeHistoryResults>.Fail($"blockCount: Value {blockCount} is less than 1", ErrorCodes.InvalidParams);
            }

            if (rewardPercentiles.Length > RewardPercentilesLengthLimit)
            {
                return ResultWrapper<FeeHistoryResults>.Fail(
                    $"rewardPercentiles: {rewardPercentiles.Length} is over the query limit {RewardPercentilesLengthLimit}.",
                    ErrorCodes.InvalidParams);
            }

            double previousPercentile = -1;
            for (int i = 0; i < rewardPercentiles.Length; i++)
            {
                double currentPercentile = rewardPercentiles[i];
                if (currentPercentile is > 100 or < 0)
                {
                    return ResultWrapper<FeeHistoryResults>.Fail(
                        "rewardPercentiles: Some values are below 0 or greater than 100.",
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

            return _validationPassed;
        }

        public void Dispose() => _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
    }
}
