// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.VerkleSync;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncProgressResolver : ISyncProgressResolver
    {

        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly IFullStateFinder _fullStateFinder;

        // ReSharper disable once NotAccessedField.Local
        private readonly ILogger _logger;

        private readonly ISyncFeed<HeadersSyncBatch?>? _headersSyncFeed;
        private readonly ISyncFeed<BodiesSyncBatch?>? _bodiesSyncFeed;
        private readonly ISyncFeed<ReceiptsSyncBatch?>? _receiptsSyncFeed;
        private readonly ISyncFeed<SnapSyncBatch?>? _snapSyncFeed;
        private readonly ISyncFeed<VerkleSyncBatch?>? _verkleSyncFeed;

        public SyncProgressResolver(
            IBlockTree blockTree,
            IFullStateFinder fullStateFinder,
            ISyncConfig syncConfig,
            ISyncFeed<HeadersSyncBatch?>? headersSyncFeed,
            ISyncFeed<BodiesSyncBatch?>? bodiesSyncFeed,
            ISyncFeed<ReceiptsSyncBatch?>? receiptsSyncFeed,
            ISyncFeed<SnapSyncBatch?>? snapSyncFeed,
            ISyncFeed<VerkleSyncBatch?>? verkleSyncFeed,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _fullStateFinder = fullStateFinder ?? throw new ArgumentNullException(nameof(fullStateFinder));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));

            _headersSyncFeed = headersSyncFeed;
            _bodiesSyncFeed = bodiesSyncFeed;
            _receiptsSyncFeed = receiptsSyncFeed;
            _snapSyncFeed = snapSyncFeed;
            _verkleSyncFeed = verkleSyncFeed;
        }

        public long FindBestFullState()
        {
            return _fullStateFinder.FindBestFullState();
        }

        public long FindBestHeader() => _blockTree.BestSuggestedHeader?.Number ?? 0;
        public long FindBestFullBlock() => Math.Min(FindBestHeader(), _blockTree.BestSuggestedBody?.Number ?? 0); // avoiding any potential concurrency issue

        public bool IsLoadingBlocksFromDb() => !_blockTree.CanAcceptNewBlocks;

        public long FindBestProcessedBlock() => _blockTree.Head?.Number ?? -1;

        public UInt256 ChainDifficulty => _blockTree.BestSuggestedBody?.TotalDifficulty ?? UInt256.Zero;

        public UInt256? GetTotalDifficulty(Hash256 blockHash)
        {
            BlockHeader best = _blockTree.BestSuggestedHeader;

            if (best is not null)
            {
                if (best.Hash == blockHash)
                {
                    return best.TotalDifficulty;
                }

                if (best.ParentHash == blockHash)
                {
                    return best.TotalDifficulty - best.Difficulty;
                }
            }

            return _blockTree.FindHeader(blockHash)?.TotalDifficulty == 0 ? null : _blockTree.FindHeader(blockHash)?.TotalDifficulty;
        }

        public bool IsFastBlocksHeadersFinished() => !IsFastBlocks() || !_syncConfig.DownloadHeadersInFastSync || _headersSyncFeed?.IsFinished == true;

        public bool IsFastBlocksBodiesFinished() => !IsFastBlocks() || !_syncConfig.DownloadBodiesInFastSync || _bodiesSyncFeed?.IsFinished == true;

        public bool IsFastBlocksReceiptsFinished() => !IsFastBlocks() || !_syncConfig.DownloadReceiptsInFastSync || _receiptsSyncFeed?.IsFinished == true;

        public bool IsSnapGetRangesFinished() => _snapSyncFeed?.IsFinished ?? true;
        public bool IsVerkleGetRangesFinished() => _verkleSyncFeed?.IsFinished ?? true;

        public void RecalculateProgressPointers() => _blockTree.RecalculateTreeLevels();

        private bool IsFastBlocks() => _syncConfig.FastSync && _syncConfig.PivotNumberParsed != 0L; // if pivot number is 0 then it is equivalent to fast blocks disabled


    }
}
