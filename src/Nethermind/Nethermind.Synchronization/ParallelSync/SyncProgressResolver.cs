// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncProgressResolver(
        IBlockTree blockTree,
        IFullStateFinder fullStateFinder,
        ISyncConfig syncConfig,
        [KeyFilter(nameof(HeadersSyncFeed))] ISyncFeed<HeadersSyncBatch?> headersSyncFeed,
        ISyncFeed<BodiesSyncBatch?> bodiesSyncFeed,
        ISyncFeed<ReceiptsSyncBatch?> receiptsSyncFeed,
        ISyncFeed<SnapSyncBatch?> snapSyncFeed)
        : ISyncProgressResolver
    {
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly ISyncConfig _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
        private readonly IFullStateFinder _fullStateFinder = fullStateFinder ?? throw new ArgumentNullException(nameof(fullStateFinder));

        public long FindBestFullState() => _fullStateFinder.FindBestFullState();
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

            UInt256? totalDifficulty = _blockTree.FindHeader(blockHash)?.TotalDifficulty;
            return totalDifficulty?.IsZero == true ? null : totalDifficulty;
        }

        public bool IsFastBlocksHeadersFinished() => !IsFastBlocks() || !_syncConfig.DownloadHeadersInFastSync || headersSyncFeed.IsFinished;
        public bool IsFastBlocksBodiesFinished() => !IsFastBlocks() || !_syncConfig.DownloadBodiesInFastSync || bodiesSyncFeed.IsFinished;
        public bool IsFastBlocksReceiptsFinished() => !IsFastBlocks() || !_syncConfig.DownloadReceiptsInFastSync || receiptsSyncFeed.IsFinished;
        public bool IsSnapGetRangesFinished() => snapSyncFeed?.IsFinished ?? true;
        public void RecalculateProgressPointers() => _blockTree.RecalculateTreeLevels();
        public (long BlockNumber, Hash256 BlockHash) SyncPivot => _blockTree.SyncPivot;
        private bool IsFastBlocks() => _syncConfig.FastSync && _blockTree.SyncPivot.BlockNumber != 0L; // if pivot number is 0 then it is equivalent to fast blocks disabled
    }
}
