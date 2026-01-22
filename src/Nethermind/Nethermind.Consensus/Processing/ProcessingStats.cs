// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using DbMetrics = Nethermind.Db.Metrics;

namespace Nethermind.Consensus.Processing
{
    public class BlockStatistics
    {
        public long BlockCount { get; internal set; }
        public long BlockFrom { get; internal set; }
        public long BlockTo { get; internal set; }
        public double ProcessingMs { get; internal set; }
        public double SlotMs { get; internal set; }
        [JsonPropertyName("mgasPerSecond")]
        public double MGasPerSecond { get; internal set; }
        public float MinGas { get; internal set; }
        public float MedianGas { get; internal set; }
        public float AveGas { get; internal set; }
        public float MaxGas { get; internal set; }
        public long GasLimit { get; internal set; }
    }
    //TODO Consult on disabling of such metrics from configuration
    internal class ProcessingStats
    {
        private static readonly DefaultObjectPool<BlockData> _dataPool = new(new BlockDataPolicy(), 16);
        private readonly Action<BlockData> _executeFromThreadPool;
        public event EventHandler<BlockStatistics>? NewProcessingStatistics;
        private readonly IStateReader _stateReader;
        private readonly ILogger _logger;
        private readonly ILogger _slowBlockLogger;
        private readonly Stopwatch _runStopwatch = new();

        /// <summary>
        /// Threshold in milliseconds for slow block logging (default: 1000ms).
        /// </summary>
        private const long SlowBlockThresholdMs = 1000;

        private bool _showBlobs;
        private long _lastElapsedRunningMicroseconds;
        private long _lastReportMs;
        private long _startCallOps;
        private long _startEmptyCalls;
        private long _startSLoadOps;
        private long _startSStoreOps;
        private long _startSelfDestructOps;
        private long _startOpCodes;
        private long _startCreateOps;
        private long _startContractsAnalyzed;
        private long _startCachedContractsUsed;
        private long _startAccountReads;
        private long _startStorageReads;
        private long _startCodeReads;
        private long _startCodeBytesRead;
        private long _startAccountWrites;
        private long _startStorageWrites;
        private long _startCodeWrites;
        private long _startCodeBytesWritten;
        // Timing metrics for cross-client slow block logging
        private long _startStateReadTime;
        private long _startStateHashTime;
        private long _startCommitTime;
        // Cache statistics for cross-client slow block logging
        private long _startAccountCacheHits;
        private long _startAccountCacheMisses;
        private long _startStorageCacheHits;
        private long _startStorageCacheMisses;
        private long _startCodeCacheHits;
        private long _startCodeCacheMisses;
        // EIP-7702 delegation tracking for cross-client slow block logging
        private long _startEip7702DelegationsSet;
        private long _startEip7702DelegationsCleared;
        private double _chunkMGas;
        private long _chunkProcessingMicroseconds;
        private long _chunkTx;
        private long _chunkBlobs;
        private long _chunkBlocks;
        private long _opCodes;
        private long _callOps;
        private long _emptyCalls;
        private long _sLoadOps;
        private long _sStoreOps;
        private long _selfDestructOps;
        private long _createOps;
        private long _contractsAnalyzed;
        private long _cachedContractsUsed;

        public ProcessingStats(IStateReader stateReader, ILogger logger, ILogger? slowBlockLogger = null)
        {
            _executeFromThreadPool = ExecuteFromThreadPool;

            _stateReader = stateReader;
            _logger = logger;
            _slowBlockLogger = slowBlockLogger ?? logger;

            // the line below just to avoid compilation errors
            if (_logger.IsTrace) _logger.Trace($"Processing Stats in debug mode?: {_logger.IsDebug}");
#if DEBUG
            _logger.SetDebugMode();
#endif
        }

        public void CaptureStartStats()
        {
            _startSLoadOps = Evm.Metrics.ThreadLocalSLoadOpcode;
            _startSStoreOps = Evm.Metrics.ThreadLocalSStoreOpcode;
            _startCallOps = Evm.Metrics.ThreadLocalCalls;
            _startEmptyCalls = Evm.Metrics.ThreadLocalEmptyCalls;
            _startCachedContractsUsed = Evm.Metrics.ThreadLocalCodeDbCache;
            _startContractsAnalyzed = Evm.Metrics.ThreadLocalContractsAnalysed;
            _startCreateOps = Evm.Metrics.ThreadLocalCreates;
            _startSelfDestructOps = Evm.Metrics.ThreadLocalSelfDestructs;
            _startOpCodes = Evm.Metrics.ThreadLocalOpCodes;
            _startAccountReads = Evm.Metrics.ThreadLocalAccountReads;
            _startStorageReads = Evm.Metrics.ThreadLocalStorageReads;
            _startCodeReads = Evm.Metrics.ThreadLocalCodeReads;
            _startCodeBytesRead = Evm.Metrics.ThreadLocalCodeBytesRead;
            _startAccountWrites = Evm.Metrics.ThreadLocalAccountWrites;
            _startStorageWrites = Evm.Metrics.ThreadLocalStorageWrites;
            _startCodeWrites = Evm.Metrics.ThreadLocalCodeWrites;
            _startCodeBytesWritten = Evm.Metrics.ThreadLocalCodeBytesWritten;
            // Timing metrics for cross-client slow block logging
            _startStateReadTime = Evm.Metrics.ThreadLocalStateReadTime;
            _startStateHashTime = Evm.Metrics.ThreadLocalStateHashTime;
            _startCommitTime = Evm.Metrics.ThreadLocalCommitTime;
            // Cache statistics for cross-client slow block logging
            _startAccountCacheHits = DbMetrics.ThreadLocalStateTreeCacheHits;
            _startAccountCacheMisses = DbMetrics.ThreadLocalStateTreeReads;
            _startStorageCacheHits = DbMetrics.ThreadLocalStorageTreeCacheHits;
            _startStorageCacheMisses = DbMetrics.ThreadLocalStorageTreeReads;
            _startCodeCacheHits = Evm.Metrics.ThreadLocalCodeDbCache;
            _startCodeCacheMisses = Evm.Metrics.ThreadLocalCodeReads;
            // EIP-7702 delegation tracking
            _startEip7702DelegationsSet = Evm.Metrics.ThreadLocalEip7702DelegationsSet;
            _startEip7702DelegationsCleared = Evm.Metrics.ThreadLocalEip7702DelegationsCleared;
        }

        public void UpdateStats(Block? block, BlockHeader? baseBlock, long blockProcessingTimeInMicros)
        {
            if (block is null) return;

            BlockData blockData = _dataPool.Get();
            blockData.Block = block;
            blockData.BaseBlock = baseBlock;
            blockData.RunningMicroseconds = _runStopwatch.ElapsedMicroseconds();
            blockData.RunMicroseconds = (_runStopwatch.ElapsedMicroseconds() - _lastElapsedRunningMicroseconds);
            blockData.StartOpCodes = _startOpCodes;
            blockData.StartSLoadOps = _startSLoadOps;
            blockData.StartSStoreOps = _startSStoreOps;
            blockData.StartCallOps = _startCallOps;
            blockData.StartEmptyCalls = _startEmptyCalls;
            blockData.StartCachedContractsUsed = _startCachedContractsUsed;
            blockData.StartContractsAnalyzed = _startContractsAnalyzed;
            blockData.StartCreateOps = _startCreateOps;
            blockData.StartSelfDestructOps = _startSelfDestructOps;
            blockData.ProcessingMicroseconds = blockProcessingTimeInMicros;
            blockData.CurrentOpCodes = Evm.Metrics.ThreadLocalOpCodes;
            blockData.CurrentSLoadOps = Evm.Metrics.ThreadLocalSLoadOpcode;
            blockData.CurrentSStoreOps = Evm.Metrics.ThreadLocalSStoreOpcode;
            blockData.CurrentCallOps = Evm.Metrics.ThreadLocalCalls;
            blockData.CurrentEmptyCalls = Evm.Metrics.ThreadLocalEmptyCalls;
            blockData.CurrentCachedContractsUsed = Evm.Metrics.ThreadLocalCodeDbCache;
            blockData.CurrentContractsAnalyzed = Evm.Metrics.ThreadLocalContractsAnalysed;
            blockData.CurrentCreatesOps = Evm.Metrics.ThreadLocalCreates;
            blockData.CurrentSelfDestructOps = Evm.Metrics.ThreadLocalSelfDestructs;
            // Capture state access metrics for cross-client slow block logging
            blockData.CurrentAccountReads = Evm.Metrics.ThreadLocalAccountReads;
            blockData.CurrentStorageReads = Evm.Metrics.ThreadLocalStorageReads;
            blockData.CurrentCodeReads = Evm.Metrics.ThreadLocalCodeReads;
            blockData.CurrentCodeBytesRead = Evm.Metrics.ThreadLocalCodeBytesRead;
            blockData.CurrentAccountWrites = Evm.Metrics.ThreadLocalAccountWrites;
            blockData.CurrentStorageWrites = Evm.Metrics.ThreadLocalStorageWrites;
            blockData.StartAccountReads = _startAccountReads;
            blockData.StartStorageReads = _startStorageReads;
            blockData.StartCodeReads = _startCodeReads;
            blockData.StartCodeBytesRead = _startCodeBytesRead;
            blockData.StartAccountWrites = _startAccountWrites;
            blockData.StartStorageWrites = _startStorageWrites;
            blockData.CurrentCodeWrites = Evm.Metrics.ThreadLocalCodeWrites;
            blockData.CurrentCodeBytesWritten = Evm.Metrics.ThreadLocalCodeBytesWritten;
            blockData.StartCodeWrites = _startCodeWrites;
            blockData.StartCodeBytesWritten = _startCodeBytesWritten;
            // Capture timing metrics for cross-client slow block logging
            blockData.CurrentStateReadTime = Evm.Metrics.ThreadLocalStateReadTime;
            blockData.CurrentStateHashTime = Evm.Metrics.ThreadLocalStateHashTime;
            blockData.CurrentCommitTime = Evm.Metrics.ThreadLocalCommitTime;
            blockData.StartStateReadTime = _startStateReadTime;
            blockData.StartStateHashTime = _startStateHashTime;
            blockData.StartCommitTime = _startCommitTime;
            // Capture cache statistics from existing Db.Metrics counters
            blockData.CurrentAccountCacheHits = DbMetrics.ThreadLocalStateTreeCacheHits;
            blockData.CurrentAccountCacheMisses = DbMetrics.ThreadLocalStateTreeReads;
            blockData.CurrentStorageCacheHits = DbMetrics.ThreadLocalStorageTreeCacheHits;
            blockData.CurrentStorageCacheMisses = DbMetrics.ThreadLocalStorageTreeReads;
            blockData.CurrentCodeCacheHits = Evm.Metrics.ThreadLocalCodeDbCache;
            blockData.CurrentCodeCacheMisses = Evm.Metrics.ThreadLocalCodeReads;
            blockData.StartAccountCacheHits = _startAccountCacheHits;
            blockData.StartAccountCacheMisses = _startAccountCacheMisses;
            blockData.StartStorageCacheHits = _startStorageCacheHits;
            blockData.StartStorageCacheMisses = _startStorageCacheMisses;
            blockData.StartCodeCacheHits = _startCodeCacheHits;
            blockData.StartCodeCacheMisses = _startCodeCacheMisses;
            // EIP-7702 delegation tracking
            blockData.CurrentEip7702DelegationsSet = Evm.Metrics.ThreadLocalEip7702DelegationsSet;
            blockData.CurrentEip7702DelegationsCleared = Evm.Metrics.ThreadLocalEip7702DelegationsCleared;
            blockData.StartEip7702DelegationsSet = _startEip7702DelegationsSet;
            blockData.StartEip7702DelegationsCleared = _startEip7702DelegationsCleared;

            CaptureReportData(blockData);
        }

        private void CaptureReportData(in BlockData data) => ThreadPool.UnsafeQueueUserWorkItem(_executeFromThreadPool, data, preferLocal: false);

        private readonly Lock _reportLock = new();
        void ExecuteFromThreadPool(BlockData data)
        {
            try
            {
                lock (_reportLock)
                {
                    GenerateReport(data);
                }
            }
            catch (Exception ex)
            {
                // Don't allow exception to escape to ThreadPool
                if (_logger.IsError) _logger.Error("Error when generating processing statistics", ex);
            }
            finally
            {
                _dataPool.Return(data);
            }
        }

        private void GenerateReport(BlockData data)
        {
            const long weiToEth = 1_000_000_000_000_000_000;
            const string resetColor = "\u001b[37m";
            const string whiteText = "\u001b[97m";
            const string yellowText = "\u001b[93m";
            const string orangeText = "\u001b[38;5;208m";
            const string redText = "\u001b[38;5;196m";
            const string greenText = "\u001b[92m";
            const string darkGreenText = "\u001b[32m";
            const string darkCyanText = "\u001b[36m";
            const string blueText = "\u001b[94m";
            const string darkGreyText = resetColor; // "\u001b[90m";

            Block? block = data.Block;
            if (block is null) return;

            long blockNumber = data.Block.Number;
            double chunkMGas = (_chunkMGas += block.GasUsed / 1_000_000.0);

            // We want the rate here
            double mgas = block.GasUsed / 1_000_000.0;
            double timeSec = data.ProcessingMicroseconds / 1_000_000.0;
            double mgasPerSec = timeSec > 0 ? mgas / timeSec : 0;
            Metrics.BlockMGasPerSec.Observe(mgasPerSec);
            Metrics.BlockProcessingTimeMicros.Observe(data.ProcessingMicroseconds);

            // Log slow blocks in JSON format for cross-client performance analysis
            long processingMs = data.ProcessingMicroseconds / 1000;
            if (processingMs > SlowBlockThresholdMs)
            {
                LogSlowBlock(block, data, processingMs, mgasPerSec);
            }

            Metrics.Mgas += block.GasUsed / 1_000_000.0;
            Transaction[] txs = block.Transactions;
            double chunkMicroseconds = (_chunkProcessingMicroseconds += data.ProcessingMicroseconds);
            double chunkTx = (_chunkTx += txs.Length);

            long chunkBlocks = (++_chunkBlocks);

            Metrics.Blocks = blockNumber;
            Metrics.BlockchainHeight = blockNumber;

            Metrics.Transactions += txs.Length;
            Metrics.TotalDifficulty = block.TotalDifficulty ?? UInt256.Zero;
            Metrics.LastDifficulty = block.Difficulty;
            Metrics.GasUsed = block.GasUsed;
            Metrics.GasLimit = block.GasLimit;

            long chunkOpCodes = (_opCodes += data.CurrentOpCodes - data.StartOpCodes);
            long chunkCalls = (_callOps += data.CurrentCallOps - data.StartCallOps);
            long chunkEmptyCalls = (_emptyCalls += data.CurrentEmptyCalls - data.StartEmptyCalls);
            long chunkSload = (_sLoadOps += data.CurrentSLoadOps - data.StartSLoadOps);
            long chunkSstore = (_sStoreOps += data.CurrentSStoreOps - data.StartSStoreOps);
            long chunkSelfDestructs = (_selfDestructOps += data.CurrentSelfDestructOps - data.StartSelfDestructOps);
            long chunkCreates = (_createOps += data.CurrentCreatesOps - data.StartCreateOps);
            long contractsAnalysed = (_contractsAnalyzed += data.CurrentContractsAnalyzed - data.StartContractsAnalyzed);
            long cachedContractsUsed = (_cachedContractsUsed += data.CurrentCachedContractsUsed - data.StartCachedContractsUsed);

            Address beneficiary = block.Header.GasBeneficiary ?? Address.Zero;
            Transaction lastTx = txs.Length > 0 ? txs[^1] : null;
            bool isMev = false;
            if (lastTx?.To is not null && (lastTx.SenderAddress == beneficiary || _alternateMevPayees.Contains(lastTx.SenderAddress)))
            {
                // Mev reward with in last tx
                isMev = true;
            }

            if (data.BaseBlock is null || !_stateReader.HasStateForBlock(data.BaseBlock) || block.StateRoot is null || !_stateReader.HasStateForBlock(block.Header))
                return;

            UInt256 rewards = default;
            try
            {
                if (!isMev)
                {
                    rewards = CalculateBalanceChange(data.BaseBlock, block.Header, beneficiary);
                }
                else
                {
                    // Sometimes the beneficiary has done their own balance changing tx
                    // So prefer the mev reward tx value
                    rewards = lastTx.Value;
                    if (rewards.IsZero)
                    {
                        rewards = CalculateBalanceChange(data.BaseBlock, block.Header, lastTx.To);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Error when calculating block rewards", ex);
            }

            foreach (Transaction tx in txs)
            {
                _chunkBlobs += tx.GetBlobCount();
            }
            long blobs = _chunkBlobs;
            if (blobs > 0)
            {
                _showBlobs = true;
            }

            long reportMs = Environment.TickCount64;
            if (reportMs - _lastReportMs > 1000 || _logger.IsDebug)
            {
                _lastReportMs = reportMs;
            }
            else
            {
                return;
            }

            _chunkBlobs = 0;
            _chunkBlocks = 0;
            _chunkMGas = 0;
            _chunkTx = 0;
            _chunkProcessingMicroseconds = 0;
            _opCodes = 0;
            _callOps = 0;
            _emptyCalls = 0;
            _sLoadOps = 0;
            _sStoreOps = 0;
            _selfDestructOps = 0;
            _createOps = 0;
            _contractsAnalyzed = 0;
            _cachedContractsUsed = 0;

            double mgasPerSecond;
            if (chunkMicroseconds == 0)
            {
                mgasPerSecond = -1;
            }
            else
            {
                mgasPerSecond = chunkMGas / chunkMicroseconds * 1_000_000.0;

                if (chunkMGas != 0)
                {
                    Metrics.MgasPerSec = mgasPerSecond;
                }
            }

            double txps = chunkMicroseconds == 0 ? -1 : chunkTx / chunkMicroseconds * 1_000_000.0;
            double bps = chunkMicroseconds == 0 ? -1 : chunkBlocks / chunkMicroseconds * 1_000_000.0;
            double chunkMs = (chunkMicroseconds == 0 ? -1 : chunkMicroseconds / 1000.0);
            double runMs = (data.RunMicroseconds == 0 ? -1 : data.RunMicroseconds / 1000.0);
            string blockGas = Evm.Metrics.BlockMinGasPrice != float.MaxValue ? $"⛽ Gas gwei: {Evm.Metrics.BlockMinGasPrice:N3} .. {whiteText}{Math.Max(Evm.Metrics.BlockMinGasPrice, Evm.Metrics.BlockEstMedianGasPrice):N3}{resetColor} ({Evm.Metrics.BlockAveGasPrice:N3}) .. {Evm.Metrics.BlockMaxGasPrice:N3}" : "";
            string mgasColor = whiteText;

            NewProcessingStatistics?.Invoke(this, new BlockStatistics()
            {
                BlockCount = chunkBlocks,
                BlockFrom = block.Number - chunkBlocks + 1,
                BlockTo = block.Number,

                ProcessingMs = chunkMs,
                SlotMs = runMs,
                MGasPerSecond = mgasPerSecond,
                MinGas = Evm.Metrics.BlockMinGasPrice,
                MedianGas = Math.Max(Evm.Metrics.BlockMinGasPrice, Evm.Metrics.BlockEstMedianGasPrice),
                AveGas = Evm.Metrics.BlockAveGasPrice,
                MaxGas = Evm.Metrics.BlockMaxGasPrice,
                GasLimit = block.GasLimit
            });

            _lastElapsedRunningMicroseconds = data.RunningMicroseconds;

            if (_logger.IsInfo)
            {
                if (chunkBlocks > 1)
                {
                    _logger.Info($"Processed    {block.Number - chunkBlocks + 1,10}...{block.Number,9}   | {chunkMs,10:N1} ms  | slot    {runMs,11:N0} ms |{blockGas}");
                }
                else
                {
                    mgasColor = (chunkMGas / (block.GasLimit / 16_000_000.0)) switch
                    {
                        // At 30M gas limit the values are in comments
                        > 15 => redText, // 28.125 MGas
                        > 14 => orangeText, // 26.25 MGas
                        > 13 => yellowText, // 24.375 MGas
                        > 10 => darkGreenText, // 18.75 MGas
                        > 7 => greenText, // 13.125 MGas
                        > 6 => darkGreenText, // 11.25 MGas
                        > 5 => whiteText, // 9.375 MGas
                        > 4 => resetColor, // 7.5 MGas
                        > 3 => darkCyanText, // 5.625 MGas
                        _ => blueText
                    };
                    var chunkColor = chunkMs switch
                    {
                        < 200 => greenText,
                        < 300 => darkGreenText,
                        < 500 => whiteText,
                        < 1000 => yellowText,
                        < 2000 => orangeText,
                        _ => redText
                    };
                    _logger.Info($"Processed          {block.Number,10}         | {chunkColor}{chunkMs,10:N1}{resetColor} ms  | slot    {runMs,11:N0} ms |{blockGas}");
                }

                string mgasPerSecondColor = (mgasPerSecond / (block.GasLimit / 1_000_000.0)) switch
                {
                    // At 30M gas limit the values are in comments
                    > 3 => greenText, // 90 MGas/s
                    > 2.5f => darkGreenText, // 75 MGas/s
                    > 2 => whiteText, // 60 MGas/s
                    > 1.5f => resetColor, // 45 MGas/s
                    > 1 => yellowText, // 30 MGas/s
                    > 0.5f => orangeText, // 15 MGas/s
                    _ => redText
                };
                var sstoreColor = chunkBlocks > 1 ? "" : chunkSstore switch
                {
                    > 3500 => redText,
                    > 2500 => orangeText,
                    > 2000 => yellowText,
                    > 1500 => whiteText,
                    > 900 when chunkCalls > 900 => whiteText,
                    _ => ""
                };
                var callsColor = chunkBlocks > 1 ? "" : chunkCalls switch
                {
                    > 3500 => redText,
                    > 2500 => orangeText,
                    > 2000 => yellowText,
                    > 1500 => whiteText,
                    > 900 when chunkSstore > 900 => whiteText,
                    _ => ""
                };
                var createsColor = chunkBlocks > 1 ? "" : chunkCreates switch
                {
                    > 300 => redText,
                    > 200 => orangeText,
                    > 150 => yellowText,
                    > 75 => whiteText,
                    _ => ""
                };

                var recoveryQueue = Metrics.RecoveryQueueSize;
                var processingQueue = Metrics.ProcessingQueueSize;

                _logger.Info($" Block{(chunkBlocks > 1 ? $"s  x{chunkBlocks,-9:N0} " : $"{(isMev ? " mb" : "   ")} {rewards.ToDecimal(null) / weiToEth,6:N4}{BlocksConfig.GasTokenTicker,4}")}{(chunkBlocks == 1 ? mgasColor : "")} {chunkMGas,8:F2}{resetColor} MGas    | {chunkTx,8:N0}   txs | calls {callsColor}{chunkCalls,10:N0}{resetColor} {darkGreyText}({chunkEmptyCalls,3:N0}){resetColor} | sload {chunkSload,7:N0} | sstore {sstoreColor}{chunkSstore,6:N0}{resetColor} | create {createsColor}{chunkCreates,3:N0}{resetColor}{(chunkSelfDestructs > 0 ? $"{darkGreyText}({-chunkSelfDestructs,3:N0}){resetColor}" : "")}");
                string blobsOrBlocksPerSec = _showBlobs switch
                {
                    true => $" blobs {blobs,3:N0} ",
                    _ => $"       {bps,10:F2} Blk/s "
                };

                if (recoveryQueue > 0 || processingQueue > 0)
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |{blobsOrBlocksPerSec}| recover {recoveryQueue,5:N0} | process {processingQueue,5:N0} | ops {chunkOpCodes,11:N0}");
                }
                else
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |{blobsOrBlocksPerSec}| exec code{resetColor} cache {cachedContractsUsed,7:N0} |{resetColor} new {contractsAnalysed,6:N0} | ops {chunkOpCodes,11:N0}");
                }
            }

            UInt256 CalculateBalanceChange(BlockHeader? startBlock, BlockHeader endBlock, Address beneficiary)
            {
                UInt256 beforeBalance = _stateReader.GetBalance(startBlock, beneficiary);
                UInt256 afterBalance = _stateReader.GetBalance(endBlock, beneficiary);
                return beforeBalance < afterBalance ? afterBalance - beforeBalance : default;
            }
        }

        /// <summary>
        /// Logs slow block execution statistics in JSON format for cross-client performance analysis.
        /// Follows the standardized execution metrics specification.
        /// </summary>
        private void LogSlowBlock(Block block, BlockData data, long processingMs, double mgasPerSec)
        {
            try
            {
                long sloadOps = data.CurrentSLoadOps - data.StartSLoadOps;
                long sstoreOps = data.CurrentSStoreOps - data.StartSStoreOps;
                long callOps = data.CurrentCallOps - data.StartCallOps;
                long createOps = data.CurrentCreatesOps - data.StartCreateOps;
                long cachedContracts = data.CurrentCachedContractsUsed - data.StartCachedContractsUsed;
                long analyzedContracts = data.CurrentContractsAnalyzed - data.StartContractsAnalyzed;

                // State access metrics for cross-client standardization
                long accountReads = data.CurrentAccountReads - data.StartAccountReads;
                long storageReads = data.CurrentStorageReads - data.StartStorageReads;
                long codeReads = data.CurrentCodeReads - data.StartCodeReads;
                long codeBytesRead = data.CurrentCodeBytesRead - data.StartCodeBytesRead;
                long accountWrites = data.CurrentAccountWrites - data.StartAccountWrites;
                long storageWrites = data.CurrentStorageWrites - data.StartStorageWrites;
                long codeWrites = data.CurrentCodeWrites - data.StartCodeWrites;
                long codeBytesWritten = data.CurrentCodeBytesWritten - data.StartCodeBytesWritten;

                // EIP-7702 delegation tracking
                long eip7702DelegationsSet = data.CurrentEip7702DelegationsSet - data.StartEip7702DelegationsSet;
                long eip7702DelegationsCleared = data.CurrentEip7702DelegationsCleared - data.StartEip7702DelegationsCleared;

                // Timing metrics for cross-client standardization (convert ticks to ms with sub-ms precision)
                // 1 tick = 100 nanoseconds, so 10000 ticks = 1 ms
                double stateReadMs = (data.CurrentStateReadTime - data.StartStateReadTime) / (double)TimeSpan.TicksPerMillisecond;
                double stateHashMs = (data.CurrentStateHashTime - data.StartStateHashTime) / (double)TimeSpan.TicksPerMillisecond;
                double commitMs = (data.CurrentCommitTime - data.StartCommitTime) / (double)TimeSpan.TicksPerMillisecond;
                // Convert from microseconds to milliseconds with sub-ms precision
                double totalMs = data.ProcessingMicroseconds / 1000.0;
                // Derive execution time by subtracting state I/O overhead from total
                double executionMs = totalMs - stateReadMs - stateHashMs - commitMs;
                if (executionMs < 0) executionMs = totalMs; // Fallback if timing not fully captured

                // Calculate cache deltas for this block
                long accountCacheHits = data.CurrentAccountCacheHits - data.StartAccountCacheHits;
                long accountCacheMisses = data.CurrentAccountCacheMisses - data.StartAccountCacheMisses;
                long storageCacheHits = data.CurrentStorageCacheHits - data.StartStorageCacheHits;
                long storageCacheMisses = data.CurrentStorageCacheMisses - data.StartStorageCacheMisses;
                long codeCacheHits = data.CurrentCodeCacheHits - data.StartCodeCacheHits;
                long codeCacheMisses = data.CurrentCodeCacheMisses - data.StartCodeCacheMisses;

                // Calculate hit rates
                double accountHitRate = CalculateHitRate(accountCacheHits, accountCacheMisses);
                double storageHitRate = CalculateHitRate(storageCacheHits, storageCacheMisses);
                double codeHitRate = CalculateHitRate(codeCacheHits, codeCacheMisses);

                var slowBlockLog = new
                {
                    level = "warn",
                    msg = "Slow block",
                    block = new
                    {
                        number = block.Number,
                        hash = block.Hash?.ToString() ?? "0x",
                        gas_used = block.GasUsed,
                        tx_count = block.Transactions.Length
                    },
                    timing = new
                    {
                        execution_ms = Math.Round(executionMs, 3),
                        state_read_ms = Math.Round(stateReadMs, 3),
                        state_hash_ms = Math.Round(stateHashMs, 3),
                        commit_ms = Math.Round(commitMs, 3),
                        total_ms = Math.Round(totalMs, 3)
                    },
                    throughput = new
                    {
                        mgas_per_sec = Math.Round(mgasPerSec, 2)
                    },
                    state_reads = new
                    {
                        accounts = accountReads,
                        storage_slots = storageReads,
                        code = codeReads,
                        code_bytes = codeBytesRead
                    },
                    state_writes = new
                    {
                        accounts = accountWrites,
                        storage_slots = storageWrites,
                        code = codeWrites,
                        code_bytes = codeBytesWritten,
                        eip7702_delegations_set = eip7702DelegationsSet,
                        eip7702_delegations_cleared = eip7702DelegationsCleared
                    },
                    cache = new
                    {
                        account = new { hits = accountCacheHits, misses = accountCacheMisses, hit_rate = accountHitRate },
                        storage = new { hits = storageCacheHits, misses = storageCacheMisses, hit_rate = storageHitRate },
                        code = new { hits = codeCacheHits, misses = codeCacheMisses, hit_rate = codeHitRate }
                    },
                    evm = new
                    {
                        sload = sloadOps,
                        sstore = sstoreOps,
                        calls = callOps,
                        creates = createOps
                    }
                };

                string json = JsonSerializer.Serialize(slowBlockLog, new JsonSerializerOptions
                {
                    WriteIndented = false
                });

                if (_slowBlockLogger.IsWarn) _slowBlockLogger.Warn(json);
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug) _logger.Debug($"Error logging slow block: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates the cache hit rate as a percentage.
        /// </summary>
        private static double CalculateHitRate(long hits, long misses)
        {
            long total = hits + misses;
            return total > 0 ? Math.Round((double)hits / total * 100.0, 2) : 0.0;
        }

        public void Start()
        {
            if (!_runStopwatch.IsRunning)
            {
                _lastReportMs = Environment.TickCount64;
                _runStopwatch.Start();
            }
        }

        // Help identify mev blocks when doesn't follow regular pattern
        private static readonly HashSet<AddressAsKey> _alternateMevPayees = new()
        {
            new Address("0xa83114A443dA1CecEFC50368531cACE9F37fCCcb"), // Extra data as: beaverbuild.org
            new Address("0x9FC3da866e7DF3a1c57adE1a97c9f00a70f010c8"), // Extra data as: Titan (titanbuilder.xyz)
            new Address("0x0b92619DdE55C0cbf828d32993a7fB004E00c84B"), // Extra data as: Builder+ www.btcs.com/builder
        };

        private class BlockDataPolicy() : IPooledObjectPolicy<BlockData>
        {
            public BlockData Create() => new BlockData();
            public bool Return(BlockData data)
            {
                // Release the object references so we don't hold them from being GC'd
                data.Block = null;
                data.BaseBlock = null;

                return true;
            }
        }

        private class BlockData
        {
            public Block Block;
            public BlockHeader? BaseBlock;
            public long CurrentOpCodes;
            public long CurrentSLoadOps;
            public long CurrentSStoreOps;
            public long CurrentCallOps;
            public long CurrentEmptyCalls;
            public long CurrentCachedContractsUsed;
            public long CurrentContractsAnalyzed;
            public long CurrentCreatesOps;
            public long CurrentSelfDestructOps;
            public long ProcessingMicroseconds;
            public long RunningMicroseconds;
            public long RunMicroseconds;
            public long StartOpCodes;
            public long StartSelfDestructOps;
            public long StartCreateOps;
            public long StartContractsAnalyzed;
            public long StartCachedContractsUsed;
            public long StartEmptyCalls;
            public long StartCallOps;
            public long StartSStoreOps;
            public long StartSLoadOps;
            // State access metrics for cross-client slow block logging
            public long CurrentAccountReads;
            public long CurrentStorageReads;
            public long CurrentCodeReads;
            public long CurrentCodeBytesRead;
            public long CurrentAccountWrites;
            public long CurrentStorageWrites;
            public long StartAccountReads;
            public long StartStorageReads;
            public long StartCodeReads;
            public long StartCodeBytesRead;
            public long StartAccountWrites;
            public long StartStorageWrites;
            public long CurrentCodeWrites;
            public long CurrentCodeBytesWritten;
            public long StartCodeWrites;
            public long StartCodeBytesWritten;
            // Timing metrics for cross-client slow block logging (in ticks)
            public long CurrentStateReadTime;
            public long CurrentStateHashTime;
            public long CurrentCommitTime;
            public long StartStateReadTime;
            public long StartStateHashTime;
            public long StartCommitTime;
            // Cache statistics for cross-client slow block logging
            public long CurrentAccountCacheHits;
            public long CurrentAccountCacheMisses;
            public long CurrentStorageCacheHits;
            public long CurrentStorageCacheMisses;
            public long CurrentCodeCacheHits;
            public long CurrentCodeCacheMisses;
            public long StartAccountCacheHits;
            public long StartAccountCacheMisses;
            public long StartStorageCacheHits;
            public long StartStorageCacheMisses;
            public long StartCodeCacheHits;
            public long StartCodeCacheMisses;
            // EIP-7702 delegation tracking for cross-client slow block logging
            public long CurrentEip7702DelegationsSet;
            public long CurrentEip7702DelegationsCleared;
            public long StartEip7702DelegationsSet;
            public long StartEip7702DelegationsCleared;
        }
    }
}
