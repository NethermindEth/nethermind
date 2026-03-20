// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
        public long BlockCount { get; set; }
        public long BlockFrom { get; set; }
        public long BlockTo { get; set; }
        public double ProcessingMs { get; set; }
        public double SlotMs { get; set; }
        [JsonPropertyName("mgasPerSecond")]
        public double MGasPerSecond { get; set; }
        public float MinGas { get; set; }
        public float MedianGas { get; set; }
        public float AveGas { get; set; }
        public float MaxGas { get; set; }
        public long GasLimit { get; set; }
    }
    //TODO Consult on disabling of such metrics from configuration
    public class ProcessingStats : IProcessingStats
    {
        private static readonly DefaultObjectPool<BlockData> _dataPool = new(new BlockDataPolicy(), 16);
        private readonly Action<BlockData> _executeFromThreadPool;
        public event EventHandler<BlockStatistics>? NewProcessingStatistics;
        protected readonly IStateReader _stateReader;
        protected readonly ILogger _logger;
        private readonly ILogger _slowBlockLogger;
        private readonly Stopwatch _runStopwatch = new();

        /// <summary>
        /// Threshold in milliseconds for slow block logging (default: 1000ms).
        /// Set to 0 to log all blocks.
        /// </summary>
        private readonly long _slowBlockThresholdMs;

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
        private long _startAccountDeleted;
        private long _startStorageWrites;
        private long _startStorageDeleted;
        private long _startCodeWrites;
        private long _startCodeBytesWritten;
        private long _startStateHashTime;
        private long _startCommitTime;
        private long _startAccountCacheHits;
        private long _startAccountCacheMisses;
        private long _startStorageCacheHits;
        private long _startStorageCacheMisses;
        private long _startCodeCacheHits;
        private long _startCodeCacheMisses;
        private long _startEip7702DelegationsSet;
        private long _startEip7702DelegationsCleared;
        private long _startStorageMerkleTime;
        private long _startStateRootTime;
        private long _startBloomsTime;
        private long _startReceiptsRootTime;
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

        public ProcessingStats(IStateReader stateReader, ILogManager logManager)
            : this(stateReader, logManager.GetClassLogger(), logManager.GetClassLogger("SlowBlocks"), slowBlockThresholdMs: 1000)
        {
        }

        public ProcessingStats(IStateReader stateReader, ILogger logger, ILogger? slowBlockLogger = null, long slowBlockThresholdMs = 1000)
        {
            _executeFromThreadPool = ExecuteFromThreadPool;

            _stateReader = stateReader;
            _logger = logger;
            _slowBlockLogger = slowBlockLogger ?? logger;
            _slowBlockThresholdMs = slowBlockThresholdMs;

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
            _startAccountDeleted = Evm.Metrics.ThreadLocalAccountDeleted;
            _startStorageWrites = Evm.Metrics.ThreadLocalStorageWrites;
            _startStorageDeleted = Evm.Metrics.ThreadLocalStorageDeleted;
            _startCodeWrites = Evm.Metrics.ThreadLocalCodeWrites;
            _startCodeBytesWritten = Evm.Metrics.ThreadLocalCodeBytesWritten;
            _startStateHashTime = Evm.Metrics.ThreadLocalStateHashTime;
            _startCommitTime = Evm.Metrics.ThreadLocalCommitTime;
            _startAccountCacheHits = DbMetrics.ThreadLocalStateTreeCacheHits;
            _startAccountCacheMisses = DbMetrics.ThreadLocalStateTreeReads;
            _startStorageCacheHits = DbMetrics.ThreadLocalStorageTreeCacheHits;
            _startStorageCacheMisses = DbMetrics.ThreadLocalStorageTreeReads;
            _startCodeCacheHits = Evm.Metrics.ThreadLocalCodeDbCache;
            _startCodeCacheMisses = Evm.Metrics.ThreadLocalCodeReads;
            _startEip7702DelegationsSet = Evm.Metrics.ThreadLocalEip7702DelegationsSet;
            _startEip7702DelegationsCleared = Evm.Metrics.ThreadLocalEip7702DelegationsCleared;
            _startStorageMerkleTime = Evm.Metrics.ThreadLocalStorageMerkleTime;
            _startStateRootTime = Evm.Metrics.ThreadLocalStateRootTime;
            _startBloomsTime = Evm.Metrics.ThreadLocalBloomsTime;
            _startReceiptsRootTime = Evm.Metrics.ThreadLocalReceiptsRootTime;
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

            // Pre-compute deltas for slow block logging (done here on the block-processing thread)
            blockData.DeltaAccountReads = Evm.Metrics.ThreadLocalAccountReads - _startAccountReads;
            blockData.DeltaStorageReads = Evm.Metrics.ThreadLocalStorageReads - _startStorageReads;
            blockData.DeltaCodeReads = Evm.Metrics.ThreadLocalCodeReads - _startCodeReads;
            blockData.DeltaCodeBytesRead = Evm.Metrics.ThreadLocalCodeBytesRead - _startCodeBytesRead;
            blockData.DeltaAccountWrites = Evm.Metrics.ThreadLocalAccountWrites - _startAccountWrites;
            blockData.DeltaAccountDeleted = Evm.Metrics.ThreadLocalAccountDeleted - _startAccountDeleted;
            blockData.DeltaStorageWrites = Evm.Metrics.ThreadLocalStorageWrites - _startStorageWrites;
            blockData.DeltaStorageDeleted = Evm.Metrics.ThreadLocalStorageDeleted - _startStorageDeleted;
            blockData.DeltaCodeWrites = Evm.Metrics.ThreadLocalCodeWrites - _startCodeWrites;
            blockData.DeltaCodeBytesWritten = Evm.Metrics.ThreadLocalCodeBytesWritten - _startCodeBytesWritten;
            blockData.DeltaStateHashTime = Evm.Metrics.ThreadLocalStateHashTime - _startStateHashTime;
            blockData.DeltaCommitTime = Evm.Metrics.ThreadLocalCommitTime - _startCommitTime;
            blockData.DeltaAccountCacheHits = DbMetrics.ThreadLocalStateTreeCacheHits - _startAccountCacheHits;
            blockData.DeltaAccountCacheMisses = DbMetrics.ThreadLocalStateTreeReads - _startAccountCacheMisses;
            blockData.DeltaStorageCacheHits = DbMetrics.ThreadLocalStorageTreeCacheHits - _startStorageCacheHits;
            blockData.DeltaStorageCacheMisses = DbMetrics.ThreadLocalStorageTreeReads - _startStorageCacheMisses;
            blockData.DeltaCodeCacheHits = Evm.Metrics.ThreadLocalCodeDbCache - _startCodeCacheHits;
            blockData.DeltaCodeCacheMisses = Evm.Metrics.ThreadLocalCodeReads - _startCodeCacheMisses;
            blockData.DeltaEip7702DelegationsSet = Evm.Metrics.ThreadLocalEip7702DelegationsSet - _startEip7702DelegationsSet;
            blockData.DeltaEip7702DelegationsCleared = Evm.Metrics.ThreadLocalEip7702DelegationsCleared - _startEip7702DelegationsCleared;
            blockData.DeltaStorageMerkleTime = Evm.Metrics.ThreadLocalStorageMerkleTime - _startStorageMerkleTime;
            blockData.DeltaStateRootTime = Evm.Metrics.ThreadLocalStateRootTime - _startStateRootTime;
            blockData.DeltaBloomsTime = Evm.Metrics.ThreadLocalBloomsTime - _startBloomsTime;
            blockData.DeltaReceiptsRootTime = Evm.Metrics.ThreadLocalReceiptsRootTime - _startReceiptsRootTime;

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

        protected virtual void GenerateReport(BlockData data)
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
            if (processingMs >= _slowBlockThresholdMs)
            {
                LogSlowBlock(block, data, mgasPerSec);
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

        private void LogSlowBlock(Block block, BlockData data, double mgasPerSec)
        {
            if (!_slowBlockLogger.IsWarn) return;
            try
            {
                double stateHashMs = data.DeltaStateHashTime / (double)TimeSpan.TicksPerMillisecond;
                double storageMerkleMs = data.DeltaStorageMerkleTime / (double)TimeSpan.TicksPerMillisecond;
                double stateRootMs = data.DeltaStateRootTime / (double)TimeSpan.TicksPerMillisecond;
                double commitMs = data.DeltaCommitTime / (double)TimeSpan.TicksPerMillisecond;
                double bloomsMs = data.DeltaBloomsTime / (double)TimeSpan.TicksPerMillisecond;
                double receiptsRootMs = data.DeltaReceiptsRootTime / (double)TimeSpan.TicksPerMillisecond;
                double totalMs = data.ProcessingMicroseconds / 1000.0;
                // execution_ms: original definition (total - state_hash - commit) for backwards compat
                double executionMs = totalMs - stateHashMs - commitMs;
                if (executionMs < 0) executionMs = totalMs;
                // evm_ms: pure EVM execution (excludes blooms + receipts root as well)
                double evmMs = totalMs - stateHashMs - commitMs - bloomsMs - receiptsRootMs;
                if (evmMs < 0) evmMs = executionMs;

                double accountHitRate = CalculateHitRate(data.DeltaAccountCacheHits, data.DeltaAccountCacheMisses);
                double storageHitRate = CalculateHitRate(data.DeltaStorageCacheHits, data.DeltaStorageCacheMisses);
                double codeHitRate = CalculateHitRate(data.DeltaCodeCacheHits, data.DeltaCodeCacheMisses);

                // Compute blob count on the ThreadPool thread (not block-processing thread)
                int blobCount = 0;
                Transaction[] txs = block.Transactions;
                for (int i = 0; i < txs.Length; i++)
                {
                    blobCount += txs[i].GetBlobCount();
                }

                ArrayBufferWriter<byte> buffer = new(1024);
                using (Utf8JsonWriter writer = new(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("level", "warn");
                    writer.WriteString("msg", "Slow block");

                    writer.WriteStartObject("block");
                    writer.WriteNumber("number", block.Number);
                    writer.WriteString("hash", block.Hash?.ToString() ?? "0x");
                    writer.WriteNumber("gas_used", block.GasUsed);
                    writer.WriteNumber("gas_limit", block.GasLimit);
                    writer.WriteNumber("tx_count", block.Transactions.Length);
                    writer.WriteNumber("blob_count", blobCount);
                    writer.WriteEndObject();

                    writer.WriteStartObject("timing");
                    writer.WriteNumber("execution_ms", Math.Round(executionMs, 3));
                    writer.WriteNumber("evm_ms", Math.Round(evmMs, 3));
                    writer.WriteNumber("blooms_ms", Math.Round(bloomsMs, 3));
                    writer.WriteNumber("receipts_root_ms", Math.Round(receiptsRootMs, 3));
                    writer.WriteNumber("commit_ms", Math.Round(commitMs, 3));
                    writer.WriteNumber("storage_merkle_ms", Math.Round(storageMerkleMs, 3));
                    writer.WriteNumber("state_root_ms", Math.Round(stateRootMs, 3));
                    writer.WriteNumber("state_hash_ms", Math.Round(stateHashMs, 3));
                    writer.WriteNumber("total_ms", Math.Round(totalMs, 3));
                    writer.WriteEndObject();

                    writer.WriteStartObject("throughput");
                    writer.WriteNumber("mgas_per_sec", Math.Round(mgasPerSec, 2));
                    writer.WriteEndObject();

                    writer.WriteStartObject("state_reads");
                    writer.WriteNumber("accounts", data.DeltaAccountReads);
                    writer.WriteNumber("storage_slots", data.DeltaStorageReads);
                    writer.WriteNumber("code", data.DeltaCodeReads);
                    writer.WriteNumber("code_bytes", data.DeltaCodeBytesRead);
                    writer.WriteEndObject();

                    writer.WriteStartObject("state_writes");
                    writer.WriteNumber("accounts", data.DeltaAccountWrites);
                    writer.WriteNumber("accounts_deleted", data.DeltaAccountDeleted);
                    writer.WriteNumber("storage_slots", data.DeltaStorageWrites);
                    writer.WriteNumber("storage_slots_deleted", data.DeltaStorageDeleted);
                    writer.WriteNumber("code", data.DeltaCodeWrites);
                    writer.WriteNumber("code_bytes", data.DeltaCodeBytesWritten);
                    writer.WriteNumber("eip7702_delegations_set", data.DeltaEip7702DelegationsSet);
                    writer.WriteNumber("eip7702_delegations_cleared", data.DeltaEip7702DelegationsCleared);
                    writer.WriteEndObject();

                    writer.WriteStartObject("cache");
                    WriteCacheEntry(writer, "account", data.DeltaAccountCacheHits, data.DeltaAccountCacheMisses, accountHitRate);
                    WriteCacheEntry(writer, "storage", data.DeltaStorageCacheHits, data.DeltaStorageCacheMisses, storageHitRate);
                    WriteCacheEntry(writer, "code", data.DeltaCodeCacheHits, data.DeltaCodeCacheMisses, codeHitRate);
                    writer.WriteEndObject();

                    writer.WriteStartObject("evm");
                    writer.WriteNumber("opcodes", data.CurrentOpCodes - data.StartOpCodes);
                    writer.WriteNumber("sload", data.CurrentSLoadOps - data.StartSLoadOps);
                    writer.WriteNumber("sstore", data.CurrentSStoreOps - data.StartSStoreOps);
                    writer.WriteNumber("calls", data.CurrentCallOps - data.StartCallOps);
                    writer.WriteNumber("empty_calls", data.CurrentEmptyCalls - data.StartEmptyCalls);
                    writer.WriteNumber("creates", data.CurrentCreatesOps - data.StartCreateOps);
                    writer.WriteNumber("self_destructs", data.CurrentSelfDestructOps - data.StartSelfDestructOps);
                    writer.WriteNumber("contracts_analyzed", data.CurrentContractsAnalyzed - data.StartContractsAnalyzed);
                    writer.WriteNumber("cached_contracts_used", data.CurrentCachedContractsUsed - data.StartCachedContractsUsed);
                    writer.WriteEndObject();

                    writer.WriteEndObject();
                }

                _slowBlockLogger.Warn(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug) _logger.Debug($"Error logging slow block: {ex.Message}");
            }

            static void WriteCacheEntry(Utf8JsonWriter writer, string name, long hits, long misses, double hitRate)
            {
                writer.WriteStartObject(name);
                writer.WriteNumber("hits", hits);
                writer.WriteNumber("misses", misses);
                writer.WriteNumber("hit_rate", hitRate);
                writer.WriteEndObject();
            }
        }

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

        protected class BlockData
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
            // Pre-computed deltas for slow block logging
            public long DeltaAccountReads;
            public long DeltaStorageReads;
            public long DeltaCodeReads;
            public long DeltaCodeBytesRead;
            public long DeltaAccountWrites;
            public long DeltaAccountDeleted;
            public long DeltaStorageWrites;
            public long DeltaStorageDeleted;
            public long DeltaCodeWrites;
            public long DeltaCodeBytesWritten;
            public long DeltaStateHashTime;
            public long DeltaCommitTime;
            public long DeltaAccountCacheHits;
            public long DeltaAccountCacheMisses;
            public long DeltaStorageCacheHits;
            public long DeltaStorageCacheMisses;
            public long DeltaCodeCacheHits;
            public long DeltaCodeCacheMisses;
            public long DeltaEip7702DelegationsSet;
            public long DeltaEip7702DelegationsCleared;
            public long DeltaStorageMerkleTime;
            public long DeltaStateRootTime;
            public long DeltaBloomsTime;
            public long DeltaReceiptsRootTime;
        }
    }
}
