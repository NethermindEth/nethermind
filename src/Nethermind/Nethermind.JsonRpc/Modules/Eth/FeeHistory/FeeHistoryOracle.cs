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
using Nethermind.JsonRpc.Exceptions;
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
        private readonly ulong _maxBlockCount;
        private readonly int _maxUncachedRequests;
        private int _uncachedRequests;

        public FeeHistoryOracle(IBlockTree blockTree, IReceiptStorage receiptStorage, ISpecProvider specProvider, IJsonRpcConfig jsonRpcConfig, ulong? maxDistanceFromHead = null)
        {
            _blockTree = blockTree;
            _receiptStorage = receiptStorage;
            _specProvider = specProvider;
            _oldestBlockDistanceFromHeadAllowedInCache = maxDistanceFromHead ?? MaxBlockCount + 4;
            _maxBlockCount = (ulong)Math.Clamp(jsonRpcConfig.FeeHistoryMaxBlockCount, 1, MaxBlockCount);
            _maxUncachedRequests = jsonRpcConfig.FeeHistoryMaxConcurrentRequests ?? Math.Max(1, Environment.ProcessorCount / 4);
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
                SaveHistorySearchInfo(e.Block);
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
            // Total of the per-tx reward weights. Not the header GasUsed: that is pre-refund under
            // EIP-7778, and it never matched the GasLimit weights used when receipts are unavailable.
            ulong RewardsGasUsedTotal,
            int BlockTransactionsLength,
            List<RewardInfo> RewardsInBlocks);

        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(BlockParameter blockParameter, ref UncachedRequestSlot uncachedRequestSlot)
        {
            if (blockParameter.Type == BlockParameterType.BlockHash
                && _feeHistoryCache.TryGetValue(blockParameter.BlockHash!, out BlockFeeHistorySearchInfo i))
            {
                return i;
            }

            // A cached block needs only its header to be found, not its body decoded.
            if (_blockTree.FindHeader(blockParameter)?.Hash is { } hash
                && _feeHistoryCache.TryGetValue(hash, out BlockFeeHistorySearchInfo cached))
            {
                return cached;
            }

            uncachedRequestSlot.EnsureAcquired();
            Block block = _blockTree.FindBlock(blockParameter);
            return block is null
                ? null
                : _feeHistoryCache.TryGetValue(block.Hash!, out BlockFeeHistorySearchInfo info)
                    ? info
                    : SaveHistorySearchInfo(block);
        }

        private BlockFeeHistorySearchInfo? GetHistorySearchInfo(Hash256 blockHash, ulong blockNumber, ref UncachedRequestSlot uncachedRequestSlot)
        {
            if (!_feeHistoryCache.TryGetValue(blockHash, out BlockFeeHistorySearchInfo info))
            {
                const BlockTreeLookupOptions options = BlockTreeLookupOptions.ExcludeTxHashes |
                                                       BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                                                       BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

                uncachedRequestSlot.EnsureAcquired();
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

                List<RewardInfo> rewardsInBlock = GetRewardsInBlock(b, out ulong rewardsGasUsedTotal);

                return new(
                    b.Number,
                    b.BaseFeePerGas,
                    BaseFeeCalculator.Calculate(b.Header, _specProvider.GetSpecFor1559(b.Number + 1)),
                    feePerBlobGas == UInt256.MaxValue ? UInt256.Zero : feePerBlobGas,
                    nextFeePerBlobGas,
                    b.GasUsed / (double)b.GasLimit,
                    blobGasUsedRatio,
                    b.ParentHash,
                    rewardsGasUsedTotal,
                    b.Transactions.Length,
                    rewardsInBlock);
            }

            BlockFeeHistorySearchInfo historyInfo = BlockFeeHistorySearchInfoFromBlock(block);

            if (ShouldCache(block))
            {
                _feeHistoryCache[block.Hash!] = historyInfo;
            }

            return historyInfo;
        }

        private bool ShouldCache(Block block) =>
            _blockTree.Head is null || block.Number >= _blockTree.Head.Number.SaturatingSub(_oldestBlockDistanceFromHeadAllowedInCache);

        public ResultWrapper<FeeHistoryResults> GetFeeHistory(
            ulong blockCount,
            BlockParameter newestBlock,
            double[] rewardPercentiles)
        {
            ArgumentNullException.ThrowIfNull(rewardPercentiles);
            if (blockCount > _maxBlockCount)
                blockCount = _maxBlockCount;

            ResultWrapper<FeeHistoryResults> initialCheckResult = Validate(blockCount, newestBlock, rewardPercentiles);
            if (initialCheckResult.Result.ResultType == ResultType.Failure)
            {
                return initialCheckResult;
            }

            UncachedRequestSlot uncachedRequestSlot = new(this);
            try
            {
                return GetFeeHistory(blockCount, newestBlock, rewardPercentiles, ref uncachedRequestSlot);
            }
            finally
            {
                uncachedRequestSlot.Dispose();
            }
        }

        private ResultWrapper<FeeHistoryResults> GetFeeHistory(
            ulong blockCount,
            BlockParameter newestBlock,
            double[] rewardPercentiles,
            ref UncachedRequestSlot uncachedRequestSlot)
        {
            BlockFeeHistorySearchInfo? historyInfo = GetHistorySearchInfo(newestBlock, ref uncachedRequestSlot);

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
            ArrayPoolList<ArrayPoolList<UInt256>>? rewards = rewardPercentiles.Length > 0
                ? new ArrayPoolList<ArrayPoolList<UInt256>>(effectiveBlockCount, effectiveBlockCount)
                : null;

            ulong oldestBlockNumber = info.BlockNumber;
            baseFeePerGas[effectiveBlockCount] = info.BaseFeePerGasEst;
            baseFeePerBlobGas[effectiveBlockCount] = info.BaseFeePerBlobGasEst;

            try
            {
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
                    historyInfo = effectiveBlockCount == 0 || info.ParentHash is null ? null : GetHistorySearchInfo(info.ParentHash, info.BlockNumber - 1, ref uncachedRequestSlot);
                }
            }
            catch
            {
                // The walk throws when it reaches the first uncached block while all slots are taken.
                baseFeePerGas.Dispose();
                baseFeePerBlobGas.Dispose();
                gasUsedRatio.Dispose();
                blobGasUsedRatio.Dispose();
                rewards?.DisposeRecursive();
                throw;
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

        /// <summary>
        /// One of the limited slots for a request that has to read blocks or receipts from the database.
        /// </summary>
        /// <remarks>
        /// Building the fee data of a block not in the cache reads and decodes its body and receipts, which costs
        /// far more than serving cached data, so the number of such requests running at once is capped.
        /// The slot is taken right before the first such read and held until the request ends, so requests served
        /// entirely from the cache stay unlimited.
        /// </remarks>
        private struct UncachedRequestSlot(FeeHistoryOracle oracle) : IDisposable
        {
            private bool _acquired;

            /// <exception cref="LimitExceededException">All slots are taken.</exception>
            public void EnsureAcquired()
            {
                if (_acquired || oracle._maxUncachedRequests == 0) return;

                if (Interlocked.Increment(ref oracle._uncachedRequests) > oracle._maxUncachedRequests)
                {
                    Interlocked.Decrement(ref oracle._uncachedRequests);
                    throw new LimitExceededException($"Too many concurrent eth_feeHistory requests for blocks outside the cache. Limit: {oracle._maxUncachedRequests}.");
                }

                _acquired = true;
            }

            public void Dispose()
            {
                if (!_acquired) return;
                _acquired = false;
                Interlocked.Decrement(ref oracle._uncachedRequests);
            }
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

        private static ArrayPoolList<UInt256>? CalculateRewardsPercentiles(
            BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles) => blockInfo.BlockTransactionsLength == 0
                ? new ArrayPoolList<UInt256>(rewardPercentiles.Length, rewardPercentiles.Length)
                : CalculatePercentileValues(blockInfo, rewardPercentiles, blockInfo.RewardsInBlocks);

        private List<RewardInfo> GetRewardsInBlock(Block block, out ulong gasUsedTotal)
        {
            Transaction[] txs = block.Transactions;
            using ArrayPoolListRef<ulong> gasUsed = new(txs.Length, txs.Length);
            Span<ulong> gasUsedSpan = gasUsed.AsSpan();
            if (!TryGetGasUsedFromReceipts(block, gasUsedSpan))
            {
                // If no receipts available, approximate on GasLimit
                // We could just go with null here too and just don't return percentiles
                for (int i = 0; i < txs.Length; i++)
                {
                    gasUsedSpan[i] = txs[i].GasLimit;
                }
            }

            List<RewardInfo> rewardInfos = new(txs.Length);
            gasUsedTotal = 0;
            for (int i = 0; i < txs.Length; i++)
            {
                txs[i].TryCalculatePremiumPerGas(block.BaseFeePerGas, out UInt256 premiumPerGas);
                rewardInfos.Add(new RewardInfo(gasUsedSpan[i], premiumPerGas));
                gasUsedTotal += gasUsedSpan[i];
            }

            CollectionsMarshal.AsSpan(rewardInfos).Sort(default(RewardInfoByPremiumAscendingComparer));

            return rewardInfos;
        }

        /// <remarks>
        /// Iterates the stored receipts without materializing them: their logs, which a fee history never needs,
        /// make up most of their size.
        /// </remarks>
        private bool TryGetGasUsedFromReceipts(Block block, Span<ulong> gasUsed)
        {
            if (!_receiptStorage.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator))
            {
                iterator = new ReceiptsIterator(_receiptStorage.Get(block, false));
            }

            using ReceiptsIterator receipts = iterator;
            int count = 0;
            ulong previousGasUsedTotal = 0;
            while (receipts.TryGetNext(out TxReceiptStructRef receipt))
            {
                if (count == gasUsed.Length) return false;
                gasUsed[count++] = receipt.GasUsedTotal - previousGasUsedTotal;
                previousGasUsedTotal = receipt.GasUsedTotal;
            }

            return count == gasUsed.Length;
        }

        private static ArrayPoolList<UInt256> CalculatePercentileValues(
            BlockFeeHistorySearchInfo blockInfo,
            double[] rewardPercentiles,
            List<RewardInfo> rewardsInBlock)
        {
            ulong sumGasUsed = rewardsInBlock[0].GasUsed;
            int txIndex = 0;
            ArrayPoolList<UInt256> percentileValues = new(rewardPercentiles.Length);

            foreach (double percentile in rewardPercentiles)
            {
                ulong thresholdGasUsed = (ulong)(blockInfo.RewardsGasUsedTotal * percentile / 100);
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
