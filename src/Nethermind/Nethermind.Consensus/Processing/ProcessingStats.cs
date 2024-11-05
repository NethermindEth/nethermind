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
        private long _lastBlockNumber;
        private long _lastElapsedRunningMicroseconds;
        private double _lastTotalMGas;
        private long _lastTotalTx;
        private long _lastTotalCalls;
        private long _lastTotalEmptyCalls;
        private long _lastTotalSLoad;
        private long _lastTotalSStore;
        private long _lastSelfDestructs;
        private long _chunkProcessingMicroseconds;
        private long _lastTotalCreates;
        private long _lastReportMs;
        private long _lastContractsAnalyzed;
        private long _lastCachedContractsUsed;
        private long _runningMicroseconds;
        private long _runMicroseconds;
        private long _reportMs;
        private Block? _lastBlock;
        private Hash256 _lastBranchRoot;
        private long _sloadOpcodeProcessing;
        private long _sstoreOpcodeProcessing;
        private long _callsProcessing;
        private long _emptyCallsProcessing;
        private long _codeDbCacheProcessing;
        private long _contractAnalysedProcessing;
        private long _createsProcessing;

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

            long reportMs = _reportMs = Environment.TickCount64;
            if (reportMs - _lastReportMs > 1000 || _logger.IsDebug)
            {
                _lastReportMs = _reportMs;
                _lastBlock = block;
                _lastBranchRoot = branchRoot;
                _sloadOpcodeProcessing = Evm.Metrics.ThreadLocalSLoadOpcode;
                _sstoreOpcodeProcessing = Evm.Metrics.ThreadLocalSStoreOpcode;
                _callsProcessing = Evm.Metrics.ThreadLocalCalls;
                _emptyCallsProcessing = Evm.Metrics.ThreadLocalEmptyCalls;
                _codeDbCacheProcessing = Db.Metrics.ThreadLocalCodeDbCache;
                _contractAnalysedProcessing = Evm.Metrics.ThreadLocalContractsAnalysed;
                _createsProcessing = Evm.Metrics.ThreadLocalCreates;
                GenerateReport();
            }
        }

        private void GenerateReport() => ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);

        void IThreadPoolWorkItem.Execute()
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
            if (lastTx is not null && (lastTx.SenderAddress == beneficiary || _alternateMevPayees.Contains(lastTx.SenderAddress)))
            {
                // Mev reward with in last tx
                beneficiary = lastTx.To;
                isMev = true;
            }
            UInt256 beforeBalance = _stateReader.GetBalance(_lastBranchRoot, beneficiary);
            UInt256 afterBalance = _stateReader.GetBalance(block.StateRoot, beneficiary);
            UInt256 rewards = beforeBalance < afterBalance ? afterBalance - beforeBalance : default;

            long currentSelfDestructs = Evm.Metrics.SelfDestructs;

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
                long chunkCalls = _callsProcessing - _lastTotalCalls;
                long chunkEmptyCalls = _emptyCallsProcessing - _lastTotalEmptyCalls;
                long chunkCreates = _createsProcessing - _lastTotalCreates;
                long chunkSload = _sloadOpcodeProcessing - _lastTotalSLoad;
                long chunkSstore = _sstoreOpcodeProcessing - _lastTotalSStore;
                long contractsAnalysed = _contractAnalysedProcessing - _lastContractsAnalyzed;
                long cachedContractsUsed = _codeDbCacheProcessing - _lastCachedContractsUsed;
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

                _logger.Info($" Block{(chunkBlocks > 1 ? $"s  x{chunkBlocks,-9:N0}  " : $"{(isMev ? " mev" : "    ")} {rewards.ToDecimal(null) / weiToEth,5:N4}{BlocksConfig.GasTokenTicker,4}")}{(chunkBlocks == 1 ? mgasColor : "")} {chunkMGas,7:F2}{resetColor} MGas    | {chunkTx,8:N0}   txs |  calls {callsColor}{chunkCalls,6:N0}{resetColor} {darkGreyText}({chunkEmptyCalls,3:N0}){resetColor} | sload {chunkSload,7:N0} | sstore {sstoreColor}{chunkSstore,6:N0}{resetColor} | create {createsColor}{chunkCreates,3:N0}{resetColor}{(currentSelfDestructs - _lastSelfDestructs > 0 ? $"{darkGreyText}({-(currentSelfDestructs - _lastSelfDestructs),3:N0}){resetColor}" : "")}");
                if (recoveryQueue > 0 || processingQueue > 0)
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |       {bps,7:F2} Blk/s | recover {recoveryQueue,5:N0} | process {processingQueue,5:N0}");
                }
                else
                {
                    _logger.Info($" Block throughput {mgasPerSecondColor}{mgasPerSecond,11:F2}{resetColor} MGas/s{(mgasPerSecond > 1000 ? "🔥" : "  ")}| {txps,10:N1} tps |       {bps,7:F2} Blk/s | exec code {resetColor} from cache {cachedContractsUsed,7:N0} |{resetColor} new {contractsAnalysed,6:N0}");
                }
            }

            _lastCachedContractsUsed = _codeDbCacheProcessing;
            _lastContractsAnalyzed = _contractAnalysedProcessing;
            _lastBlockNumber = Metrics.Blocks;
            _lastTotalMGas = Metrics.Mgas;
            _lastElapsedRunningMicroseconds = _runningMicroseconds;
            _lastTotalTx = Metrics.Transactions;
            _lastTotalCalls = _callsProcessing;
            _lastTotalEmptyCalls = _emptyCallsProcessing;
            _lastTotalCreates = _createsProcessing;
            _lastTotalSLoad = _sloadOpcodeProcessing;
            _lastTotalSStore = _sstoreOpcodeProcessing;
            _lastSelfDestructs = currentSelfDestructs;
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
        private static HashSet<AddressAsKey> _alternateMevPayees = new()
        {
            new Address("0xa83114A443dA1CecEFC50368531cACE9F37fCCcb"), // Extra data as: beaverbuild.org
            new Address("0x9FC3da866e7DF3a1c57adE1a97c9f00a70f010c8"), // Extra data as: Titan (titanbuilder.xyz)
            new Address("0x0b92619DdE55C0cbf828d32993a7fB004E00c84B"), // Extra data as: Builder+ www.btcs.com/builder
        };
    }
}
