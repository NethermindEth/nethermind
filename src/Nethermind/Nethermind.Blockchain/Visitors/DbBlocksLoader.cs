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
// 

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Visitors
{
    public class DbBlocksLoader : IBlockTreeVisitor
    {
        public const int DefaultBatchSize = 4000;

        private readonly long _batchSize;
        private readonly long _blocksToLoad;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        private TaskCompletionSource<object> _dbBatchProcessed;
        private long _currentDbLoadBatchEnd;

        public DbBlocksLoader(IBlockTree blockTree,
            ILogger logger,
            long? startBlockNumber = null,
            long batchSize = DefaultBatchSize,
            long maxBlocksToLoad = long.MaxValue)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _batchSize = batchSize;
            StartLevelInclusive = Math.Max(0L, startBlockNumber ?? (_blockTree.Head?.Number + 1) ?? 0L);
            _blocksToLoad = Math.Min(maxBlocksToLoad, _blockTree.BestKnownNumber - StartLevelInclusive);
            EndLevelExclusive = StartLevelInclusive + _blocksToLoad + 1;

            if (_blocksToLoad != 0)
            {
                _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
            }

            LogPlannedOperation();
        }

        private void BlockTreeOnNewHeadBlock(object sender, BlockEventArgs e)
        {
            if (_dbBatchProcessed != null)
            {
                if (e.Block.Number == _currentDbLoadBatchEnd)
                {
                    TaskCompletionSource<object> completionSource = _dbBatchProcessed;
                    _dbBatchProcessed = null;
                    completionSource.SetResult(null);
                }
            }
        }

        public bool PreventsAcceptingNewBlocks => true;
        public long StartLevelInclusive { get; }

        public long EndLevelExclusive { get; }

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevelStart(ChainLevelInfo? chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
        {
            if (chainLevelInfo is null)
            {
                return Task.FromResult(LevelVisitOutcome.StopVisiting);
            }

            if (chainLevelInfo.BlockInfos.Length == 0)
            {
                // this should never happen as we should have run the fixer first
                throw new InvalidDataException("Level has no blocks when loading blocks from the DB");
            }

            return Task.FromResult(LevelVisitOutcome.None);
        }

        Task<bool> IBlockTreeVisitor.VisitMissing(Keccak hash, CancellationToken cancellationToken)
        {
            throw new InvalidDataException($"Block {hash} is missing from the database when loading blocks.");
        }

        Task<HeaderVisitOutcome> IBlockTreeVisitor.VisitHeader(BlockHeader header, CancellationToken cancellationToken)
        {
            long i = header.Number - StartLevelInclusive;
            if (i % _batchSize == _batchSize - 1 && i != _blocksToLoad - 1 && _blockTree.Head.Number + _batchSize < header.Number)
            {
                if (_logger.IsInfo) _logger.Info($"Loaded {i + 1} out of {_blocksToLoad} headers from DB.");
            }

            return Task.FromResult(HeaderVisitOutcome.None);
        }

        async Task<BlockVisitOutcome> IBlockTreeVisitor.VisitBlock(Block block, CancellationToken cancellationToken)
        {
            // this will hang
            long i = block.Number - StartLevelInclusive;
            if (i % _batchSize == _batchSize - 1 && i != _blocksToLoad - 1 && _blockTree.Head.Number + _batchSize < block.Number)
            {
                if (_logger.IsInfo)
                {
                    _logger.Info($"Loaded {i + 1} out of {_blocksToLoad} blocks from DB into processing queue, waiting for processor before loading more.");
                }

                _dbBatchProcessed = new TaskCompletionSource<object>();
                await using (cancellationToken.Register(() => _dbBatchProcessed.SetCanceled()))
                {
                    _currentDbLoadBatchEnd = block.Number - _batchSize;
                    await _dbBatchProcessed.Task;
                }
            }

            return BlockVisitOutcome.Suggest;
        }

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevelEnd(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
        {
            return Task.FromResult(LevelVisitOutcome.None);
        }

        private void LogPlannedOperation()
        {
            if (_blocksToLoad <= 0)
            {
                if (_logger.IsInfo) _logger.Info("Found no blocks to load from DB");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Found {_blocksToLoad} blocks to load from DB starting from current head block {_blockTree.Head?.ToString(Block.Format.Short)}");
            }
        }
    }
}
