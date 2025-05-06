// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
{
    //TODO Consult on disabling of such metrics from configuration
    internal class ProcessingStats
    {
        private static readonly DefaultObjectPool<BlockData> _dataPool = new(new BlockDataPolicy(), 16);
        private readonly Action<BlockData> _executeFromThreadPool;
        private readonly IStateReader _stateReader;
        private readonly ILogger _logger;
        private readonly Stopwatch _runStopwatch = new();

        private bool _showBlobs;
        private long _lastBlockNumber;
        private long _lastElapsedRunningMicroseconds;
        private long _lastReportMs;
        private long _startCallOps;
        private long _startEmptyCalls;
        private long _startSLoadOps;
        private long _startSStoreOps;
        private long _startSelfDestructOps;
        private long _startCreateOps;
        private long _startContractsAnalyzed;
        private long _startCachedContractsUsed;
        private double _chunkMGas;
        private long _chunkProcessingMicroseconds;
        private long _chunkTx;
        private long _chunkBlobs;
        private long _chunkBlocks;
        private long _callOps;
        private long _emptyCalls;
        private long _sLoadOps;
        private long _sStoreOps;
        private long _selfDestructOps;
        private long _createOps;
        private long _contractsAnalyzed;
        private long _cachedContractsUsed;

        public ProcessingStats(IStateReader stateReader, ILogger logger)
        {
            _executeFromThreadPool = ExecuteFromThreadPool;

            _stateReader = stateReader;
            _logger = logger;

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
            _startCachedContractsUsed = Db.Metrics.ThreadLocalCodeDbCache;
            _startContractsAnalyzed = Evm.Metrics.ThreadLocalContractsAnalysed;
            _startCreateOps = Evm.Metrics.ThreadLocalCreates;
            _startSelfDestructOps = Evm.Metrics.ThreadLocalSelfDestructs;
        }

        public void UpdateStats(Block? block, Hash256 branchRoot, long blockProcessingTimeInMicros)
        {
            if (block is null) return;

            if (_lastBlockNumber == 0)
            {
                _lastBlockNumber = block.Number;
            }

            BlockData blockData = _dataPool.Get();
            blockData.Block = block;
            blockData.BranchRoot = branchRoot;
            blockData.RunningMicroseconds = _runStopwatch.ElapsedMicroseconds();
            blockData.RunMicroseconds = (_runStopwatch.ElapsedMicroseconds() - _lastElapsedRunningMicroseconds);
            blockData.StartSLoadOps = _startSLoadOps;
            blockData.StartSStoreOps = _startSStoreOps;
            blockData.StartCallOps = _startCallOps;
            blockData.StartEmptyCalls = _startEmptyCalls;
            blockData.StartCachedContractsUsed = _startCachedContractsUsed;
            blockData.StartContractsAnalyzed = _startContractsAnalyzed;
            blockData.StartCreateOps = _startCreateOps;
            blockData.StartSelfDestructOps = _startSelfDestructOps;
            blockData.ProcessingMicroseconds = blockProcessingTimeInMicros;
            blockData.CurrentSLoadOps = Evm.Metrics.ThreadLocalSLoadOpcode;
            blockData.CurrentSStoreOps = Evm.Metrics.ThreadLocalSStoreOpcode;
            blockData.CurrentCallOps = Evm.Metrics.ThreadLocalCalls;
            blockData.CurrentEmptyCalls = Evm.Metrics.ThreadLocalEmptyCalls;
            blockData.CurrentCachedContractsUsed = Db.Metrics.ThreadLocalCodeDbCache;
            blockData.CurrentContractsAnalyzed = Evm.Metrics.ThreadLocalContractsAnalysed;
            blockData.CurrentCreatesOps = Evm.Metrics.ThreadLocalCreates;
            blockData.CurrentSelfDestructOps = Evm.Metrics.ThreadLocalSelfDestructs;

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
            if (_lastBlockNumber >= blockNumber) return;

            _lastBlockNumber = blockNumber;
            double chunkMGas = (_chunkMGas += block.GasUsed / 1_000_000.0);

            // We want the rate here
            double mgas = block.GasUsed / 1_000_000.0;
            double timeSec = data.ProcessingMicroseconds / 1_000_000.0;
            Metrics.BlockMGasPerSec.Observe(mgas / timeSec);
            Metrics.BlockProcessingTimeMicros.Observe(data.ProcessingMicroseconds);

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

            if (data.BranchRoot is null || !_stateReader.HasStateForRoot(data.BranchRoot) || block.StateRoot is null || !_stateReader.HasStateForRoot(block.StateRoot))
                return;

            UInt256 rewards = default;
            try
            {
                if (!isMev)
                {
                    rewards = CalculateBalanceChange(data.BranchRoot, block.StateRoot, beneficiary);
                }
                else
                {
                    // Sometimes the beneficiary has done their own balance changing tx
                    // So prefer the mev reward tx value
                    rewards = lastTx.Value;
                    if (rewards.IsZero)
                    {
                        rewards = CalculateBalanceChange(data.BranchRoot, block.StateRoot, lastTx.To);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Error when calculating block rewards", ex);
            }

            foreach (var tx in txs)
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
            string blockGas = Evm.Metrics.BlockMinGasPrice != float.MaxValue ? $"⛽ Gas gwei: {Evm.Metrics.BlockMinGasPrice:N2} .. {whiteText}{Math.Max(Evm.Metrics.BlockMinGasPrice, Evm.Metrics.BlockEstMedianGasPrice):N2}{resetColor} ({Evm.Metrics.BlockAveGasPrice:N2}) .. {Evm.Metrics.BlockMaxGasPrice:N2}" : "";
            string mgasColor = whiteText;

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
                    true => $" blobs {blobs,10:N0}       ",
                    _ => $"       {bps,10:F2} Blk/s "
                };

                if (recoveryQueue > 0 || processingQueue > 0)
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |{blobsOrBlocksPerSec}| recover {recoveryQueue,5:N0} | process {processingQueue,5:N0}");
                }
                else
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |{blobsOrBlocksPerSec}| exec code {resetColor} from cache {cachedContractsUsed,7:N0} |{resetColor} new {contractsAnalysed,6:N0}");
                }
            }

            UInt256 CalculateBalanceChange(Hash256 beforeRoot, Hash256 afterRoot, Address beneficiary)
            {
                UInt256 beforeBalance = _stateReader.GetBalance(beforeRoot, beneficiary);
                UInt256 afterBalance = _stateReader.GetBalance(afterRoot, beneficiary);
                return beforeBalance < afterBalance ? afterBalance - beforeBalance : default;
            }
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
                data.BranchRoot = null;

                return true;
            }
        }

        private class BlockData
        {
            public Block Block;
            public Hash256 BranchRoot;
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
            public long StartSelfDestructOps;
            public long StartCreateOps;
            public long StartContractsAnalyzed;
            public long StartCachedContractsUsed;
            public long StartEmptyCalls;
            public long StartCallOps;
            public long StartSStoreOps;
            public long StartSLoadOps;
        }
    }
}
