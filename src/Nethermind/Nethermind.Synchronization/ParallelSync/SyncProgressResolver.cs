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
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncProgressResolver : ISyncProgressResolver
    {
        // TODO: we can search 1024 back and confirm 128 deep header and start using it as Max(0, confirmed)
        // then we will never have to look 128 back again
        // note that we will be doing that every second or so
        private const int _maxLookupBack = 128;

        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IDb _stateDb;
        private readonly IDb _beamStateDb;
        private readonly ISyncConfig _syncConfig;
        private ILogger _logger;

        public SyncProgressResolver(IBlockTree blockTree, IReceiptStorage receiptStorage, IDb stateDb, IDb beamStateDb, ISyncConfig syncConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _beamStateDb = beamStateDb ?? throw new ArgumentNullException(nameof(beamStateDb));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
        }

        private bool IsFullySynced(Keccak stateRoot)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return true;
            }

            return _stateDb.Innermost.Get(stateRoot) != null;
        }

        private bool IsBeamSynced(Keccak stateRoot)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return true;
            }

            return _beamStateDb.Innermost.Get(stateRoot) != null;
        }

        public long FindBestFullState()
        {
            // so the full state can be in a few places but there are some best guesses
            // if we are state syncing then the full state may be one of the recent blocks (maybe one of the last 128 blocks)
            // if we full syncing then the state should be at head
            // if we are beam syncing then the state should be in a different DB and should not cause much trouble here
            // it also may seem tricky if best suggested is part of a reorg while we are already full syncing so
            // ideally we would like to check it siblings too (but this may be a bit expensive and less likely
            // to be important
            // we want to avoid a scenario where state is not found even as it is just near head or best suggested

            Block head = _blockTree.Head;
            BlockHeader initialBestSuggested = _blockTree.BestSuggestedHeader; // just storing here for debugging sake
            BlockHeader bestSuggested = initialBestSuggested;

            long bestFullState = 0;
            if (head != null)
            {
                // head search should be very inexpensive as we generally expect the state to be there
                bestFullState = SearchForFullState(head.Header);
            }

            if (bestSuggested != null)
            {
                if (bestFullState < bestSuggested?.Number)
                {
                    bestFullState = Math.Max(bestFullState, SearchForFullState(bestSuggested));
                }
            }

            return bestFullState;
        }

        private long SearchForFullState(BlockHeader startHeader)
        {
            long bestFullState = 0;
            for (int i = 0; i < _maxLookupBack; i++)
            {
                if (startHeader == null)
                {
                    break;
                }

                if (IsFullySynced(startHeader.StateRoot))
                {
                    bestFullState = startHeader.Number;
                    break;
                }

                startHeader = _blockTree.FindHeader(startHeader.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }

            return bestFullState;
        }

        public long FindBestHeader() => _blockTree.BestSuggestedHeader?.Number ?? 0;

        public long FindBestFullBlock() => Math.Min(FindBestHeader(), _blockTree.BestSuggestedBody?.Number ?? 0); // avoiding any potential concurrency issue

        public bool IsLoadingBlocksFromDb()
        {
            return !_blockTree.CanAcceptNewBlocks;
        }

        public long FindBestProcessedBlock() => _blockTree.Head?.Number ?? -1;

        public UInt256 ChainDifficulty => _blockTree.BestSuggestedBody?.TotalDifficulty ?? UInt256.Zero;

        public bool IsFastBlocksHeadersFinished() => !IsFastBlocks() || (_blockTree.LowestInsertedHeader?.Number ?? long.MaxValue) <= 1;
        
        public bool IsFastBlocksBodiesFinished() => !IsFastBlocks() || (!_syncConfig.DownloadBodiesInFastSync || (_blockTree.LowestInsertedBody?.Number ?? long.MaxValue) <= 1);

        public bool IsFastBlocksReceiptsFinished() => !IsFastBlocks() || (!_syncConfig.DownloadReceiptsInFastSync || (_receiptStorage.LowestInsertedReceiptBlock ?? long.MaxValue) <= 1);

        private bool IsFastBlocks()
        {
            bool isFastBlocks = _syncConfig.FastBlocks;

            // if pivot number is 0 then it is equivalent to fast blocks disabled
            if (!isFastBlocks || _syncConfig.PivotNumberParsed == 0L)
            {
                return false;
            }
            
            bool immediateBeamSync = !_syncConfig.DownloadHeadersInFastSync;
            bool anyHeaderDownloaded = _blockTree.LowestInsertedHeader != null;
            if (immediateBeamSync && anyHeaderDownloaded)
            {
                return false;
            }

            return true;
        }


    }
}
