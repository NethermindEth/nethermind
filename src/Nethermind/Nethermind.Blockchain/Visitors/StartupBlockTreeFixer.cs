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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Visitors
{
    public class StartupBlockTreeFixer : IBlockTreeVisitor
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private long _startNumber;
        private long _blocksToLoad;

        private ChainLevelInfo _currentLevel;
        private long _currentLevelNumber;
        private long _blocksCheckedInCurrentLevel;
        private long _bodiesInCurrentLevel;

        private long? _gapStart;
        private long? _lastProcessedLevel;
        private long? _processingGapStart;

        public StartupBlockTreeFixer(ISyncConfig syncConfig, IBlockTree blockTree, ILogger logger)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            long assumedHead = _blockTree.Head?.Number ?? 0;
            _startNumber = Math.Max(syncConfig.PivotNumberParsed, assumedHead + 1);
            _blocksToLoad = (assumedHead + 1) >= _startNumber ? (_blockTree.BestKnownNumber - _startNumber + 1) : 0;

            _currentLevelNumber = _startNumber - 1; // because we always increment on entering

            LogPlannedOperation();
        }

        public bool PreventsAcceptingNewBlocks => true;
        public long StartLevelInclusive => _startNumber;

        public long EndLevelExclusive => _startNumber + _blocksToLoad;

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevelStart(ChainLevelInfo chainLevelInfo, CancellationToken cancellationToken)
        {
            if (_currentLevelNumber >= EndLevelExclusive - 1)
            {
                throw new InvalidOperationException($"Not expecting to visit level past my {EndLevelExclusive}");
            }

            _blocksCheckedInCurrentLevel = 0;
            _bodiesInCurrentLevel = 0;

            _currentLevelNumber++;
            _currentLevel = chainLevelInfo;

            if ((_currentLevelNumber - StartLevelInclusive) % 10000 == 0)
            {
                if(_logger.IsInfo) _logger.Info($"Reviewed {_currentLevelNumber - StartLevelInclusive} blocks out of {EndLevelExclusive - StartLevelInclusive}");
            }
            
            if (_gapStart != null)
            {
                _currentLevel = null;
                return Task.FromResult(LevelVisitOutcome.DeleteLevel);
            }

            if (chainLevelInfo == null)
            {
                _gapStart = _currentLevelNumber;
            }

            WarnAboutProcessingGaps(chainLevelInfo);
            return Task.FromResult(LevelVisitOutcome.None);
        }

        private void WarnAboutProcessingGaps(ChainLevelInfo chainLevelInfo)
        {
            bool thisLevelWasProcessed = chainLevelInfo?.BlockInfos.Any(b => b.WasProcessed) ?? false;
            if (thisLevelWasProcessed)
            {
                if (_processingGapStart != null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Detected processed blocks gap between {_processingGapStart} and {_currentLevelNumber}");
                    _processingGapStart = null;
                }

                _lastProcessedLevel = _currentLevelNumber;
            }
            else if (_lastProcessedLevel == _currentLevelNumber - 1)
            {
                _processingGapStart = _currentLevelNumber;
            }
        }

        Task<bool> IBlockTreeVisitor.VisitMissing(Keccak hash, CancellationToken cancellationToken)
        {
            AssertNotVisitingAfterGap();
            _blocksCheckedInCurrentLevel++;
            if (_logger.IsWarn) _logger.Warn($"Discovered a missing block for hash {hash} at level {_currentLevelNumber}. This means there is a minor chain level corruption that in general should not lead to any issues but is a result of incorrect node behaviour in the past.");
            return Task.FromResult(true);
        }

        Task<bool> IBlockTreeVisitor.VisitHeader(BlockHeader header, CancellationToken cancellationToken)
        {
            AssertNotVisitingAfterGap();
            _blocksCheckedInCurrentLevel++;
            return Task.FromResult(true);
        }

        Task<BlockVisitOutcome> IBlockTreeVisitor.VisitBlock(Block block, CancellationToken cancellationToken)
        {
            AssertNotVisitingAfterGap();
            _blocksCheckedInCurrentLevel++;
            _bodiesInCurrentLevel++;
            return Task.FromResult(BlockVisitOutcome.None);
        }

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevelEnd(CancellationToken cancellationToken)
        {
            int expectedVisitedBlocksCount = _currentLevel?.BlockInfos.Length ?? 0;
            if (_blocksCheckedInCurrentLevel != expectedVisitedBlocksCount)
            {
                throw new InvalidDataException($"Some blocks have not been visited at level {_currentLevelNumber}: {_blocksCheckedInCurrentLevel}/{expectedVisitedBlocksCount}");
            }

            if (_bodiesInCurrentLevel > expectedVisitedBlocksCount)
            {
                throw new InvalidOperationException($"Invalid bodies count at level {_currentLevelNumber}: {_bodiesInCurrentLevel}/{expectedVisitedBlocksCount}");
            }

            if (_gapStart != null)
            {
                if(_logger.IsWarn) _logger.Warn($"Found a gap in blocks after last shutdown at level {_currentLevelNumber}. The node will attempt to continue (the problem may be auto-corrected).");
                // if(_logger.IsInfo) _logger.Info($"Found a gap in blocks after last shutdown - deleting {_currentLevelNumber}");
                return Task.FromResult(LevelVisitOutcome.StopVisiting);
                // return Task.FromResult(LevelVisitOutcome.DeleteLevel);
            }

            if (_bodiesInCurrentLevel == 0)
            {
                _gapStart = _currentLevelNumber;
                return Task.FromResult(LevelVisitOutcome.None);
                // return Task.FromResult(LevelVisitOutcome.DeleteLevel);
            }

            return Task.FromResult(LevelVisitOutcome.None);
        }

        private void AssertNotVisitingAfterGap()
        {
            if (_gapStart != null)
            {
                throw new InvalidOperationException($"Not expecting to visit block at {_currentLevelNumber} because the gap has already been identified.");
            }
        }
        
        private void LogPlannedOperation()
        {
            if (_blocksToLoad == 0)
            {
                if (_logger.IsInfo) _logger.Info("No block tree levels to review for fixes. All fine.");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Found {_blocksToLoad} block tree levels to review for fixes starting from {StartLevelInclusive}");
            }
        }
    }
}