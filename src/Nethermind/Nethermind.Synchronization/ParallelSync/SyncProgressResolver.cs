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

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncProgressResolver : ISyncProgressResolver
    {
        private const int _maxLookup = 64;

        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IDb _stateDb;
        private readonly ISyncConfig _syncConfig;
        private ILogger _logger;

        public SyncProgressResolver(IBlockTree blockTree, IReceiptStorage receiptStorage, IDb stateDb, ISyncConfig syncConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
        }

        private bool IsFullySynced(Keccak stateRoot)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return true;
            }

            return _stateDb.Get(stateRoot) != null;
        }

        public long FindBestFullState()
        {
            /* There is an interesting scenario (unlikely) here where we download more than 'full sync threshold'
             blocks in full sync but they are not processed immediately so we switch to node sync
             and the blocks that we downloaded are processed from their respective roots
             and the next full sync will be after a leap.
             This scenario is still correct. It may be worth to analyze what happens
             when it causes a full sync vs node sync race at every block.*/

            BlockHeader bestSuggested = _blockTree.BestSuggestedHeader;
            BlockHeader head = _blockTree.Head;
            long bestFullState = head?.Number ?? 0;
            long maxLookup = Math.Min(_maxLookup * 2, (bestSuggested?.Number ?? 0L) - bestFullState);

            for (int i = 0; i < maxLookup; i++)
            {
                if (bestSuggested == null)
                {
                    break;
                }

                if (_syncConfig.BeamSync)
                {
                    bestFullState = bestSuggested.Number;
                    break;
                }

                if (IsFullySynced(bestSuggested.StateRoot))
                {
                    bestFullState = bestSuggested.Number;
                    break;
                }

                bestSuggested = _blockTree.FindHeader(bestSuggested.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }

            return bestFullState;
        }

        public long FindBestHeader() => _blockTree.BestSuggestedHeader?.Number ?? 0;

        public long FindBestFullBlock() => Math.Min(FindBestHeader(), _blockTree.BestSuggestedBody?.Number ?? 0); // avoiding any potential concurrency issue

        public long FindBestProcessedBlock() => _blockTree.Head?.Number ?? -1;
        
        public bool IsFastBlocksFinished()
        {
            bool isFastBlocks = _syncConfig.FastBlocks;
            bool isBeamSync = _syncConfig.BeamSync;

            // if pivot number is 0 then it is equivalent to fast blocks disabled
            if (!isFastBlocks || _syncConfig.PivotNumberParsed == 0L)
            {
                return true;
            }

            bool anyHeaderDownloaded = (_blockTree.LowestInsertedHeader?.Number ?? long.MaxValue) <= _syncConfig.PivotNumberParsed;
            bool allHeadersDownloaded = (_blockTree.LowestInsertedHeader?.Number ?? long.MaxValue) <= 1;
            bool allReceiptsDownloaded = !_syncConfig.DownloadReceiptsInFastSync || (_receiptStorage.LowestInsertedReceiptBlock ?? long.MaxValue) <= 1;
            bool allBodiesDownloaded = !_syncConfig.DownloadBodiesInFastSync || (_blockTree.LowestInsertedBody?.Number ?? long.MaxValue) <= 1;

            return allBodiesDownloaded && allHeadersDownloaded && allReceiptsDownloaded
                   || isBeamSync && anyHeaderDownloaded;
        }
    }
}