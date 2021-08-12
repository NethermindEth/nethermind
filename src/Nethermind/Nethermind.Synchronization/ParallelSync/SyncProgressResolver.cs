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

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

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
        private readonly IDb? _beamStateDb;
        private readonly ITrieNodeResolver _trieNodeResolver;
        private readonly ISyncConfig _syncConfig;

        // ReSharper disable once NotAccessedField.Local
        private ILogger _logger;

        private long _bodiesBarrier;
        private long _receiptsBarrier;

        public SyncProgressResolver(
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IDb stateDb,
            IDb? beamStateDb,
            ITrieNodeResolver trieNodeResolver,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _beamStateDb = beamStateDb;
            _trieNodeResolver = trieNodeResolver ?? throw new ArgumentNullException(nameof(trieNodeResolver));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));

            _bodiesBarrier = _syncConfig.AncientBodiesBarrierCalc;
            _receiptsBarrier = _syncConfig.AncientReceiptsBarrierCalc;
        }

        private bool IsFullySynced(Keccak stateRoot)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return true;
            }

            if (_syncConfig.BeamSync)
            {
                // in Beam Sync we the innermost DB as we may have state root in the beam sync DB
                // which does not yet mean that we have the block synced
                return _stateDb.Innermost.Get(stateRoot) != null;
            }
            else
            {
                TrieNode trieNode = _trieNodeResolver.FindCachedOrUnknown(stateRoot);
                bool stateRootIsInMemory = trieNode.NodeType != NodeType.Unknown;
                // We check whether one of below happened:
                //   1) the block has been processed but not yet persisted (pruning) OR
                //   2) the block has been persisted and removed from cache already OR
                //   3) the full block state has been synced in the state nodes sync (fast sync)
                // In 2) and 3) the state root will be saved in the database.
                // In fast sync we never save the state root unless all the descendant nodes have been stored in the DB.
                return stateRootIsInMemory || _stateDb.Get(stateRoot) != null;
            }
        }

        // ReSharper disable once UnusedMember.Local
        private bool IsBeamSynced(Keccak stateRoot)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return true;
            }

            return _beamStateDb?.Innermost.Get(stateRoot) != null;
        }

        public long FindBestFullState()
        {
            // so the full state can be in a few places but there are some best guesses
            // if we are state syncing then the full state may be one of the recent blocks (maybe one of the last 128 blocks)
            // if we full syncing then the state should be at head
            // if we are beam syncing then the state should be in a different DB and should not cause much trouble here
            // it also may seem tricky if best suggested is part of a reorg while we are already full syncing so
            // ideally we would like to check its siblings too (but this may be a bit expensive and less likely
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

                if (IsFullySynced(startHeader.StateRoot!))
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

        public UInt256? GetTotalDifficulty(Keccak blockHash)
        {
            BlockHeader best = _blockTree.BestSuggestedHeader;

            if (best != null)
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

            return _blockTree.FindHeader(blockHash)?.TotalDifficulty;
        }

        public bool IsFastBlocksHeadersFinished() => !IsFastBlocks() || (!_syncConfig.DownloadHeadersInFastSync || (_blockTree.LowestInsertedHeader?.Number ?? long.MaxValue) <= 1);
        
        public bool IsFastBlocksBodiesFinished() => !IsFastBlocks() || (!_syncConfig.DownloadBodiesInFastSync || (_blockTree.LowestInsertedBodyNumber ?? long.MaxValue) <= _bodiesBarrier);

        public bool IsFastBlocksReceiptsFinished() => !IsFastBlocks() || (!_syncConfig.DownloadReceiptsInFastSync || (_receiptStorage.LowestInsertedReceiptBlockNumber ?? long.MaxValue) <= _receiptsBarrier);

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
