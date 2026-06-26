// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Visitors
{
    public class DbBlocksLoader : IBlockTreeVisitor
    {
        public const int DefaultBatchSize = 4000;

        private readonly ulong _blocksToLoad;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly ProgressReporter _progress;

        private readonly BlockTreeSuggestPacer _blockTreeSuggestPacer;

        public DbBlocksLoader(IBlockTree blockTree,
            ILogManager logManager,
            ulong? startBlockNumber = null,
            ulong batchSize = DefaultBatchSize,
            ulong maxBlocksToLoad = ulong.MaxValue)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockTreeSuggestPacer = new BlockTreeSuggestPacer(_blockTree, batchSize, batchSize / 2);
            _logger = logManager.GetClassLogger<DbBlocksLoader>();

            StartLevelInclusive = startBlockNumber ?? (_blockTree.Head?.Number + 1) ?? 0UL;
            ulong bestKnown = _blockTree.BestKnownNumber;
            _blocksToLoad = Math.Min(maxBlocksToLoad, bestKnown.SaturatingSub(StartLevelInclusive));
            EndLevelExclusive = StartLevelInclusive + _blocksToLoad + 1;

            _progress = new ProgressReporter("DB blocks load", logManager, _blocksToLoad);

            LogPlannedOperation();
        }

        public bool PreventsAcceptingNewBlocks => true;
        public bool CalculateTotalDifficultyIfMissing => true;
        public ulong StartLevelInclusive { get; }

        public ulong EndLevelExclusive { get; }

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevelStart(ChainLevelInfo? chainLevelInfo, ulong levelNumber, CancellationToken cancellationToken)
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

        Task<bool> IBlockTreeVisitor.VisitMissing(Hash256 hash, CancellationToken cancellationToken) => throw new InvalidDataException($"Block {hash} is missing from the database when loading blocks.");

        Task<HeaderVisitOutcome> IBlockTreeVisitor.VisitHeader(BlockHeader header, CancellationToken cancellationToken)
        {
            _progress.Update(header.Number - StartLevelInclusive + 1);
            return Task.FromResult(HeaderVisitOutcome.None);
        }

        async Task<BlockVisitOutcome> IBlockTreeVisitor.VisitBlock(Block block, CancellationToken cancellationToken)
        {
            // this will hang
            Task waitTask = _blockTreeSuggestPacer.WaitForQueue(block.Number, cancellationToken);

            ulong i = block.Number - StartLevelInclusive;
            if (!waitTask.IsCompleted)
            {
                if (_logger.IsInfo)
                {
                    _logger.Info($"Loaded {i + 1} out of {_blocksToLoad} blocks from DB into processing queue, waiting for processor before loading more.");
                }

                await waitTask;
            }

            return BlockVisitOutcome.Suggest;
        }

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevelEnd(ChainLevelInfo? chainLevelInfo, ulong levelNumber, CancellationToken cancellationToken) => Task.FromResult(LevelVisitOutcome.None);

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

        public void Dispose()
        {
            _progress.Dispose();
            _blockTreeSuggestPacer.Dispose();
        }
    }
}
