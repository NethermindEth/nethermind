// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        protected readonly ILogger _logger;
        private int _good = 0;
        private int _bad = 0;
        private ChainLevelInfo _currentLevel;
        private long _checked = 0;
        private readonly long _toCheck;

        public ReceiptsVerificationVisitor(long startLevel, long endLevel, IReceiptStorage receiptStorage, ILogManager logManager)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            EndLevelExclusive = endLevel;
            StartLevelInclusive = startLevel; // we should start post-pivot
            _toCheck = EndLevelExclusive - StartLevelInclusive;
        }

        public bool PreventsAcceptingNewBlocks => false;
        public long StartLevelInclusive { get; }

        public long EndLevelExclusive { get; }

        public Task<LevelVisitOutcome> VisitLevelStart(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
        {
            _currentLevel = chainLevelInfo;
            return Task.FromResult(LevelVisitOutcome.None);
        }

        public Task<bool> VisitMissing(Keccak hash, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<HeaderVisitOutcome> VisitHeader(BlockHeader header, CancellationToken cancellationToken) =>
            Task.FromResult(HeaderVisitOutcome.None);

        public virtual async Task<BlockVisitOutcome> VisitBlock(Block block, CancellationToken cancellationToken)
        {
            int txReceiptsLength = GetTxReceiptsLength(block, true);
            int transactionsLength = block.Transactions.Length;
            if (txReceiptsLength != transactionsLength)
            {
                if (_currentLevel.MainChainBlock?.BlockHash == block.Hash)
                {
                    _bad++;
                    await OnBlockWithoutReceipts(block, transactionsLength, txReceiptsLength);
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug($"Missing receipts for non-canonical block  {block.ToString(Block.Format.FullHashAndNumber)}, expected {transactionsLength} but got {txReceiptsLength}. Good {_good}, Bad {_bad}");
                }

            }
            else
            {
                _good++;
                if (_logger.IsDebug) _logger.Debug($"OK Receipts for block {block.ToString(Block.Format.FullHashAndNumber)}, expected {transactionsLength}. Good {_good}, Bad {_bad}");
            }

            return BlockVisitOutcome.None;
        }

        protected virtual Task OnBlockWithoutReceipts(Block block, int transactionsLength, int txReceiptsLength)
        {
            if (_logger.IsError) _logger.Error($"Missing receipts for block {block.ToString(Block.Format.FullHashAndNumber)}, expected {transactionsLength} but got {txReceiptsLength}. Good {_good}, Bad {_bad}");
            return Task.CompletedTask;
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
                return _receiptStorage.Get(block)?.Where(r => r is not null).Count() ?? 0;
            }
        }

        public Task<LevelVisitOutcome> VisitLevelEnd(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
        {
            _checked++;
            if (_checked % 1000 == 0)
            {
                if (_logger.IsInfo) _logger.Info($"Checking receipts {_checked}/{_toCheck}");
            }
            return Task.FromResult(LevelVisitOutcome.None);
        }
    }
}
