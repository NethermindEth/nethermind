// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
    internal class ProcessingStats : IThreadPoolWorkItem
    {
        private readonly IStateReader _stateReader;
        private readonly ILogger _logger;
        private readonly Stopwatch _runStopwatch = new();

        private Block? _lastBlock;
        private Hash256? _lastBranchRoot;
        private double _lastTotalMGas;
        private long _lastBlockNumber;
        private long _lastElapsedRunningMicroseconds;
        private long _lastTotalTx;
        private long _runningMicroseconds;
        private long _runMicroseconds;
        private long _chunkProcessingMicroseconds;
        private long _currentReportMs;
        private long _lastReportMs;

        private long _lastCallOps;
        private long _currentCallOps;
        private long _lastEmptyCalls;
        private long _currentEmptyCalls;
        private long _lastSLoadOps;
        private long _currentSLoadOps;
        private long _lastSStoreOps;
        private long _currentSStoreOps;
        private long _lastSelfDestructOps;
        private long _currentSelfDestructOps;
        private long _lastCreateOps;
        private long _currentCreatesOps;
        private long _lastContractsAnalyzed;
        private long _currentContractsAnalyzed;
        private long _lastCachedContractsUsed;
        private long _currentCachedContractsUsed;

        public ProcessingStats(IStateReader stateReader, ILogger logger)
        {
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
            _lastSLoadOps = Evm.Metrics.ThreadLocalSLoadOpcode;
            _lastSStoreOps = Evm.Metrics.ThreadLocalSStoreOpcode;
            _lastCallOps = Evm.Metrics.ThreadLocalCalls;
            _lastEmptyCalls = Evm.Metrics.ThreadLocalEmptyCalls;
            _lastCachedContractsUsed = Db.Metrics.ThreadLocalCodeDbCache;
            _lastContractsAnalyzed = Evm.Metrics.ThreadLocalContractsAnalysed;
            _lastCreateOps = Evm.Metrics.ThreadLocalCreates;
            _lastSelfDestructOps = Evm.Metrics.ThreadLocalSelfDestructs;
        }

        public void UpdateStats(Block? block, Hash256 branchRoot, long blockProcessingTimeInMicros)
        {
            if (block is null) return;

            if (_lastBlockNumber == 0)
            {
                _lastBlockNumber = block.Number;
            }

            _chunkProcessingMicroseconds += blockProcessingTimeInMicros;

            Metrics.Mgas += block.GasUsed / 1_000_000.0;
            Metrics.Transactions += block.Transactions.Length;
            Metrics.Blocks = block.Number;
            Metrics.TotalDifficulty = block.TotalDifficulty ?? UInt256.Zero;
            Metrics.LastDifficulty = block.Difficulty;
            Metrics.GasUsed = block.GasUsed;
            Metrics.GasLimit = block.GasLimit;

            Metrics.BlockchainHeight = block.Header.Number;

            _runningMicroseconds = _runStopwatch.ElapsedMicroseconds();
            _runMicroseconds = (_runningMicroseconds - _lastElapsedRunningMicroseconds);

            long reportMs = _currentReportMs = Environment.TickCount64;
            if (reportMs - _lastReportMs > 1000 || _logger.IsDebug)
            {
                _lastReportMs = _currentReportMs;
                _lastBlock = block;
                _lastBranchRoot = branchRoot;
                _currentSLoadOps = Evm.Metrics.ThreadLocalSLoadOpcode;
                _currentSStoreOps = Evm.Metrics.ThreadLocalSStoreOpcode;
                _currentCallOps = Evm.Metrics.ThreadLocalCalls;
                _currentEmptyCalls = Evm.Metrics.ThreadLocalEmptyCalls;
                _currentCachedContractsUsed = Db.Metrics.ThreadLocalCodeDbCache;
                _currentContractsAnalyzed = Evm.Metrics.ThreadLocalContractsAnalysed;
                _currentCreatesOps = Evm.Metrics.ThreadLocalCreates;
                _currentSelfDestructOps = Evm.Metrics.ThreadLocalSelfDestructs;
                GenerateReport();
            }
        }

        private void GenerateReport() => ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);

        void IThreadPoolWorkItem.Execute()
        {
            try
            {
                Execute();
            }
            catch (Exception ex)
            {
                // Don't allow exception to escape to ThreadPool
                if (_logger.IsError) _logger.Error("Error when generating processing statistics", ex);
            }
        }

        void Execute()
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

            Block? block = Interlocked.Exchange(ref _lastBlock, null);
            if (block is null) return;

            Transaction[] txs = block.Transactions;
            Address beneficiary = block.Header.GasBeneficiary ?? Address.Zero;
            Transaction lastTx = txs.Length > 0 ? txs[^1] : null;
            bool isMev = false;
            if (lastTx?.To is not null && (lastTx.SenderAddress == beneficiary || _alternateMevPayees.Contains(lastTx.SenderAddress)))
            {
                // Mev reward with in last tx
                beneficiary = lastTx.To;
                isMev = true;
            }

            if (_lastBranchRoot is null || !_stateReader.HasStateForRoot(_lastBranchRoot) || block.StateRoot is null || !_stateReader.HasStateForRoot(block.StateRoot))
                return;

            UInt256 rewards = default;
            try
            {
                UInt256 beforeBalance = _stateReader.GetBalance(_lastBranchRoot, beneficiary);
                UInt256 afterBalance = _stateReader.GetBalance(block.StateRoot, beneficiary);
                rewards = beforeBalance < afterBalance ? afterBalance - beforeBalance : default;
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Error when calculating block rewards", ex);
            }

            long chunkBlocks = Metrics.Blocks - _lastBlockNumber;

            double chunkMicroseconds = _chunkProcessingMicroseconds;
            double chunkMGas = Metrics.Mgas - _lastTotalMGas;
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

            if (_logger.IsInfo)
            {
                long chunkTx = Metrics.Transactions - _lastTotalTx;
                long chunkCalls = _currentCallOps - _lastCallOps;
                long chunkEmptyCalls = _currentEmptyCalls - _lastEmptyCalls;
                long chunkCreates = _currentCreatesOps - _lastCreateOps;
                long chunkSload = _currentSLoadOps - _lastSLoadOps;
                long chunkSstore = _currentSStoreOps - _lastSStoreOps;
                long contractsAnalysed = _currentContractsAnalyzed - _lastContractsAnalyzed;
                long cachedContractsUsed = _currentCachedContractsUsed - _lastCachedContractsUsed;
                double txps = chunkMicroseconds == 0 ? -1 : chunkTx / chunkMicroseconds * 1_000_000.0;
                double bps = chunkMicroseconds == 0 ? -1 : chunkBlocks / chunkMicroseconds * 1_000_000.0;
                double chunkMs = (chunkMicroseconds == 0 ? -1 : chunkMicroseconds / 1000.0);
                double runMs = (_runMicroseconds == 0 ? -1 : _runMicroseconds / 1000.0);
                string blockGas = Evm.Metrics.BlockMinGasPrice != float.MaxValue ? $"⛽ Gas gwei: {Evm.Metrics.BlockMinGasPrice:N2} .. {whiteText}{Math.Max(Evm.Metrics.BlockMinGasPrice, Evm.Metrics.BlockEstMedianGasPrice):N2}{resetColor} ({Evm.Metrics.BlockAveGasPrice:N2}) .. {Evm.Metrics.BlockMaxGasPrice:N2}" : "";
                string mgasColor = whiteText;

                if (chunkBlocks > 1)
                {
                    _logger.Info($"Processed    {block.Number - chunkBlocks + 1,10}...{block.Number,9}   | {chunkMs,10:N1} ms  |  slot    {runMs,7:N0} ms |{blockGas}");
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
                    _logger.Info($"Processed          {block.Number,10}         | {chunkColor}{chunkMs,10:N1}{resetColor} ms  |  slot    {runMs,7:N0} ms |{blockGas}");
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

                _logger.Info($" Block{(chunkBlocks > 1 ? $"s  x{chunkBlocks,-9:N0}  " : $"{(isMev ? " mev" : "    ")} {rewards.ToDecimal(null) / weiToEth,5:N4}{BlocksConfig.GasTokenTicker,4}")}{(chunkBlocks == 1 ? mgasColor : "")} {chunkMGas,7:F2}{resetColor} MGas    | {chunkTx,8:N0}   txs |  calls {callsColor}{chunkCalls,6:N0}{resetColor} {darkGreyText}({chunkEmptyCalls,3:N0}){resetColor} | sload {chunkSload,7:N0} | sstore {sstoreColor}{chunkSstore,6:N0}{resetColor} | create {createsColor}{chunkCreates,3:N0}{resetColor}{(_currentSelfDestructOps - _lastSelfDestructOps > 0 ? $"{darkGreyText}({-(_currentSelfDestructOps - _lastSelfDestructOps),3:N0}){resetColor}" : "")}");
                if (recoveryQueue > 0 || processingQueue > 0)
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |       {bps,7:F2} Blk/s | recover {recoveryQueue,5:N0} | process {processingQueue,5:N0}");
                }
                else
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |       {bps,7:F2} Blk/s | exec code {resetColor} from cache {cachedContractsUsed,7:N0} |{resetColor} new {contractsAnalysed,6:N0}");
                }
            }

            _lastBlockNumber = Metrics.Blocks;
            _lastTotalMGas = Metrics.Mgas;
            _lastElapsedRunningMicroseconds = _runningMicroseconds;
            _lastTotalTx = Metrics.Transactions;
            _chunkProcessingMicroseconds = 0;
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
    }
}
