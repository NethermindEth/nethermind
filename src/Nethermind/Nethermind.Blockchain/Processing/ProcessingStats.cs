//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Processing
{
    internal class ProcessingStats
    {
        private readonly ILogger _logger;
        private readonly Stopwatch _processingStopwatch = new();
        private long _lastBlockNumber;
        private long _lastElapsedTicks;
        private decimal _lastTotalMGas;
        private long _lastTotalTx;
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

        public ProcessingStats(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // the line below just to avoid compilation errors
            if(_logger.IsTrace) _logger.Trace($"Processing Stats in debug mode?: {_isDebugMode}");
#if DEBUG	
            _isDebugMode = true;	
#endif
        }

        public void UpdateStats(Block? block, int recoveryQueueSize, int blockQueueSize)
        {
            if (block is null)
            {
                return;
            }
            
            if (_lastBlockNumber == 0)
            {
                _lastBlockNumber = block.Number;
            }

            Metrics.Mgas += block.GasUsed / 1_000_000m;
            Metrics.Transactions += block.Transactions.Length;
            Metrics.Blocks = block.Number;
            Metrics.RecoveryQueueSize = recoveryQueueSize;
            Metrics.ProcessingQueueSize = blockQueueSize;

            long currentTicks = _processingStopwatch.ElapsedTicks;
            decimal totalMicroseconds = _processingStopwatch.ElapsedTicks * (1_000_000m / Stopwatch.Frequency);
            decimal chunkMicroseconds = (_processingStopwatch.ElapsedTicks - _lastElapsedTicks) * (1_000_000m / Stopwatch.Frequency);

            if (chunkMicroseconds > 1 * 1000 * 1000)
            {
                long currentGen0 = GC.CollectionCount(0);
                long currentGen1 = GC.CollectionCount(1);
                long currentGen2 = GC.CollectionCount(2);
                long currentMemory = GC.GetTotalMemory(false);
                _maxMemory = Math.Max(_maxMemory, currentMemory);
                long currentStateDbReads = Db.Metrics.StateDbReads;
                long currentStateDbWrites = Db.Metrics.StateDbWrites;
                long currentTreeNodeRlp = Trie.Metrics.TreeNodeRlpEncodings + Trie.Metrics.TreeNodeRlpDecodings;
                long evmExceptions = Evm.Metrics.EvmExceptions;
                long currentSelfDestructs = Evm.Metrics.SelfDestructs;

                long chunkTx = Metrics.Transactions - _lastTotalTx;
                long chunkBlocks = Metrics.Blocks - _lastBlockNumber;
                decimal chunkMGas = Metrics.Mgas - _lastTotalMGas;

                _totalBlocks += chunkBlocks;

                decimal mgasPerSecond = chunkMicroseconds == 0 ? -1 : chunkMGas / chunkMicroseconds * 1000 * 1000;
                decimal totalMgasPerSecond = totalMicroseconds == 0 ? -1 : Metrics.Mgas / totalMicroseconds * 1000 * 1000;
                decimal totalTxPerSecond = totalMicroseconds == 0 ? -1 : Metrics.Transactions / totalMicroseconds * 1000 * 1000;
                decimal totalBlocksPerSecond = totalMicroseconds == 0 ? -1 : _totalBlocks / totalMicroseconds * 1000 * 1000;
                decimal txps = chunkMicroseconds == 0 ? -1 : chunkTx / chunkMicroseconds * 1000m * 1000m;
                decimal bps = chunkMicroseconds == 0 ? -1 : chunkBlocks / chunkMicroseconds * 1000m * 1000m;

                if (_logger.IsInfo) _logger.Info($"Processed  {block.Number,9} |  {(chunkMicroseconds == 0 ? -1 : chunkMicroseconds / 1000),7:N0}ms, mgasps {mgasPerSecond,7:F2} total {totalMgasPerSecond,7:F2}, tps {txps,7:F2} total {totalTxPerSecond,7:F2}, bps {bps,7:F2} total {totalBlocksPerSecond,7:F2}, recv queue {recoveryQueueSize}, proc queue {blockQueueSize}");
                if (_logger.IsDebug) _logger.Trace($"Gen0 {currentGen0 - _lastGen0,6}, Gen1 {currentGen1 - _lastGen1,6}, Gen2 {currentGen2 - _lastGen2,6}, maxmem {_maxMemory / 1000000,5}, mem {currentMemory / 1000000,5}, reads {currentStateDbReads - _lastStateDbReads,9}, writes {currentStateDbWrites - _lastStateDbWrites,9}, rlp {currentTreeNodeRlp - _lastTreeNodeRlp,9}, exceptions {evmExceptions - _lastEvmExceptions}, selfdstrcs {currentSelfDestructs - _lastSelfDestructs}");

                _lastBlockNumber = Metrics.Blocks;
                _lastTotalMGas = Metrics.Mgas;
                _lastElapsedTicks = currentTicks;
                _lastTotalTx = Metrics.Transactions;
                _lastGen0 = currentGen0;
                _lastGen1 = currentGen1;
                _lastGen2 = currentGen2;
                _lastStateDbReads = currentStateDbReads;
                _lastStateDbWrites = currentStateDbWrites;
                _lastTreeNodeRlp = currentTreeNodeRlp;
                _lastEvmExceptions = evmExceptions;
                _lastSelfDestructs = currentSelfDestructs;
            }
        }

        public void Start()
        {
            _processingStopwatch.Start();
        }
    }
}
