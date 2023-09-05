// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.ParallelSync
{
    public class SyncProgressResolver : ISyncProgressResolver
    {
        // TODO: we can search 1024 back and confirm 128 deep header and start using it as Max(0, confirmed)
        // then we will never have to look 128 back again
        // note that we will be doing that every second or so
        private const int MaxLookupBack = 192;

        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IDb _stateDb;
        private readonly ITrieNodeResolver _trieNodeResolver;
        private readonly ProgressTracker _progressTracker;
        private readonly ISyncConfig _syncConfig;

        // ReSharper disable once NotAccessedField.Local
        private ILogger _logger;

        private readonly long _bodiesBarrier;
        private readonly long _receiptsBarrier;

        public SyncProgressResolver(IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IDb stateDb,
            ITrieNodeResolver trieNodeResolver,
            ProgressTracker progressTracker,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _trieNodeResolver = trieNodeResolver ?? throw new ArgumentNullException(nameof(trieNodeResolver));
            _progressTracker = progressTracker ?? throw new ArgumentNullException(nameof(progressTracker));
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

            TrieNode trieNode = _trieNodeResolver.FindCachedOrUnknown(stateRoot, Array.Empty<byte>(), Array.Empty<byte>());
            if (trieNode is null) return false;
            bool stateRootIsInMemory = trieNode.NodeType != NodeType.Unknown;
            // We check whether one of below happened:
            //   1) the block has been processed but not yet persisted (pruning) OR
            //   2) the block has been persisted and removed from cache already OR
            //   3) the full block state has been synced in the state nodes sync (fast sync)
            // In 2) and 3) the state root will be saved in the database.
            // In fast sync we never save the state root unless all the descendant nodes have been stored in the DB.
            return stateRootIsInMemory || _trieNodeResolver.ExistsInDB(stateRoot, Array.Empty<byte>());
        }

        public long FindBestFullState()
        {
            // so the full state can be in a few places but there are some best guesses
            // if we are state syncing then the full state may be one of the recent blocks (maybe one of the last 128 blocks)
            // if we full syncing then the state should be at head
            // it also may seem tricky if best suggested is part of a reorg while we are already full syncing so
            // ideally we would like to check its siblings too (but this may be a bit expensive and less likely
            // to be important
            // we want to avoid a scenario where state is not found even as it is just near head or best suggested

            Block head = _blockTree.Head;
            BlockHeader initialBestSuggested = _blockTree.BestSuggestedHeader; // just storing here for debugging sake
            BlockHeader bestSuggested = initialBestSuggested;

            long bestFullState = 0;
            if (head is not null)
            {
                // head search should be very inexpensive as we generally expect the state to be there
                bestFullState = SearchForFullState(head.Header);
            }

            if (bestSuggested is not null)
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
            for (int i = 0; i < MaxLookupBack; i++)
            {
                if (startHeader is null)
                {
                    break;
                }

                if (IsFullySynced(startHeader.StateRoot!))
                {
                    bestFullState = startHeader.Number;
                    break;
                }

                startHeader = _blockTree.FindHeader(startHeader.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }

            return bestFullState;
        }

        public long FindBestHeader() => _blockTree.BestSuggestedHeader?.Number ?? 0;
        public long FindBestFullBlock() => Math.Min(FindBestHeader(), _blockTree.BestSuggestedBody?.Number ?? 0); // avoiding any potential concurrency issue

        public bool IsLoadingBlocksFromDb() => !_blockTree.CanAcceptNewBlocks;

        public long FindBestProcessedBlock() => _blockTree.Head?.Number ?? -1;

        public UInt256 ChainDifficulty => _blockTree.BestSuggestedBody?.TotalDifficulty ?? UInt256.Zero;

        public UInt256? GetTotalDifficulty(Keccak blockHash)
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

        public bool IsFastBlocksHeadersFinished() => !IsFastBlocks() || (!_syncConfig.DownloadHeadersInFastSync ||
                                                                         (_blockTree.LowestInsertedHeader?.Number ??
                                                                          long.MaxValue) <= 1);

        public bool IsFastBlocksBodiesFinished() => !IsFastBlocks() || (!_syncConfig.DownloadBodiesInFastSync ||
                                                                        (_blockTree.LowestInsertedBodyNumber ??
                                                                         long.MaxValue) <= _bodiesBarrier);

        public bool IsFastBlocksReceiptsFinished() => !IsFastBlocks() || (!_syncConfig.DownloadReceiptsInFastSync ||
                                                                          (_receiptStorage
                                                                               .LowestInsertedReceiptBlockNumber ??
                                                                           long.MaxValue) <= _receiptsBarrier);

        public bool IsSnapGetRangesFinished() => _progressTracker.IsSnapGetRangesFinished();

        public void RecalculateProgressPointers() => _blockTree.RecalculateTreeLevels();

        private bool IsFastBlocks()
        {
            bool isFastBlocks = _syncConfig.FastBlocks;

            // if pivot number is 0 then it is equivalent to fast blocks disabled
            if (!isFastBlocks || _syncConfig.PivotNumberParsed == 0L)
            {
                return false;
            }

            return true;
        }
    }
}
