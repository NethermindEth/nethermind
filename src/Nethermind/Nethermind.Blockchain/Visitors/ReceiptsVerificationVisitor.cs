//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Visitors
{
    public class ReceiptsVerificationVisitor : IBlockTreeVisitor
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly ILogger _logger;

        public ReceiptsVerificationVisitor(long endLevel, IReceiptStorage receiptStorage, ILogManager logManager)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            EndLevelExclusive = endLevel + 1;
        }

        public bool PreventsAcceptingNewBlocks => false;
        public long StartLevelInclusive => 3729844;
        public long EndLevelExclusive { get; }
        
        public Task<LevelVisitOutcome> VisitLevelStart(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
        {
            return Task.FromResult(LevelVisitOutcome.None);
        }

        public Task<bool> VisitMissing(Keccak hash, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<HeaderVisitOutcome> VisitHeader(BlockHeader header, CancellationToken cancellationToken) =>
            Task.FromResult(HeaderVisitOutcome.None);

        private long _reviewed = 0;
        private long _lastReported = 0;
        private int good = 0;
        private int bad = 0;
        
        public Task<BlockVisitOutcome> VisitBlock(Block block, CancellationToken cancellationToken)
        {
            int txReceiptsLength = GetTxReceiptsLength(block, false);
            int transactionsLength = (block.Transactions?.Length ?? 0);
            if (txReceiptsLength != transactionsLength)
            {
                bad++;
                if (_logger.IsError) _logger.Error($"Missing receipts for block {block.ToString(Block.Format.FullHashAndNumber)}, expected {transactionsLength} but got {txReceiptsLength}. Good {good}, Bad {bad}");
            }
            else
            {
                good++;
                if (_logger.IsInfo) _logger.Info($"OK Receipts for block {block.ToString(Block.Format.FullHashAndNumber)}, expected {transactionsLength}. Good {good}, Bad {bad}");
            }

            if (_lastReported < (_reviewed * 100 / EndLevelExclusive))
            {
                _lastReported++;
                if (_logger.IsInfo) _logger.Info($"Reviewed {_lastReported}% Receipts.");
            }
            
            _reviewed++;
            return Task.FromResult(BlockVisitOutcome.None);
        }

        private int GetTxReceiptsLength(Block block, bool useIterator)
        {
            if (useIterator)
            {
                int txReceiptsLength = 0;
                if (_receiptStorage.TryGetReceiptsIterator(block.Number, block.Hash, out var iterator))
                {
                    try
                    {
                        while (iterator.TryGetNext(out _))
                        {
                            txReceiptsLength++;
                        }
                    }
                    finally
                    {
                        iterator.Dispose();
                    }
                }

                return txReceiptsLength;
            }
            else
            {
                return _receiptStorage.Get(block)?.Where(r => r != null).Count() ?? 0;
            }
        }

        public Task<LevelVisitOutcome> VisitLevelEnd(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken) =>
            Task.FromResult(LevelVisitOutcome.None);
    }
}
