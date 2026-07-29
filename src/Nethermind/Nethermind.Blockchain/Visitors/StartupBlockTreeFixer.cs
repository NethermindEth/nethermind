// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain.Visitors
{
    public class StartupBlockTreeFixer : IBlockTreeVisitor
    {
        public const int DefaultBatchSize = 4000;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly ILogger _logger;
        private readonly ProgressReporter _progress;
        private readonly ulong _startNumber;
        private readonly ulong _blocksToLoad;

        private ChainLevelInfo _currentLevel;
        private ulong _currentLevelNumber;
        private ulong _blocksCheckedInCurrentLevel;
        private ulong _bodiesInCurrentLevel;

        private ulong? _gapStart;
        private ulong? _lastProcessedLevel;
        private ulong? _processingGapStart;

        private bool _firstBlockVisited = true;
        private bool _suggestBlocks = true;
        private readonly BlockTreeSuggestPacer _blockTreeSuggestPacer;

        public StartupBlockTreeFixer(
            ISyncConfig syncConfig,
            IBlockTree blockTree,
            IStateReader stateReader,
            ILogManager logManager,
            ulong batchSize = DefaultBatchSize)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockTreeSuggestPacer = new BlockTreeSuggestPacer(_blockTree, batchSize, batchSize / 2);
            _stateReader = stateReader;
            _logger = logManager.GetClassLogger<StartupBlockTreeFixer>();

            ulong assumedHead = _blockTree.Head?.Number ?? 0;
            _startNumber = Math.Max(_blockTree.SyncPivot.BlockNumber, assumedHead + 1);
            _blocksToLoad = (assumedHead + 1) >= _startNumber ? (_blockTree.BestKnownNumber - _startNumber + 1) : 0;

            _currentLevelNumber = _startNumber - 1; // because we always increment on entering
            _progress = new ProgressReporter("Startup fixer", logManager, _blocksToLoad);
            LogPlannedOperation();
        }

        public bool PreventsAcceptingNewBlocks => true;
        public bool CalculateTotalDifficultyIfMissing => true;
        public ulong StartLevelInclusive => _startNumber;

        public ulong EndLevelExclusive => _startNumber + _blocksToLoad;

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevelStart(ChainLevelInfo chainLevelInfo, ulong levelNumber,
            CancellationToken cancellationToken)
        {
            if (_currentLevelNumber >= EndLevelExclusive - 1)
            {
                throw new InvalidOperationException($"Not expecting to visit level past my {EndLevelExclusive}");
            }

            _blocksCheckedInCurrentLevel = 0;
            _bodiesInCurrentLevel = 0;

            _currentLevelNumber++;
            _currentLevel = chainLevelInfo;

            _progress.Update(_currentLevelNumber - StartLevelInclusive);

            if (_gapStart is not null)
            {
                _currentLevel = null;
                return Task.FromResult(LevelVisitOutcome.DeleteLevel);
            }

            if (chainLevelInfo is null)
            {
                _gapStart = _currentLevelNumber;
            }

            WarnAboutProcessingGaps(chainLevelInfo);
            return Task.FromResult(LevelVisitOutcome.None);
        }

        private void WarnAboutProcessingGaps(ChainLevelInfo chainLevelInfo)
        {
            bool thisLevelWasProcessed = chainLevelInfo?.BlockInfos.Any(static b => b.WasProcessed) ?? false;
            if (thisLevelWasProcessed)
            {
                if (_processingGapStart is not null)
                {
                    if (_logger.IsWarn)
                        _logger.Warn(
                            $"Detected processed blocks gap between {_processingGapStart} and {_currentLevelNumber}");
                    _processingGapStart = null;
                }

                _lastProcessedLevel = _currentLevelNumber;
            }
            else if (_lastProcessedLevel == _currentLevelNumber - 1)
            {
                _processingGapStart = _currentLevelNumber;
            }
        }

        Task<bool> IBlockTreeVisitor.VisitMissing(Hash256 hash, CancellationToken cancellationToken)
        {
            AssertNotVisitingAfterGap();
            _blocksCheckedInCurrentLevel++;
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Discovered a missing block for hash {hash} at level {_currentLevelNumber}. This means there is a minor chain level corruption that in general should not lead to any issues but is a result of incorrect node behaviour in the past.");
            return Task.FromResult(true);
        }

        Task<HeaderVisitOutcome> IBlockTreeVisitor.VisitHeader(BlockHeader header, CancellationToken cancellationToken)
        {
            AssertNotVisitingAfterGap();
            _blocksCheckedInCurrentLevel++;
            return Task.FromResult(HeaderVisitOutcome.None);
        }

        async Task<BlockVisitOutcome> IBlockTreeVisitor.VisitBlock(Block block, CancellationToken cancellationToken)
        {
            AssertNotVisitingAfterGap();
            _blocksCheckedInCurrentLevel++;
            _bodiesInCurrentLevel++;

            if (_firstBlockVisited)
            {
                _suggestBlocks = CanSuggestBlocks(block);
            }

            if (!_suggestBlocks) return BlockVisitOutcome.None;

            Task waitSuggestQueue = _blockTreeSuggestPacer.WaitForQueue(block.Number, cancellationToken);

            ulong i = block.Number - StartLevelInclusive;
            if (!waitSuggestQueue.IsCompleted)
            {
                if (_logger.IsInfo)
                {
                    _logger.Info(
                        $"Loaded {i + 1} out of {_blocksToLoad} blocks from DB into processing queue, waiting for processor before loading more.");
                }

                await waitSuggestQueue;
            }

            return BlockVisitOutcome.Suggest;

        }

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevelEnd(ChainLevelInfo chainLevelInfo, ulong levelNumber,
            CancellationToken cancellationToken)
        {
            ulong expectedVisitedBlocksCount = (ulong)(_currentLevel?.BlockInfos.Length ?? 0);
            if (_blocksCheckedInCurrentLevel != expectedVisitedBlocksCount)
            {
                throw new InvalidDataException(
                    $"Some blocks have not been visited at level {_currentLevelNumber}: {_blocksCheckedInCurrentLevel}/{expectedVisitedBlocksCount}");
            }

            if (_bodiesInCurrentLevel > expectedVisitedBlocksCount)
            {
                throw new InvalidOperationException(
                    $"Invalid bodies count at level {_currentLevelNumber}: {_bodiesInCurrentLevel}/{expectedVisitedBlocksCount}");
            }

            if (_gapStart is not null)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Found a gap in blocks after last shutdown at level {_currentLevelNumber}. The node will attempt to continue (the problem may be auto-corrected).");
                // if(_logger.IsInfo) _logger.Info($"Found a gap in blocks after last shutdown - deleting {_currentLevelNumber}");
                // return Task.FromResult(LevelVisitOutcome.StopVisiting);
                return Task.FromResult(LevelVisitOutcome.DeleteLevel);
            }

            if (_bodiesInCurrentLevel == 0)
            {
                _gapStart = _currentLevelNumber;
                // return Task.FromResult(LevelVisitOutcome.None);
                return Task.FromResult(LevelVisitOutcome.DeleteLevel);
            }

            return Task.FromResult(LevelVisitOutcome.None);
        }

        private void AssertNotVisitingAfterGap()
        {
            if (_gapStart is not null)
            {
                throw new InvalidOperationException(
                    $"Not expecting to visit block at {_currentLevelNumber} because the gap has already been identified.");
            }
        }

        private bool CanSuggestBlocks(Block block)
        {
            _firstBlockVisited = false;
            if (block?.ParentHash is not null)
            {
                BlockHeader? parentHeader = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (parentHeader is null || parentHeader.StateRoot is null ||
                    !_stateReader.HasStateForBlock(parentHeader))
                    return false;
            }
            else
            {
                return false;
            }

            return true;
        }

        private void LogPlannedOperation()
        {
            if (_blocksToLoad == 0)
            {
                if (_logger.IsInfo) _logger.Info("No block tree levels to review for fixes. All fine.");
            }
            else
            {
                if (_logger.IsInfo)
                    _logger.Info(
                        $"Found {_blocksToLoad} block tree levels to review for fixes starting from {StartLevelInclusive}");
            }
        }

        public void Dispose()
        {
            _progress.Dispose();
            _blockTreeSuggestPacer.Dispose();
        }
    }
}
