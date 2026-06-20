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
using Nethermind.Core.Collections;
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
        /// Threshold in milliseconds for slow block logging. Defaults to -1 (disabled), matching
        /// <see cref="BlocksConfig.SlowBlockThresholdMs"/>. Set to 0 to log every block, or to a
        /// positive value to log only blocks slower than that many milliseconds.
        /// </summary>
        private readonly long _slowBlockThresholdMs;

        /// <summary>
        /// Per-tx threshold in milliseconds. Transactions slower than this are included
        /// individually in the slow block JSON. Set to -1 to disable.
        /// </summary>
        private readonly long _slowBlockPerTxThresholdMs;

        /// <summary>
        /// Whether <see cref="BlocksConfig.ParallelExecution"/> was enabled for this node. Emitted in
        /// the slow-block JSON so cross-client consumers can normalise EVM/state counter values
        /// (these aggregate across parallel workers and inflate relative to single-threaded clients).
        /// </summary>
        private readonly bool _parallelExecution;

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
        private long _chunkFirstBlockNumber = -1;
        private long _opCodes;
        private long _callOps;
        private long _emptyCalls;
        private long _sLoadOps;
        private long _sStoreOps;
        private long _selfDestructOps;
        private long _createOps;
        private long _contractsAnalyzed;
        private long _cachedContractsUsed;

        public ProcessingStats(IStateReader stateReader, ILogManager logManager, IBlocksConfig blocksConfig)
            : this(stateReader, logManager.GetClassLogger<ProcessingStats>(), logManager.GetLogger("SlowBlocks"),
                   slowBlockThresholdMs: blocksConfig.SlowBlockThresholdMs,
                   slowBlockPerTxThresholdMs: blocksConfig.SlowBlockPerTxThresholdMs,
                   parallelExecution: blocksConfig.ParallelExecution)
        {
        }

        public ProcessingStats(IStateReader stateReader, ILogManager logManager)
            : this(stateReader, logManager.GetClassLogger<ProcessingStats>(), logManager.GetLogger("SlowBlocks"))
        {
        }

        /// <summary>
        /// Lower-level ctor used by tests and by the public <see cref="ILogManager"/> overloads.
        /// </summary>
        /// <remarks>
        /// <paramref name="slowBlockThresholdMs"/> defaults to <c>-1</c> (disabled) to match
        /// <see cref="BlocksConfig.SlowBlockThresholdMs"/>; callers that need slow-block JSON
        /// emission must pass an explicit non-negative value (typical: <c>1000</c> for the
        /// production "log blocks slower than 1s" threshold, <c>0</c> to log every block).
        /// <paramref name="parallelExecution"/> is surfaced in the slow-block JSON so cross-client
        /// analysers can normalise opcode/state counter values that aggregate across parallel workers.
        /// </remarks>
        public ProcessingStats(IStateReader stateReader, ILogger logger, ILogger? slowBlockLogger = null, long slowBlockThresholdMs = -1, long slowBlockPerTxThresholdMs = -1, bool parallelExecution = false)
        {
            _executeFromThreadPool = ExecuteFromThreadPool;

            _stateReader = stateReader;
            _logger = logger;
            _slowBlockLogger = slowBlockLogger ?? logger;
            _slowBlockThresholdMs = slowBlockThresholdMs;
            _slowBlockPerTxThresholdMs = slowBlockPerTxThresholdMs;
            _parallelExecution = parallelExecution;

            // the line below just to avoid compilation errors
            if (_logger.IsTrace) _logger.Trace($"Processing Stats in debug mode?: {_logger.IsDebug}");
#if DEBUG
            _logger.SetDebugMode();
#endif
        }

        public void CaptureStartStats()
        {
            // EVM counters — always captured (used by normal console reporting).
            // Read MainThread* so deltas are consistent with the MainThread* values read in UpdateStats
            // and don't include increments from background threads (prewarmer, etc.).
            _startSLoadOps = Evm.Metrics.MainThreadSLoadOpcode;
            _startSStoreOps = Evm.Metrics.MainThreadSStoreOpcode;
            _startCallOps = Evm.Metrics.MainThreadCalls;
            _startEmptyCalls = Evm.Metrics.MainThreadEmptyCalls;
            _startCachedContractsUsed = Evm.Metrics.MainThreadCodeDbCache;
            _startContractsAnalyzed = Evm.Metrics.MainThreadContractsAnalysed;
            _startCreateOps = Evm.Metrics.MainThreadCreates;
            _startSelfDestructOps = Evm.Metrics.MainThreadSelfDestructs;
            _startOpCodes = Evm.Metrics.MainThreadOpCodes;

            // Slow block diagnostics — skip when disabled (-1)
            if (_slowBlockThresholdMs < 0) return;

            // Enable per-tx timing on the current block-processing thread.
            // Must be set here (not in Start()) because the async processing loop
            // can resume on a different ThreadPool thread after each await.
            if (_slowBlockPerTxThresholdMs >= 0)
            {
                PerTxTimingCollector.SetEnabled(true);
            }

            // Read MainThread* to exclude background prewarmer activity from per-block deltas
            // (cross-client metrics must reflect only the block-processing flow's reads/writes).
            _startCodeBytesRead = Evm.Metrics.MainThreadCodeBytesRead;
            _startAccountWrites = Evm.Metrics.MainThreadAccountWrites;
            _startAccountDeleted = Evm.Metrics.MainThreadAccountDeleted;
            _startStorageWrites = Evm.Metrics.MainThreadStorageWrites;
            _startStorageDeleted = Evm.Metrics.MainThreadStorageDeleted;
            _startCodeWrites = Evm.Metrics.MainThreadCodeWrites;
            _startCodeBytesWritten = Evm.Metrics.MainThreadCodeBytesWritten;
            _startStateHashTime = Evm.Metrics.MainThreadStateHashTime;
            _startCommitTime = Evm.Metrics.MainThreadCommitTime;
            _startAccountCacheHits = DbMetrics.MainThreadStateTreeCache;
            _startAccountCacheMisses = DbMetrics.MainThreadStateTreeReads;
            _startStorageCacheHits = DbMetrics.MainThreadStorageTreeCache;
            _startStorageCacheMisses = DbMetrics.MainThreadStorageTreeReads;
            _startCodeCacheHits = Evm.Metrics.MainThreadCodeDbCache;
            _startCodeCacheMisses = Evm.Metrics.MainThreadCodeReads;
            _startEip7702DelegationsSet = Evm.Metrics.MainThreadEip7702DelegationsSet;
            _startEip7702DelegationsCleared = Evm.Metrics.MainThreadEip7702DelegationsCleared;
            _startStorageMerkleTime = Evm.Metrics.MainThreadStorageMerkleTime;
            _startStateRootTime = Evm.Metrics.MainThreadStateRootTime;
            _startBloomsTime = Evm.Metrics.MainThreadBloomsTime;
            _startReceiptsRootTime = Evm.Metrics.MainThreadReceiptsRootTime;
        }

        public void UpdateStats(IReadOnlyList<Block> blocks, BlockHeader? baseBlock, long blockProcessingTimeInMicros)
        {
            if (blocks.Count == 0) return;

            Block lastBlock = blocks[^1];
            long gasUsed = 0;
            long transactionCount = 0;
            long blobCount = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                Block block = blocks[i];
                gasUsed += block.GasUsed;
                Transaction[] transactions = block.Transactions;
                transactionCount += transactions.Length;
                for (int j = 0; j < transactions.Length; j++)
                {
                    blobCount += transactions[j].GetBlobCount();
                }
            }

            BlockData blockData = _dataPool.Get();
            blockData.Block = lastBlock;
            blockData.BaseBlock = baseBlock;
            blockData.BlockCount = blocks.Count;
            blockData.FirstBlockNumber = blocks[0].Number;
            blockData.GasUsed = gasUsed;
            blockData.TransactionCount = transactionCount;
            blockData.BlobCount = blobCount;
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
            blockData.CurrentOpCodes = Evm.Metrics.MainThreadOpCodes;
            blockData.CurrentSLoadOps = Evm.Metrics.MainThreadSLoadOpcode;
            blockData.CurrentSStoreOps = Evm.Metrics.MainThreadSStoreOpcode;
            blockData.CurrentCallOps = Evm.Metrics.MainThreadCalls;
            blockData.CurrentEmptyCalls = Evm.Metrics.MainThreadEmptyCalls;
            blockData.CurrentCachedContractsUsed = Evm.Metrics.MainThreadCodeDbCache;
            blockData.CurrentContractsAnalyzed = Evm.Metrics.MainThreadContractsAnalysed;
            blockData.CurrentCreatesOps = Evm.Metrics.MainThreadCreates;
            blockData.CurrentSelfDestructOps = Evm.Metrics.MainThreadSelfDestructs;

            // Pre-compute deltas for slow block logging (done here on the block-processing thread)
            // Skip entirely when slow block logging is disabled (-1)
            if (_slowBlockThresholdMs >= 0)
            {
                blockData.DeltaAccountWrites = Evm.Metrics.MainThreadAccountWrites - _startAccountWrites;
                blockData.DeltaAccountDeleted = Evm.Metrics.MainThreadAccountDeleted - _startAccountDeleted;
                blockData.DeltaStorageWrites = Evm.Metrics.MainThreadStorageWrites - _startStorageWrites;
                blockData.DeltaStorageDeleted = Evm.Metrics.MainThreadStorageDeleted - _startStorageDeleted;
                blockData.DeltaCodeWrites = Evm.Metrics.MainThreadCodeWrites - _startCodeWrites;
                blockData.DeltaCodeBytesWritten = Evm.Metrics.MainThreadCodeBytesWritten - _startCodeBytesWritten;
                blockData.DeltaStateHashTime = Evm.Metrics.MainThreadStateHashTime - _startStateHashTime;
                blockData.DeltaCommitTime = Evm.Metrics.MainThreadCommitTime - _startCommitTime;
                blockData.DeltaAccountCacheHits = DbMetrics.MainThreadStateTreeCache - _startAccountCacheHits;
                blockData.DeltaAccountCacheMisses = DbMetrics.MainThreadStateTreeReads - _startAccountCacheMisses;
                blockData.DeltaStorageCacheHits = DbMetrics.MainThreadStorageTreeCache - _startStorageCacheHits;
                blockData.DeltaStorageCacheMisses = DbMetrics.MainThreadStorageTreeReads - _startStorageCacheMisses;
                blockData.DeltaCodeCacheHits = Evm.Metrics.MainThreadCodeDbCache - _startCodeCacheHits;
                blockData.DeltaCodeCacheMisses = Evm.Metrics.MainThreadCodeReads - _startCodeCacheMisses;
                blockData.DeltaAccountReads = blockData.DeltaAccountCacheMisses;
                blockData.DeltaStorageReads = blockData.DeltaStorageCacheHits + blockData.DeltaStorageCacheMisses;
                blockData.DeltaCodeReads = blockData.DeltaCodeCacheMisses;
                blockData.DeltaCodeBytesRead = Evm.Metrics.MainThreadCodeBytesRead - _startCodeBytesRead;
                blockData.DeltaEip7702DelegationsSet = Evm.Metrics.MainThreadEip7702DelegationsSet - _startEip7702DelegationsSet;
                blockData.DeltaEip7702DelegationsCleared = Evm.Metrics.MainThreadEip7702DelegationsCleared - _startEip7702DelegationsCleared;
                blockData.DeltaStorageMerkleTime = Evm.Metrics.MainThreadStorageMerkleTime - _startStorageMerkleTime;
                blockData.DeltaStateRootTime = Evm.Metrics.MainThreadStateRootTime - _startStateRootTime;
                blockData.DeltaBloomsTime = Evm.Metrics.MainThreadBloomsTime - _startBloomsTime;
                blockData.DeltaReceiptsRootTime = Evm.Metrics.MainThreadReceiptsRootTime - _startReceiptsRootTime;

                // Snapshot per-tx timing (rents a pooled list for ThreadPool use, null when disabled).
                // The list is disposed (returning its array to the pool) in BlockDataPolicy.Return.
                blockData.PerTxTicks = PerTxTimingCollector.Snapshot();
            }

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
            double chunkMGas = (_chunkMGas += data.GasUsed / 1_000_000.0);

            // We want the rate here
            double mgas = data.GasUsed / 1_000_000.0;
            double timeSec = data.ProcessingMicroseconds / 1_000_000.0;
            double mgasPerSec = timeSec > 0 ? mgas / timeSec : 0;
            Metrics.BlockMGasPerSec.Observe(mgasPerSec);
            Metrics.BlockProcessingTimeMicros.Observe(data.ProcessingMicroseconds);

            // Log slow blocks in JSON format for cross-client performance analysis
            // Only log when slow block threshold is enabled (>= 0)
            if (_slowBlockThresholdMs >= 0)
            {
                long processingMs = data.ProcessingMicroseconds / 1000;
                if (processingMs >= _slowBlockThresholdMs)
                {
                    LogSlowBlock(block, data, mgasPerSec);
                }
            }

            Metrics.Mgas += data.GasUsed / 1_000_000.0;
            Transaction[] txs = block.Transactions;
            double chunkMicroseconds = (_chunkProcessingMicroseconds += data.ProcessingMicroseconds);
            double chunkTx = (_chunkTx += data.TransactionCount);

            long chunkFirstBlockNumber = _chunkFirstBlockNumber;
            if (chunkFirstBlockNumber == -1)
            {
                chunkFirstBlockNumber = data.FirstBlockNumber;
                _chunkFirstBlockNumber = chunkFirstBlockNumber;
            }

            long chunkBlocks = (_chunkBlocks += data.BlockCount);

            Metrics.Blocks = blockNumber;
            Metrics.BlockchainHeight = blockNumber;

            Metrics.Transactions += data.TransactionCount;
            Metrics.TotalDifficulty = block.TotalDifficulty ?? UInt256.Zero;
            Metrics.LastDifficulty = block.Difficulty;
            // These gauges describe the latest processed block; chunk totals above use data.GasUsed and data.TransactionCount.
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

            _chunkBlobs += data.BlobCount;
            long blobs = _chunkBlobs;
            if (blobs > 0)
            {
                _showBlobs = true;
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

            long reportMs = GetReportMs();
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
            _chunkFirstBlockNumber = -1;
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
            string blockGas = "";
            if (Evm.Metrics.BlockMinGasPrice != float.MaxValue)
            {
                float minGas = Evm.Metrics.BlockMinGasPrice;
                float medianGas = Math.Max(minGas, Evm.Metrics.BlockEstMedianGasPrice);
                float aveGas = Evm.Metrics.BlockAveGasPrice;
                float maxGas = Evm.Metrics.BlockMaxGasPrice;
                // Step the unit down (gwei -> mwei -> kwei -> wei) so small gas prices stay visible at :N3
                // instead of all rendering 0.000 on low-base-fee chains.
                (string unit, float scale) = minGas switch
                {
                    0f or >= 0.001f => ("gwei", 1f),
                    >= 0.000_001f => ("mwei", 1_000f),
                    >= 0.000_000_001f => ("kwei", 1_000_000f),
                    _ => ("wei", 1_000_000_000f),
                };
                blockGas = $"⛽ Gas {unit}: {minGas * scale:N3} .. {whiteText}{medianGas * scale:N3}{resetColor} ({aveGas * scale:N3}) .. {maxGas * scale:N3}";
            }
            string mgasColor = whiteText;

            NewProcessingStatistics?.Invoke(this, new BlockStatistics()
            {
                BlockCount = chunkBlocks,
                BlockFrom = chunkFirstBlockNumber,
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
                    _logger.Info($"Processed    {chunkFirstBlockNumber,10}...{block.Number,9}   | {chunkMs,10:N1} ms  | slot    {runMs,11:N0} ms |{blockGas}");
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
                    string chunkColor = chunkMs switch
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
                string sstoreColor = chunkBlocks > 1 ? "" : chunkSstore switch
                {
                    > 3500 => redText,
                    > 2500 => orangeText,
                    > 2000 => yellowText,
                    > 1500 => whiteText,
                    > 900 when chunkCalls > 900 => whiteText,
                    _ => ""
                };
                string callsColor = chunkBlocks > 1 ? "" : chunkCalls switch
                {
                    > 3500 => redText,
                    > 2500 => orangeText,
                    > 2000 => yellowText,
                    > 1500 => whiteText,
                    > 900 when chunkSstore > 900 => whiteText,
                    _ => ""
                };
                string createsColor = chunkBlocks > 1 ? "" : chunkCreates switch
                {
                    > 300 => redText,
                    > 200 => orangeText,
                    > 150 => yellowText,
                    > 75 => whiteText,
                    _ => ""
                };

                long recoveryQueue = Metrics.RecoveryQueueSize;
                long processingQueue = Metrics.ProcessingQueueSize;

                _logger.Info($" Block{(chunkBlocks > 1 ? $"s  x{chunkBlocks,-9:N0} " : $"{(isMev ? " mb" : "   ")} {rewards.ToDecimal(null) / weiToEth,6:N4}{BlocksConfig.GasTokenTicker,4}")}{(chunkBlocks == 1 ? mgasColor : "")} {chunkMGas,8:F2}{resetColor} MGas    | {chunkTx,8:N0}   txs | calls {callsColor}{chunkCalls,10:N0}{resetColor} {darkGreyText}({chunkEmptyCalls,3:N0}){resetColor} | sload {chunkSload,7:N0} | sstore {sstoreColor}{chunkSstore,6:N0}{resetColor} | create {createsColor}{chunkCreates,3:N0}{resetColor}{(chunkSelfDestructs > 0 ? $"{darkGreyText}({-chunkSelfDestructs,3:N0}){resetColor}" : "")}");
                string blobsOrBlocksPerSec = _showBlobs switch
                {
                    true => $" blobs {blobs,3:N0} ",
                    _ => $"       {bps,10:F2} Blk/s "
                };

                // Exec-mode indicator: chains for parallel BAL, link for sequential. BlockAccessList is
                // null pre-Amsterdam, so a parallel-configured node correctly shows sequential pre-fork.
                string execMode = _parallelExecution && block.BlockAccessList is not null ? " ⛓️" : " 🔗";

                if (recoveryQueue > 0 || processingQueue > 0)
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |{blobsOrBlocksPerSec}| recover {recoveryQueue,5:N0} | process {processingQueue,5:N0} | ops {chunkOpCodes,11:N0}{execMode}");
                }
                else
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |{blobsOrBlocksPerSec}| exec code{resetColor} cache {cachedContractsUsed,7:N0} |{resetColor} new {contractsAnalysed,6:N0} | ops {chunkOpCodes,11:N0}{execMode}");
                }
            }

            UInt256 CalculateBalanceChange(BlockHeader? startBlock, BlockHeader endBlock, Address beneficiary)
            {
                UInt256 beforeBalance = _stateReader.GetBalance(startBlock, beneficiary);
                UInt256 afterBalance = _stateReader.GetBalance(endBlock, beneficiary);
                return beforeBalance < afterBalance ? afterBalance - beforeBalance : default;
            }
        }

        protected virtual long GetReportMs() => Environment.TickCount64;

        /// <remarks>
        /// Under <c>ParallelExecution</c>, workers are explicitly marked with <c>IsBlockProcessingThread = true</c>,
        /// so <c>evm.*</c> and <c>state_reads/writes.*</c> counters sum across all workers (not
        /// per-thread). Wall-clock timings are unaffected.
        /// </remarks>
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
                if (executionMs < 0)
                {
                    // Indicates the per-phase counters (DeltaStateHashTime / DeltaCommitTime)
                    // are larger than the block's wall-clock — should not happen on a single
                    // block-processing thread. Surface it for diagnosis without failing the log.
                    if (_logger.IsDebug) _logger.Debug($"Slow block {block.Number}: executionMs clamped to total ({totalMs:F3}ms): stateHashMs={stateHashMs:F3} commitMs={commitMs:F3}");
                    executionMs = totalMs;
                }
                // evm_ms: pure EVM execution (excludes blooms + receipts root as well)
                double evmMs = totalMs - stateHashMs - commitMs - bloomsMs - receiptsRootMs;
                if (evmMs < 0)
                {
                    if (_logger.IsDebug) _logger.Debug($"Slow block {block.Number}: evmMs clamped to executionMs ({executionMs:F3}ms): stateHashMs={stateHashMs:F3} commitMs={commitMs:F3} bloomsMs={bloomsMs:F3} receiptsRootMs={receiptsRootMs:F3}");
                    evmMs = executionMs;
                }

                double accountHitRate = CalculateHitRate(data.DeltaAccountCacheHits, data.DeltaAccountCacheMisses);
                double storageHitRate = CalculateHitRate(data.DeltaStorageCacheHits, data.DeltaStorageCacheMisses);
                double codeHitRate = CalculateHitRate(data.DeltaCodeCacheHits, data.DeltaCodeCacheMisses);

                // Blob count was already summed in UpdateStats on the block-processing thread
                // (BlockData.BlobCount); reuse rather than re-walking transactions here.
                Transaction[] txs = block.Transactions;

                ArrayBufferWriter<byte> buffer = new(1024);
                using (Utf8JsonWriter writer = new(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("level", "warn");
                    writer.WriteString("msg", "Slow block");
                    // Top-level flag so cross-client analysers can normalise EVM/state counters,
                    // which sum across parallel workers when this is true.
                    writer.WriteBoolean("parallel_execution", _parallelExecution);

                    writer.WriteStartObject("block");
                    writer.WriteNumber("number", block.Number);
                    writer.WriteString("hash", block.Hash?.ToString() ?? "0x");
                    writer.WriteNumber("gas_used", block.GasUsed);
                    writer.WriteNumber("gas_limit", block.GasLimit);
                    writer.WriteNumber("tx_count", block.Transactions.Length);
                    writer.WriteNumber("blob_count", data.BlobCount);
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

                    // Per-transaction timing breakdown (when enabled).
                    ArrayPoolList<long>? perTxTicks = data.PerTxTicks;
                    if (perTxTicks is not null && perTxTicks.Count > 0 && txs.Length > 0)
                    {
                        long perTxThresholdTicks = _slowBlockPerTxThresholdMs * TimeSpan.TicksPerMillisecond;
                        ReadOnlySpan<long> ticks = perTxTicks.AsSpan();

                        writer.WriteStartArray("transactions");
                        for (int i = 0; i < ticks.Length && i < txs.Length; i++)
                        {
                            long txTicks = ticks[i];
                            if (txTicks < perTxThresholdTicks) continue;

                            double txMs = txTicks / (double)TimeSpan.TicksPerMillisecond;
                            Transaction tx = txs[i];
                            writer.WriteStartObject();
                            writer.WriteNumber("index", i);
                            writer.WriteString("hash", tx.Hash?.ToString() ?? "0x");
                            writer.WriteNumber("gas_used", tx.SpentGas);
                            writer.WriteNumber("execution_ms", Math.Round(txMs, 3));
                            writer.WriteString("type", tx.Type.ToString());
                            if (tx.To is not null)
                            {
                                writer.WriteString("to", tx.To.ToString());
                            }
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                _slowBlockLogger.Warn(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
            }
            catch (Exception ex)
            {
                // Defensive: never fail block processing because of a slow-block log failure.
                // Log at Error with the full exception (stack trace included) so a serialization
                // regression is diagnosable rather than silently dropping JSON entries.
                if (_logger.IsError) _logger.Error($"Error logging slow block {block.Number}", ex);
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
                _lastReportMs = GetReportMs();
                _runStopwatch.Start();
            }
        }

        // Help identify mev blocks when doesn't follow regular pattern
        private static readonly HashSet<AddressAsKey> _alternateMevPayees =
        [
            new Address("0xa83114A443dA1CecEFC50368531cACE9F37fCCcb"), // Extra data as: beaverbuild.org
            new Address("0x9FC3da866e7DF3a1c57adE1a97c9f00a70f010c8"), // Extra data as: Titan (titanbuilder.xyz)
            new Address("0x0b92619DdE55C0cbf828d32993a7fB004E00c84B"), // Extra data as: Builder+ www.btcs.com/builder
        ];

        private class BlockDataPolicy() : IPooledObjectPolicy<BlockData>
        {
            public BlockData Create() => new();
            public bool Return(BlockData data)
            {
                // Dispose the pooled per-tx ticks list (returns its backing array to the pool)
                data.PerTxTicks?.Dispose();

                // Release the object references so we don't hold them from being GC'd
                data.Block = null;
                data.BaseBlock = null;
                data.PerTxTicks = null;
                data.BlockCount = 0;
                data.FirstBlockNumber = 0;
                data.GasUsed = 0;
                data.TransactionCount = 0;
                data.BlobCount = 0;

                // Reset the slow-block Delta* fields too. They're only written when the threshold
                // is enabled (UpdateStats line ~270), so if a pooled instance was returned without
                // them being set (threshold flipped off mid-process) the next reuse would otherwise
                // see stale values.
                data.DeltaAccountReads = 0;
                data.DeltaStorageReads = 0;
                data.DeltaCodeReads = 0;
                data.DeltaCodeBytesRead = 0;
                data.DeltaAccountWrites = 0;
                data.DeltaAccountDeleted = 0;
                data.DeltaStorageWrites = 0;
                data.DeltaStorageDeleted = 0;
                data.DeltaCodeWrites = 0;
                data.DeltaCodeBytesWritten = 0;
                data.DeltaStateHashTime = 0;
                data.DeltaCommitTime = 0;
                data.DeltaAccountCacheHits = 0;
                data.DeltaAccountCacheMisses = 0;
                data.DeltaStorageCacheHits = 0;
                data.DeltaStorageCacheMisses = 0;
                data.DeltaCodeCacheHits = 0;
                data.DeltaCodeCacheMisses = 0;
                data.DeltaEip7702DelegationsSet = 0;
                data.DeltaEip7702DelegationsCleared = 0;
                data.DeltaStorageMerkleTime = 0;
                data.DeltaStateRootTime = 0;
                data.DeltaBloomsTime = 0;
                data.DeltaReceiptsRootTime = 0;

                return true;
            }
        }

        protected class BlockData
        {
            public Block Block;
            public BlockHeader? BaseBlock;
            public long BlockCount;
            public long FirstBlockNumber;
            public long GasUsed;
            public long TransactionCount;
            public long BlobCount;
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
            public ArrayPoolList<long>? PerTxTicks;
        }
    }
}
