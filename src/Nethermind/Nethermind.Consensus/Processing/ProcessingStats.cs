// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing
{
    //TODO Consult on disabling of such metrics from configuration
    internal class ProcessingStats
    {
        private readonly ILogger _logger;
        private readonly Stopwatch _processingStopwatch = new();
        private readonly Stopwatch _runStopwatch = new();
        private long _lastBlockNumber;
        private long _lastElapsedRunningMicroseconds;
        private decimal _lastTotalMGas;
        private long _lastTotalTx;
        private long _lastTotalCalls;
        private long _lastTotalEmptyCalls;
        private long _lastTotalSLoad;
        private long _lastTotalSStore;
        private long _lastStateDbReads;
        private long _lastStateDbWrites;
        private long _lastGen0;
        private long _lastGen1;
        private long _lastGen2;
        private long _lastTreeNodeRlp;
        private long _lastEvmExceptions;
        private long _lastSelfDestructs;
        private long _maxMemory;
        private long _totalBlocks;
        private bool _isDebugMode = false;
        private decimal _processingMicroseconds;
        private long _lastTotalCreates;

        public ProcessingStats(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // the line below just to avoid compilation errors
            if (_logger.IsTrace) _logger.Trace($"Processing Stats in debug mode?: {_isDebugMode}");
#if DEBUG
            _isDebugMode = true;
#endif
        }

        public void UpdateStats(Block? block, IBlockTree blockTreeCtx, int recoveryQueueSize, int blockQueueSize, long blockProcessingTimeInMicros)
        {
            if (block is null)
            {
                return;
            }

            _processingStopwatch.Stop();

            if (_lastBlockNumber == 0)
            {
                _lastBlockNumber = block.Number;
            }

            _processingMicroseconds += blockProcessingTimeInMicros;

            Metrics.Mgas += block.GasUsed / 1_000_000m;
            Metrics.Transactions += block.Transactions.Length;
            Metrics.Blocks = block.Number;
            Metrics.TotalDifficulty = block.TotalDifficulty ?? UInt256.Zero;
            Metrics.LastDifficulty = block.Difficulty;
            Metrics.GasUsed = block.GasUsed;
            Metrics.GasLimit = block.GasLimit;
            Metrics.RecoveryQueueSize = recoveryQueueSize;
            Metrics.ProcessingQueueSize = blockQueueSize;

            Metrics.BlockchainHeight = block.Header.Number;
            Metrics.BestKnownBlockNumber = blockTreeCtx.BestKnownNumber;

            long processingMicroseconds = _processingStopwatch.ElapsedMicroseconds();
            long runningMicroseconds = _runStopwatch.ElapsedMicroseconds();
            decimal runMicroseconds = (runningMicroseconds - _lastElapsedRunningMicroseconds);

            if (runMicroseconds > 1 * 1000 * 1000)
            {
                long currentStateDbReads = Db.Metrics.StateDbReads;
                long currentStateDbWrites = Db.Metrics.StateDbWrites;
                long currentTreeNodeRlp = Trie.Metrics.TreeNodeRlpEncodings + Trie.Metrics.TreeNodeRlpDecodings;
                long evmExceptions = Evm.Metrics.EvmExceptions;
                long currentSelfDestructs = Evm.Metrics.SelfDestructs;

                long chunkBlocks = Metrics.Blocks - _lastBlockNumber;
                _totalBlocks += chunkBlocks;

                if (_logger.IsInfo)
                {
                    decimal chunkMicroseconds = _processingMicroseconds;
                    decimal totalMicroseconds = processingMicroseconds;
                    long chunkTx = Metrics.Transactions - _lastTotalTx;
                    long chunkCalls = Evm.Metrics.Calls - _lastTotalCalls;
                    long chunkEmptyCalls = Evm.Metrics.EmptyCalls - _lastTotalEmptyCalls;
                    long chunkCreates = Evm.Metrics.Creates - _lastTotalCreates;
                    long chunkSload = Evm.Metrics.SloadOpcode - _lastTotalSLoad;
                    long chunkSstore = Evm.Metrics.SstoreOpcode - _lastTotalSStore;
                    decimal chunkMGas = Metrics.Mgas - _lastTotalMGas;
                    decimal mgasPerSecond = chunkMicroseconds == 0 ? -1 : chunkMGas / chunkMicroseconds * 1000 * 1000;
                    decimal totalMgasPerSecond = totalMicroseconds == 0 ? -1 : Metrics.Mgas / totalMicroseconds * 1000 * 1000;
                    decimal totalTxPerSecond = totalMicroseconds == 0 ? -1 : Metrics.Transactions / totalMicroseconds * 1000 * 1000;
                    decimal totalBlocksPerSecond = totalMicroseconds == 0 ? -1 : _totalBlocks / totalMicroseconds * 1000 * 1000;
                    decimal txps = chunkMicroseconds == 0 ? -1 : chunkTx / chunkMicroseconds * 1000 * 1000;
                    decimal bps = chunkMicroseconds == 0 ? -1 : chunkBlocks / chunkMicroseconds * 1000 * 1000;
                    decimal chunkMs = (chunkMicroseconds == 0 ? -1 : chunkMicroseconds / 1000);
                    decimal runMs = (runMicroseconds == 0 ? -1 : runMicroseconds / 1000);
                    if (chunkBlocks > 1)
                    {
                        _logger.Info($"Processed   {block.Number - chunkBlocks + 1,9}...{block.Number,9} | {chunkMs,9:N2} ms  | slot {runMs,7:N0} ms     | recv  {recoveryQueueSize,7:N0} | proc   {blockQueueSize,6:N0}");
                    }
                    else
                    {
                        _logger.Info($"Processed     {block.Number,9}           | {chunkMs,9:N2} ms  | slot {runMs,7:N0} ms     | recv  {recoveryQueueSize,7:N0} | proc   {blockQueueSize,6:N0}");
                    }
                    _logger.Info($"- Block{(chunkBlocks > 1 ? "s" : " ")}           {chunkMGas,7:F2} MGas   | {chunkTx,6:N0}    txs | calls {chunkCalls,6:N0} ({chunkEmptyCalls,3:N0})  | sload {chunkSload,7:N0} | sstore {chunkSstore,6:N0} | creates {chunkCreates,3:N0} ({-(currentSelfDestructs - _lastSelfDestructs),3:N0})");
                    string blockGas = $" Gas gwei: {Evm.Metrics.BlockMinGasPrice:N2} .. {Math.Max(Evm.Metrics.BlockMinGasPrice, Evm.Metrics.BlockEstMedianGasPrice):N2} ({Evm.Metrics.BlockAveGasPrice:N2}) .. {Evm.Metrics.BlockMaxGasPrice:N2}";
                    _logger.Info($"- Block Throughput {mgasPerSecond,7:F2} MGas/s | {txps,9:F2} t/s |         {bps,7:F2} b/s |{(Evm.Metrics.BlockMinGasPrice != decimal.MaxValue ? blockGas : "")}");
                    _logger.Info($"- Total Throughput {totalMgasPerSecond,7:F2} MGas/s | {totalTxPerSecond,9:F2} t/s |         {totalBlocksPerSecond,7:F2} b/s | Gas gwei: {Evm.Metrics.MinGasPrice:N2} .. {Math.Max(Evm.Metrics.MinGasPrice, Evm.Metrics.EstMedianGasPrice):N2} ({Evm.Metrics.AveGasPrice:N2}) .. {Evm.Metrics.MaxGasPrice:N2}");
                }
                if (_logger.IsTrace)
                {
                    long currentGen0 = GC.CollectionCount(0);
                    long currentGen1 = GC.CollectionCount(1);
                    long currentGen2 = GC.CollectionCount(2);
                    long currentMemory = GC.GetTotalMemory(false);
                    _maxMemory = Math.Max(_maxMemory, currentMemory);
                    _logger.Trace($"Gen0 {currentGen0 - _lastGen0,6}, Gen1 {currentGen1 - _lastGen1,6}, Gen2 {currentGen2 - _lastGen2,6}, maxmem {_maxMemory / 1000000,5}, mem {currentMemory / 1000000,5}, reads {currentStateDbReads - _lastStateDbReads,9}, writes {currentStateDbWrites - _lastStateDbWrites,9}, rlp {currentTreeNodeRlp - _lastTreeNodeRlp,9}, exceptions {evmExceptions - _lastEvmExceptions}, selfdstrcs {currentSelfDestructs - _lastSelfDestructs}");
                    _lastGen0 = currentGen0;
                    _lastGen1 = currentGen1;
                    _lastGen2 = currentGen2;
                }

                _lastBlockNumber = Metrics.Blocks;
                _lastTotalMGas = Metrics.Mgas;
                _lastElapsedRunningMicroseconds = runningMicroseconds;
                _lastTotalTx = Metrics.Transactions;
                _lastTotalCalls = Evm.Metrics.Calls;
                _lastTotalEmptyCalls = Evm.Metrics.EmptyCalls;
                _lastTotalCreates = Evm.Metrics.Creates;
                _lastTotalSLoad = Evm.Metrics.SloadOpcode;
                _lastTotalSStore = Evm.Metrics.SstoreOpcode;
                _lastStateDbReads = currentStateDbReads;
                _lastStateDbWrites = currentStateDbWrites;
                _lastTreeNodeRlp = currentTreeNodeRlp;
                _lastEvmExceptions = evmExceptions;
                _lastSelfDestructs = currentSelfDestructs;
                _processingMicroseconds = 0;
            }
        }

        public void Start()
        {
            _processingStopwatch.Start();
            if (!_runStopwatch.IsRunning)
            {
                _runStopwatch.Start();
            }
        }
    }
}
