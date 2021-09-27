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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;

namespace Nethermind.Blockchain
{
    [Todo(Improve.Refactor, "After the fast sync work there are some duplicated code parts for the 'by header' and 'by block' approaches.")]
    public partial class BlockTree : IBlockTree
    {
        // there is not much logic in the addressing here
        private const long LowestInsertedBodyNumberDbEntryAddress = 0;
        private static byte[] StateHeadHashDbEntryAddress = new byte[16];
        internal static Keccak DeletePointerAddressInDb = new(new BitArray(32 * 8, true).ToBytes());

        internal static Keccak HeadAddressInDb = Keccak.Zero;
        
        private const int CacheSize = 64;
        private readonly ICache<Keccak, Block> _blockCache = new LruCache<Keccak, Block>(CacheSize, CacheSize, "blocks");
        private readonly ICache<Keccak, BlockHeader> _headerCache = new LruCache<Keccak, BlockHeader>(CacheSize, CacheSize, "headers");

        private const int BestKnownSearchLimit = 256_000_000;

        private readonly object _batchInsertLock = new();

        private readonly IDb _blockDb;
        private readonly IDb _headerDb;
        private readonly IDb _blockInfoDb;

        private ICache<long, HashSet<Keccak>> _invalidBlocks = new LruCache<long, HashSet<Keccak>>(128, 128, "invalid blocks");
        private readonly BlockDecoder _blockDecoder = new();
        private readonly HeaderDecoder _headerDecoder = new();
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IBloomStorage _bloomStorage;
        private readonly ISyncConfig _syncConfig;
        private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
        private bool _tryToRecoverFromHeaderBelowBodyCorruption = false;

        public BlockHeader? Genesis { get; private set; }
        public Block? Head { get; private set; }
        public BlockHeader? BestSuggestedHeader { get; private set; }
        public Block? BestSuggestedBody { get; private set; }
        public BlockHeader? LowestInsertedHeader { get; private set; }

        private long? _lowestInsertedReceiptBlock;

        public long? LowestInsertedBodyNumber
        {
            get => _lowestInsertedReceiptBlock;
            set
            {
                _lowestInsertedReceiptBlock = value;
                if (value.HasValue)
                {
                    _blockDb.Set(LowestInsertedBodyNumberDbEntryAddress, Rlp.Encode(value.Value).Bytes);
                }
            }
        }

        public long BestKnownNumber { get; private set; }
        public ulong ChainId => _specProvider.ChainId;

        private int _canAcceptNewBlocksCounter;
        public bool CanAcceptNewBlocks => _canAcceptNewBlocksCounter == 0;

        public BlockTree(
            IDbProvider? dbProvider,
            IChainLevelInfoRepository? chainLevelInfoRepository,
            ISpecProvider? specProvider,
            IBloomStorage? bloomStorage,
            ILogManager? logManager)
            : this(dbProvider?.BlocksDb, dbProvider?.HeadersDb, dbProvider?.BlockInfosDb, chainLevelInfoRepository, specProvider, bloomStorage, new SyncConfig(), logManager)
        {
        }

        public BlockTree(
            IDbProvider? dbProvider,
            IChainLevelInfoRepository? chainLevelInfoRepository,
            ISpecProvider? specProvider,
            IBloomStorage? bloomStorage,
            ISyncConfig? syncConfig,
            ILogManager? logManager)
            : this(dbProvider?.BlocksDb, dbProvider?.HeadersDb, dbProvider?.BlockInfosDb, chainLevelInfoRepository, specProvider, bloomStorage, syncConfig, logManager)
        {
        }

        public BlockTree(
            IDb? blockDb,
            IDb? headerDb,
            IDb? blockInfoDb,
            IChainLevelInfoRepository? chainLevelInfoRepository,
            ISpecProvider? specProvider,
            IBloomStorage? bloomStorage,
            ILogManager? logManager)
            : this(blockDb, headerDb, blockInfoDb, chainLevelInfoRepository, specProvider, bloomStorage, new SyncConfig(), logManager)
        {
        }

        public BlockTree(
            IDb? blockDb,
            IDb? headerDb,
            IDb? blockInfoDb,
            IChainLevelInfoRepository? chainLevelInfoRepository,
            ISpecProvider? specProvider,
            IBloomStorage? bloomStorage,
            ISyncConfig? syncConfig,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockDb = blockDb ?? throw new ArgumentNullException(nameof(blockDb));
            _headerDb = headerDb ?? throw new ArgumentNullException(nameof(headerDb));
            _blockInfoDb = blockInfoDb ?? throw new ArgumentNullException(nameof(blockInfoDb));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _bloomStorage = bloomStorage ?? throw new ArgumentNullException(nameof(bloomStorage));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new ArgumentNullException(nameof(chainLevelInfoRepository));

            byte[]? deletePointer = _blockInfoDb.Get(DeletePointerAddressInDb);
            if (deletePointer is not null)
            {
                DeleteBlocks(new Keccak(deletePointer));
            }

            ChainLevelInfo? genesisLevel = LoadLevel(0);
            if (genesisLevel is not null)
            {
                if (genesisLevel.BlockInfos.Length != 1)
                {
                    // just for corrupted test bases
                    genesisLevel.BlockInfos = new[] {genesisLevel.BlockInfos[0]};
                    _chainLevelInfoRepository.PersistLevel(0, genesisLevel);
                    //throw new InvalidOperationException($"Genesis level in DB has {genesisLevel.BlockInfos.Length} blocks");
                }

                if (genesisLevel.BlockInfos[0].WasProcessed)
                {
                    BlockHeader genesisHeader = FindHeader(genesisLevel.BlockInfos[0].BlockHash, BlockTreeLookupOptions.None);
                    Genesis = genesisHeader;
                    LoadStartBlock();
                }

                RecalculateTreeLevels();
                AttemptToFixCorruptionByMovingHeadBackwards();
            }

            if (_logger.IsInfo)
                _logger.Info($"Block tree initialized, " +
                             $"last processed is {Head?.Header.ToString(BlockHeader.Format.Short) ?? "0"}, " +
                             $"best queued is {BestSuggestedHeader?.Number.ToString() ?? "0"}, " +
                             $"best known is {BestKnownNumber}, " +
                             $"lowest inserted header {LowestInsertedHeader?.Number}, " +
                             $"body {LowestInsertedBodyNumber}");
            ThisNodeInfo.AddInfo("Chain ID     :", $"{Nethermind.Core.ChainId.GetChainName(ChainId)}");
            ThisNodeInfo.AddInfo("Chain head   :", $"{Head?.Header.ToString(BlockHeader.Format.Short) ?? "0"}");
        }

        private void AttemptToFixCorruptionByMovingHeadBackwards()
        {
            if (_tryToRecoverFromHeaderBelowBodyCorruption && BestSuggestedHeader != null)
            {
                ChainLevelInfo chainLevelInfo = LoadLevel(BestSuggestedHeader.Number);
                BlockInfo? canonicalBlock = chainLevelInfo?.MainChainBlock;
                if (canonicalBlock is not null)
                {
                    SetHeadBlock(canonicalBlock.BlockHash!);
                }
                else
                {
                    _logger.Error("Failed attempt to fix 'header < body' corruption caused by an unexpected shutdown.");
                }
            }
        }

        private void RecalculateTreeLevels()
        {
            if (_syncConfig.BeamSyncFixMode)
            {
                if (Head is null)
                {
                    throw new InvalidOperationException(
                        $"Head is null when entering {nameof(_syncConfig.BeamSyncFixMode)}");
                }
                
                BestKnownNumber = Head.Number;
                BestSuggestedBody = Head;
                BestSuggestedHeader = Head.Header;
                return;
            }

            LoadLowestInsertedBodyNumber();
            LoadLowestInsertedHeader();
            LoadBestKnown();
        }

        private void LoadLowestInsertedBodyNumber()
        {
            LowestInsertedBodyNumber =
                _blockDb.Get(LowestInsertedBodyNumberDbEntryAddress)?
                    .AsRlpValueContext().DecodeLong();
        }

        private void LoadLowestInsertedHeader()
        {
            long left = 1L;
            long right = _syncConfig.PivotNumberParsed;

            bool HasLevel(long blockNumber)
            {
                ChainLevelInfo level = LoadLevel(blockNumber);
                return level is not null;
            }

            long? lowestInsertedHeader = BinarySearchBlockNumber(left, right, HasLevel, BinarySearchDirection.Down);
            if (lowestInsertedHeader.HasValue)
            {
                ChainLevelInfo? level = LoadLevel(lowestInsertedHeader.Value);
                if (level is null)
                {
                    throw new InvalidDataException(
                        $"Missing chain level at number {lowestInsertedHeader.Value} when calling {nameof(LoadLowestInsertedHeader)}");
                }
                
                BlockInfo blockInfo = level.BlockInfos[0];
                LowestInsertedHeader = FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None);
            }
        }

        private void LoadBestKnown()
        {
            long left = (Head?.Number ?? 0) == 0
                ? Math.Max(_syncConfig.PivotNumberParsed, LowestInsertedHeader?.Number ?? 0) - 1
                : Head.Number;
            long right = Math.Max(0, left) + BestKnownSearchLimit;

            bool LevelExists(long blockNumber)
            {
                return LoadLevel(blockNumber) is not null;
            }

            bool HeaderExists(long blockNumber)
            {
                ChainLevelInfo level = LoadLevel(blockNumber);
                if (level is null)
                {
                    return false;
                }

                foreach (BlockInfo blockInfo in level.BlockInfos)
                {
                    if (FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None) is not null)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool BodyExists(long blockNumber)
            {
                ChainLevelInfo level = LoadLevel(blockNumber);
                if (level is null)
                {
                    return false;
                }

                foreach (BlockInfo blockInfo in level.BlockInfos)
                {
                    if (FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None) is not null)
                    {
                        return true;
                    }
                }

                return false;
            }

            long bestKnownNumberFound =
                BinarySearchBlockNumber(1, left, LevelExists) ?? 0;
            long bestKnownNumberAlternative =
                BinarySearchBlockNumber(left, right, LevelExists) ?? 0;

            long bestSuggestedHeaderNumber =
                BinarySearchBlockNumber(1, left, HeaderExists) ?? 0;
            long bestSuggestedHeaderNumberAlternative
                = BinarySearchBlockNumber(left, right, HeaderExists) ?? 0;

            long bestSuggestedBodyNumber
                = BinarySearchBlockNumber(1, left, BodyExists) ?? 0;
            long bestSuggestedBodyNumberAlternative
                = BinarySearchBlockNumber(left, right, BodyExists) ?? 0;

            if (_logger.IsInfo)
                _logger.Info("Numbers resolved, " +
                             $"level = Max({bestKnownNumberFound}, {bestKnownNumberAlternative}), " +
                             $"header = Max({bestSuggestedHeaderNumber}, {bestSuggestedHeaderNumberAlternative}), " +
                             $"body = Max({bestSuggestedBodyNumber}, {bestSuggestedBodyNumberAlternative})");

            BestKnownNumber = Math.Max(bestKnownNumberFound, bestKnownNumberAlternative);
            bestSuggestedHeaderNumber = Math.Max(bestSuggestedHeaderNumber, bestSuggestedHeaderNumberAlternative);
            bestSuggestedBodyNumber = Math.Max(bestSuggestedBodyNumber, bestSuggestedBodyNumberAlternative);

            if (BestKnownNumber < 0 ||
                bestSuggestedHeaderNumber < 0 ||
                bestSuggestedBodyNumber < 0 ||
                bestSuggestedHeaderNumber < bestSuggestedBodyNumber)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Detected corrupted block tree data ({bestSuggestedHeaderNumber} < {bestSuggestedBodyNumber}) (possibly due to an unexpected shutdown). Attempting to fix by moving head backwards. This may fail and you may need to resync the node.");
                if (bestSuggestedHeaderNumber < bestSuggestedBodyNumber)
                {
                    bestSuggestedBodyNumber = bestSuggestedHeaderNumber;
                    _tryToRecoverFromHeaderBelowBodyCorruption = true;
                }
                else
                {
                    throw new InvalidDataException("Invalid initial block tree state loaded - " +
                                                   $"best known: {BestKnownNumber}|" +
                                                   $"best header: {bestSuggestedHeaderNumber}|" +
                                                   $"best body: {bestSuggestedBodyNumber}|");
                }
            }

            BestSuggestedHeader = FindHeader(bestSuggestedHeaderNumber, BlockTreeLookupOptions.None);
            var bestSuggestedBodyHeader = FindHeader(bestSuggestedBodyNumber, BlockTreeLookupOptions.None);
            BestSuggestedBody = bestSuggestedBodyHeader is null ? null : FindBlock(bestSuggestedBodyHeader.Hash, BlockTreeLookupOptions.None);
        }

        private enum BinarySearchDirection
        {
            Up,
            Down
        }

        private static long? BinarySearchBlockNumber(long left, long right, Func<long, bool> isBlockFound, BinarySearchDirection direction = BinarySearchDirection.Up)
        {
            if (left > right)
            {
                return null;
            }

            long? result = null;
            while (left != right)
            {
                long index = direction == BinarySearchDirection.Up ? left + (right - left) / 2 : right - (right - left) / 2;
                if (isBlockFound(index))
                {
                    result = index;
                    if (direction == BinarySearchDirection.Up)
                    {
                        left = index + 1;
                    }
                    else
                    {
                        right = index - 1;
                    }
                }
                else
                {
                    if (direction == BinarySearchDirection.Up)
                    {
                        right = index;
                    }
                    else
                    {
                        left = index;
                    }
                }
            }

            if (isBlockFound(left))
            {
                result = direction == BinarySearchDirection.Up ? left : right;
            }

            return result;
        }

        public AddBlockResult Insert(BlockHeader header)
        {
            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (header.Hash is null)
            {
                throw new InvalidOperationException("An attempt to insert a block header without a known hash.");
            }
            
            if (header.Bloom is null)
            {
                throw new InvalidOperationException("An attempt to insert a block header without a known bloom.");
            }

            if (header.Number == 0)
            {
                throw new InvalidOperationException("Genesis block should not be inserted.");
            }

            if (header.TotalDifficulty is null)
            {
                SetTotalDifficulty(header);
            }

            // validate hash here
            // using previously received header RLPs would allows us to save 2GB allocations on a sample
            // 3M Goerli blocks fast sync
            Rlp newRlp = _headerDecoder.Encode(header);
            _headerDb.Set(header.Hash, newRlp.Bytes);

            BlockInfo blockInfo = new(header.Hash, header.TotalDifficulty ?? 0);
            ChainLevelInfo chainLevel = new(true, blockInfo);
            _chainLevelInfoRepository.PersistLevel(header.Number, chainLevel);
            _bloomStorage.Store(header.Number, header.Bloom);

            if (header.Number < (LowestInsertedHeader?.Number ?? long.MaxValue))
            {
                LowestInsertedHeader = header;
            }

            if (header.Number > BestKnownNumber)
            {
                BestKnownNumber = header.Number;
            }

            if (header.Number > (BestSuggestedHeader?.Number ?? 0))
            {
                BestSuggestedHeader = header;
            }

            return AddBlockResult.Added;
        }

        public AddBlockResult Insert(Block block)
        {
            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (block.Hash is null)
            {
                throw new InvalidOperationException("An attempt to store a block with a null hash.");
            }
            
            if (block.Number == 0)
            {
                throw new InvalidOperationException("Genesis block should not be inserted.");
            }

            // if we carry Rlp from the network message all the way here then we could solve 4GB of allocations and some processing
            // by avoiding encoding back to RLP here (allocations measured on a sample 3M blocks Goerli fast sync
            Rlp newRlp = _blockDecoder.Encode(block);
            _blockDb.Set(block.Hash, newRlp.Bytes);

            return AddBlockResult.Added;
        }

        public void Insert(IEnumerable<Block> blocks)
        {
            lock (_batchInsertLock)
            {
                // TODO: why is this commented out? why was it here in the first place? (2021-03-27)
                // try
                // {
                //   _blockDb.StartBatch();
                    foreach (Block block in blocks)
                    {
                        Insert(block);
                    }
                // }
                // finally
                // {
                //     _blockDb.CommitBatch();
                // }
            }
        }

        private AddBlockResult Suggest(Block? block, BlockHeader header, bool shouldProcess = true, bool? setAsMain = null)
        {
#if DEBUG
            /* this is just to make sure that we do not fall into this trap when creating tests */
            if (header.StateRoot is null && !header.IsGenesis)
            {
                throw new InvalidDataException($"State root is null in {header.ToString(BlockHeader.Format.Short)}");
            }
#endif

            if (header.Hash is null)
            {
                throw new InvalidOperationException("An attempt to suggest a header with a null hash.");
            }
            
            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

        
            HashSet<Keccak> invalidBlocksWithThisNumber = _invalidBlocks.Get(header.Number);
            if (invalidBlocksWithThisNumber?.Contains(header.Hash) ?? false)
            {
                return AddBlockResult.InvalidBlock;
            }

            bool isKnown = IsKnownBlock(header.Number, header.Hash);
            if (isKnown && (BestSuggestedHeader?.Number ?? 0) >= header.Number)
            {
                if (_logger.IsTrace) _logger.Trace($"Block {header.Hash} already known.");
                return AddBlockResult.AlreadyKnown;
            }

            if (!header.IsGenesis && !IsKnownBlock(header.Number - 1, header.ParentHash!))
            {
                if (_logger.IsTrace) _logger.Trace($"Could not find parent ({header.ParentHash}) of block {header.Hash}");
                return AddBlockResult.UnknownParent;
            }

            SetTotalDifficulty(header);

            if (block is not null && !isKnown)
            {
                if (block.Hash is null)
                {
                    throw new InvalidOperationException("An attempt to suggest block with a null hash.");
                }
                
                Rlp newRlp = _blockDecoder.Encode(block);
                _blockDb.Set(block.Hash, newRlp.Bytes);
            }

            if (!isKnown)
            {
                Rlp newRlp = _headerDecoder.Encode(header);
                _headerDb.Set(header.Hash, newRlp.Bytes);

                BlockInfo blockInfo = new(header.Hash, header.TotalDifficulty ?? 0);
                UpdateOrCreateLevel(header.Number, blockInfo, setAsMain is null ? !shouldProcess : setAsMain.Value);
                NewSuggestedBlock?.Invoke(this, new BlockEventArgs(block));
            }

            if (header.IsGenesis || header.TotalDifficulty > (BestSuggestedHeader?.TotalDifficulty ?? 0))
            {
                if (header.IsGenesis)
                {
                    Genesis = header;
                }

                BestSuggestedHeader = header;
                if (block is not null && shouldProcess)
                {
                    BestSuggestedBody = block;
                    NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));
                }
            }

            return AddBlockResult.Added;
        }

        public AddBlockResult SuggestHeader(BlockHeader header)
        {
            return Suggest(null, header);
        }

        public AddBlockResult SuggestBlock(Block block, bool shouldProcess = true, bool? setAsMain = null)
        {
            if (Genesis is null && !block.IsGenesis)
            {
                throw new InvalidOperationException("Block tree should be initialized with genesis before suggesting other blocks.");
            }

            return Suggest(block, block.Header, shouldProcess, setAsMain);
        }

        public BlockHeader? FindHeader(long number, BlockTreeLookupOptions options)
        {
            Keccak blockHash = GetBlockHashOnMainOrBestDifficultyHash(number);
            return blockHash is null ? null : FindHeader(blockHash, options);
        }

        public Keccak? FindBlockHash(long blockNumber) => GetBlockHashOnMainOrBestDifficultyHash(blockNumber);

        public BlockHeader? FindHeader(Keccak? blockHash, BlockTreeLookupOptions options)
        {
            if (blockHash is null || blockHash == Keccak.Zero)
            {
                // TODO: would be great to check why this is still needed (maybe it is something archaic)
                return null;
            }

            BlockHeader? header = _headerDb.Get(blockHash, _headerDecoder, _headerCache, false);
            if (header is null)
            {
                return null;
            }

            header.Hash ??= blockHash;

            bool totalDifficultyNeeded = (options & BlockTreeLookupOptions.TotalDifficultyNotNeeded) == BlockTreeLookupOptions.None;
            bool requiresCanonical = (options & BlockTreeLookupOptions.RequireCanonical) == BlockTreeLookupOptions.RequireCanonical;

            if ((totalDifficultyNeeded && header.TotalDifficulty is null) || requiresCanonical)
            {
                (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(header.Number, header.Hash, true);
                if (level is null || blockInfo is null)
                {
                    // TODO: this is here because storing block data is not transactional
                    // TODO: would be great to remove it, he?
                    if (_logger.IsTrace) _logger.Trace($"Entering missing block info in {nameof(FindHeader)} scope when head is {Head?.ToString(Block.Format.Short)}");
                    SetTotalDifficulty(header);
                    blockInfo = new BlockInfo(header.Hash, header.TotalDifficulty!.Value);
                    level = UpdateOrCreateLevel(header.Number, blockInfo);
                }
                else
                {
                    header.TotalDifficulty = blockInfo.TotalDifficulty;
                }

                if (requiresCanonical)
                {
                    bool isMain = level.MainChainBlock?.BlockHash?.Equals(blockHash) == true;
                    header = isMain ? header : null;
                }
            }

            if (header is not null && ShouldCache(header.Number))
            {
                _headerCache.Set(blockHash, header);
            }

            return header;
        }

        /// <returns>
        /// If level has a block on the main chain then returns the block info,otherwise <value>null</value>
        /// </returns>
        public BlockInfo? FindCanonicalBlockInfo(long blockNumber)
        {
            ChainLevelInfo level = LoadLevel(blockNumber);
            if (level is null)
            {
                return null;
            }

            if (level.HasBlockOnMainChain)
            {
                BlockInfo blockInfo = level.BlockInfos[0];
                blockInfo.BlockNumber = blockNumber;
                return blockInfo;
            }

            return null;
        }

        public Keccak? FindHash(long number)
        {
            return GetBlockHashOnMainOrBestDifficultyHash(number);
        }

        public BlockHeader[] FindHeaders(Keccak? blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            if (numberOfBlocks == 0)
            {
                return Array.Empty<BlockHeader>();
            }

            if (blockHash is null)
            {
                return new BlockHeader[numberOfBlocks];
            }

            BlockHeader startHeader = FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (startHeader is null)
            {
                return new BlockHeader[numberOfBlocks];
            }

            if (numberOfBlocks == 1)
            {
                return new[] {startHeader};
            }

            if (skip == 0)
            {
                /* if we do not skip and we have the last block then we can assume that all the blocks are there
                   and we can use the fact that we can use parent hash and that searching by hash is much faster
                   as it does not require the step of resolving number -> hash */
                BlockHeader endHeader = FindHeader(startHeader.Number + numberOfBlocks - 1, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (endHeader is not null)
                {
                    return FindHeadersReversedFull(endHeader, numberOfBlocks);
                }
            }

            BlockHeader[] result = new BlockHeader[numberOfBlocks];
            BlockHeader current = startHeader;
            int directionMultiplier = reverse ? -1 : 1;
            int responseIndex = 0;
            do
            {
                result[responseIndex] = current;
                responseIndex++;
                long nextNumber = startHeader.Number + directionMultiplier * (responseIndex * skip + responseIndex);
                if (nextNumber < 0)
                {
                    break;
                }

                current = FindHeader(nextNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            } while (current is not null && responseIndex < numberOfBlocks);

            return result;
        }

        private BlockHeader[] FindHeadersReversedFull(BlockHeader startHeader, int numberOfBlocks)
        {
            if (startHeader is null) throw new ArgumentNullException(nameof(startHeader));
            if (numberOfBlocks == 1)
            {
                return new[] {startHeader};
            }

            BlockHeader[] result = new BlockHeader[numberOfBlocks];

            BlockHeader current = startHeader;
            int responseIndex = numberOfBlocks - 1;
            do
            {
                result[responseIndex] = current;
                responseIndex--;
                if (responseIndex < 0)
                {
                    break;
                }

                current = this.FindParentHeader(current, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            } while (current is not null && responseIndex < numberOfBlocks);

            return result;
        }

        public BlockHeader? FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant, long maxSearchDepth)
        {
            if (firstDescendant.Number > secondDescendant.Number)
            {
                firstDescendant = GetAncestorAtNumber(firstDescendant, secondDescendant.Number);
            }
            else if (secondDescendant.Number > firstDescendant.Number)
            {
                secondDescendant = GetAncestorAtNumber(secondDescendant, firstDescendant.Number);
            }

            long currentSearchDepth = 0;
            while (
                firstDescendant is not null 
                && secondDescendant is not null 
                && firstDescendant.Hash != secondDescendant.Hash)
            {
                if (currentSearchDepth++ >= maxSearchDepth) return null;
                firstDescendant = this.FindParentHeader(firstDescendant, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                secondDescendant = this.FindParentHeader(secondDescendant, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }

            return firstDescendant;
        }

        private BlockHeader? GetAncestorAtNumber(BlockHeader header, long number)
        {
            BlockHeader? result = header;
            while (result is not null && result.Number < number)
            {
                result = this.FindParentHeader(result, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }

            return header;
        }

        private Keccak? GetBlockHashOnMainOrBestDifficultyHash(long blockNumber)
        {
            if (blockNumber < 0)
            {
                throw new ArgumentException($"{nameof(blockNumber)} must be greater or equal zero and is {blockNumber}",
                    nameof(blockNumber));
            }

            ChainLevelInfo level = LoadLevel(blockNumber);
            if (level is null)
            {
                return null;
            }

            if (level.HasBlockOnMainChain)
            {
                return level.BlockInfos[0].BlockHash;
            }

            UInt256 bestDifficultySoFar = UInt256.Zero;
            Keccak bestHash = null;
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                BlockInfo current = level.BlockInfos[i];
                if (level.BlockInfos[i].TotalDifficulty > bestDifficultySoFar)
                {
                    bestDifficultySoFar = current.TotalDifficulty;
                    bestHash = current.BlockHash;
                }
            }

            return bestHash;
        }

        public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options)
        {
            Keccak hash = GetBlockHashOnMainOrBestDifficultyHash(blockNumber);
            return FindBlock(hash, options);
        }

        public void DeleteInvalidBlock(Block invalidBlock)
        {
            if (invalidBlock.Hash is null)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(DeleteInvalidBlock)} call has been made for a block without a null hash.");
                return;
            }
            
            if (_logger.IsDebug) _logger.Debug($"Deleting invalid block {invalidBlock.ToString(Block.Format.FullHashAndNumber)}");

            HashSet<Keccak>? invalidBlocksWithThisNumber = _invalidBlocks.Get(invalidBlock.Number) ?? new HashSet<Keccak>();
            invalidBlocksWithThisNumber.Add(invalidBlock.Hash);
            _invalidBlocks.Set(invalidBlock.Number, invalidBlocksWithThisNumber);

            BestSuggestedHeader = Head?.Header;
            BestSuggestedBody = Head;

            BlockAcceptingNewBlocks();

            try
            {
                DeleteBlocks(invalidBlock.Hash!);
            }
            finally
            {
                ReleaseAcceptingNewBlocks();
            }
        }

        private void DeleteBlocks(Keccak deletePointer)
        {
            BlockHeader? deleteHeader = FindHeader(deletePointer, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (deleteHeader is null)
            {
                if (_logger.IsWarn) _logger.Warn(
                    $"Cannot delete invalid block {deletePointer} - block has not been added to the database or has already been deleted.");
                return;
            }

            if (deleteHeader.Hash is null)
            {
                if (_logger.IsWarn) _logger.Warn(
                    $"Cannot delete invalid block {deletePointer} - black has a null hash.");
                return;
            }
            
            long currentNumber = deleteHeader.Number;
            Keccak currentHash = deleteHeader.Hash;
            Keccak? nextHash = null;
            ChainLevelInfo? nextLevel = null;

            using BatchWrite batch = _chainLevelInfoRepository.StartBatch();
            while (true)
            {
                ChainLevelInfo? currentLevel = nextLevel ?? LoadLevel(currentNumber);
                nextLevel = LoadLevel(currentNumber + 1);

                bool shouldRemoveLevel = false;
                if (currentLevel is not null) // preparing update of the level (removal of the invalid branch block)
                {
                    if (currentLevel.BlockInfos.Length == 1)
                    {
                        shouldRemoveLevel = true;
                    }
                    else
                    {
                        currentLevel.BlockInfos = currentLevel.BlockInfos.Where(bi => bi.BlockHash != currentHash).ToArray();
                    }
                }

                // just finding what the next descendant will be
                if (nextLevel is not null)
                {
                    nextHash = FindChild(nextLevel, currentHash);
                }

                UpdateDeletePointer(nextHash);

                if (shouldRemoveLevel)
                {
                    BestKnownNumber = Math.Min(BestKnownNumber, currentNumber - 1);
                    _chainLevelInfoRepository.Delete(currentNumber, batch);
                }
                else if(currentLevel is not null)
                {
                    _chainLevelInfoRepository.PersistLevel(currentNumber, currentLevel, batch);
                }
                
                if (_logger.IsInfo) _logger.Info($"Deleting invalid block {currentHash} at level {currentNumber}");
                _blockCache.Delete(currentHash);
                _blockDb.Delete(currentHash);
                _headerCache.Delete(currentHash);
                _headerDb.Delete(currentHash);

                if (nextHash is null)
                {
                    break;
                }

                currentNumber++;
                currentHash = nextHash;
                nextHash = null;
            }
        }

        private Keccak? FindChild(ChainLevelInfo level, Keccak parentHash)
        {
            Keccak childHash = null;
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                Keccak potentialChildHash = level.BlockInfos[i].BlockHash;
                BlockHeader? potentialChild = FindHeader(potentialChildHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (potentialChild is null)
                {
                    if(_logger.IsWarn) _logger.Warn(
                        $"Block with hash {potentialChildHash} has been found on chain level but its header is missing from the DB.");
                    return null;
                }
                
                if (potentialChild.ParentHash == parentHash)
                {
                    childHash = potentialChildHash;
                    break;
                }
            }

            return childHash;
        }

        public bool IsMainChain(BlockHeader blockHeader)
        {
            ChainLevelInfo? chainLevelInfo = LoadLevel(blockHeader.Number);
            bool isMain = chainLevelInfo is not null
                          && chainLevelInfo.MainChainBlock?.BlockHash?.Equals(blockHeader.Hash) == true;
            return isMain;
        }

        public bool IsMainChain(Keccak blockHash)
        {
            BlockHeader? header = FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header is null)
            {
                throw new InvalidOperationException($"Not able to retrieve block number for an unknown block {blockHash}");
            }

            return IsMainChain(header);
        }

        public BlockHeader? FindBestSuggestedHeader() => BestSuggestedHeader;

        public bool WasProcessed(long number, Keccak blockHash)
        {
            ChainLevelInfo? levelInfo = LoadLevel(number);
            if (levelInfo is null)
            {
                throw new InvalidOperationException($"Not able to find block {blockHash} from an unknown level {number}");
            }
            
            int? index = FindIndex(blockHash, levelInfo);
            if (index is null)
            {
                throw new InvalidOperationException($"Not able to find block {blockHash} index on the chain level");
            }

            return levelInfo.BlockInfos[index.Value].WasProcessed;
        }
        
        public void UpdateMainChain(Block[] blocks, bool wereProcessed, bool forceUpdateHeadBlock = false)
        {
            if (blocks.Length == 0)
            {
                return;
            }

            bool ascendingOrder = true;
            if (blocks.Length > 1)
            {
                if (blocks[^1].Number < blocks[0].Number)
                {
                    ascendingOrder = false;
                }
            }

#if DEBUG
            for (int i = 0; i < blocks.Length; i++)
            {
                if (i != 0)
                {
                    if (ascendingOrder && blocks[i].Number != blocks[i - 1].Number + 1)
                    {
                        throw new InvalidOperationException("Update main chain invoked with gaps");
                    }

                    if (!ascendingOrder && blocks[i - 1].Number != blocks[i].Number + 1)
                    {
                        throw new InvalidOperationException("Update main chain invoked with gaps");
                    }
                }
            }
#endif

            long lastNumber = ascendingOrder ? blocks[^1].Number : blocks[0].Number;
            long previousHeadNumber = Head?.Number ?? 0L;
            using BatchWrite batch = _chainLevelInfoRepository.StartBatch();
            if (previousHeadNumber > lastNumber)
            {
                for (long i = 0; i < previousHeadNumber - lastNumber; i++)
                {
                    long levelNumber = previousHeadNumber - i;

                    ChainLevelInfo? level = LoadLevel(levelNumber);
                    if (level is not null)
                    {
                        level.HasBlockOnMainChain = false;
                        _chainLevelInfoRepository.PersistLevel(levelNumber, level, batch);
                    }
                }
            }

            for (int i = 0; i < blocks.Length; i++)
            {
                Block block = blocks[i];
                if (ShouldCache(block.Number))
                {
                    _blockCache.Set(block.Hash, blocks[i]);
                    _headerCache.Set(block.Hash, block.Header);
                }

                // we only force update head block for last block in processed blocks
                bool lastProcessedBlock = i == blocks.Length - 1;
                MoveToMain(blocks[i], batch, wereProcessed, forceUpdateHeadBlock && lastProcessedBlock);
            }
        }

        
        /// <summary>
        /// Moves block to main chain.
        /// </summary>
        /// <param name="block">Block to move</param>
        /// <param name="batch">Db batch</param>
        /// <param name="wasProcessed">Was block processed (full sync), or not (fast sync)</param>
        /// <param name="forceUpdateHeadBlock">Force updating <see cref="Head"/> to this block, even when <see cref="Block.TotalDifficulty"/> is not higher than previous head.</param>
        /// <exception cref="InvalidOperationException">Invalid block</exception>
        [Todo(Improve.MissingFunctionality, "Recalculate bloom storage on reorg.")]
        private void MoveToMain(Block block, BatchWrite batch, bool wasProcessed, bool forceUpdateHeadBlock)
        {
            if (block.Hash is null)
            {
                throw new InvalidOperationException("An attempt to move to main a block with hash not set.");
            }
            
            if (block.Bloom is null)
            {
                throw new InvalidOperationException("An attempt to move to main a block with bloom not set.");
            }
            
            ChainLevelInfo? level = LoadLevel(block.Number);
            int? index = level is null ? null : FindIndex(block.Hash, level);
            if (index is null)
            {
                throw new InvalidOperationException($"Cannot move unknown block {block.ToString(Block.Format.FullHashAndNumber)} to main");
            }

            Keccak hashOfThePreviousMainBlock = level.MainChainBlock?.BlockHash;

            BlockInfo info = level.BlockInfos[index.Value];
            info.WasProcessed = wasProcessed;
            if (index.Value != 0)
            {
                (level.BlockInfos[index.Value], level.BlockInfos[0]) = (level.BlockInfos[0], level.BlockInfos[index.Value]);
            }

            level.HasBlockOnMainChain = true;
            _chainLevelInfoRepository.PersistLevel(block.Number, level, batch);
            _bloomStorage.Store(block.Number, block.Bloom);

            Block previous = hashOfThePreviousMainBlock is not null && hashOfThePreviousMainBlock != block.Hash
                ? FindBlock(hashOfThePreviousMainBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded)
                : null;

            BlockAddedToMain?.Invoke(this, new BlockReplacementEventArgs(block, previous));

            if (forceUpdateHeadBlock || block.IsGenesis || block.TotalDifficulty > (Head?.TotalDifficulty ?? 0))
            {
                if (block.Number == 0)
                {
                    Genesis = block.Header;
                }

                if (block.TotalDifficulty is null)
                {
                    throw new InvalidOperationException("Head block with null total difficulty");
                }

                if (wasProcessed)
                {
                    UpdateHeadBlock(block);
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Block {block.ToString(Block.Format.Short)} added to main chain");
        }

        private void LoadStartBlock()
        {
            Block? startBlock = null;
            byte[] persistedNumberData = _blockInfoDb.Get(StateHeadHashDbEntryAddress);
            long? persistedNumber = persistedNumberData is null ? (long?) null : new RlpStream(persistedNumberData).DecodeLong();
            if (persistedNumber is not null)
            {
                startBlock = FindBlock(persistedNumber.Value, BlockTreeLookupOptions.None);
                _logger.Warn($"Start block loaded from reorg boundary - {persistedNumber} - {startBlock?.ToString(Block.Format.Short)}");
            }
            else
            {
                byte[] data = _blockInfoDb.Get(HeadAddressInDb);
                if (data is not null)
                {
                    startBlock = FindBlock(new Keccak(data), BlockTreeLookupOptions.None);
                    _logger.Warn($"Start block loaded from HEAD - {startBlock?.ToString(Block.Format.Short)}");
                }
            }

            if (startBlock is not null)
            {
                if (startBlock.Hash is null)
                {
                    throw new InvalidDataException("The start block hash is null.");
                }
                
                SetHeadBlock(startBlock.Hash);
            }
        }

        private void SetHeadBlock(Keccak headHash)
        {
            Block? headBlock = FindBlock(headHash, BlockTreeLookupOptions.None);
            if (headBlock is null)
            {
                throw new InvalidOperationException("An attempt to set a head block that has not been stored in the DB.");
            }

            ChainLevelInfo? level = LoadLevel(headBlock.Number);
            int? index = level is null ? null : FindIndex(headHash, level);
            if (!index.HasValue)
            {
                throw new InvalidDataException("Head block data missing from chain info");
            }

            headBlock.Header.TotalDifficulty = level.BlockInfos[index.Value].TotalDifficulty;
            Head = headBlock;
        }

        public bool IsKnownBlock(long number, Keccak blockHash)
        {
            if (number > BestKnownNumber)
            {
                return false;
            }

            // IsKnownBlock will be mainly called when new blocks are incoming
            // and these are very likely to be all at the head of the chain
            if (blockHash == Head?.Hash)
            {
                return true;
            }

            if (_headerCache.Get(blockHash) is not null)
            {
                return true;
            }

            ChainLevelInfo level = LoadLevel(number);
            return level is not null && FindIndex(blockHash, level).HasValue;
        }

        private void UpdateDeletePointer(Keccak? hash)
        {
            if (hash is null)
            {
                _blockInfoDb.Delete(DeletePointerAddressInDb);
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Deleting an invalid block or its descendant {hash}");
                _blockInfoDb.Set(DeletePointerAddressInDb, hash.Bytes);
            }
        }

        public void UpdateHeadBlock(Keccak blockHash)
        {
            if(_logger.IsError) _logger.Error($"Block tree override detected - updating head block to {blockHash}.");
            _blockInfoDb.Set(HeadAddressInDb, blockHash.Bytes);
        }

        private void UpdateHeadBlock(Block block)
        {
            if (block.Hash is null)
            {
                throw new InvalidOperationException("Block suggested as the new head block has no hash set.");
            }
            
            if (block.IsGenesis)
            {
                Genesis = block.Header;
            }

            Head = block;
            _blockInfoDb.Set(HeadAddressInDb, block.Hash.Bytes);
            NewHeadBlock?.Invoke(this, new BlockEventArgs(block));
        }

        private ChainLevelInfo UpdateOrCreateLevel(long number, BlockInfo blockInfo, bool setAsMain = false)
        {
            using (var batch = _chainLevelInfoRepository.StartBatch())
            {
                ChainLevelInfo level = LoadLevel(number, false);

                if (level is not null)
                {
                    BlockInfo[] blockInfos = level.BlockInfos;
                    Array.Resize(ref blockInfos, blockInfos.Length + 1);
                    if (setAsMain)
                    {
                        blockInfos[^1] = blockInfos[0];
                        blockInfos[0] = blockInfo;
                    }
                    else
                    {
                        blockInfos[^1] = blockInfo;
                    }

                    level.BlockInfos = blockInfos;
                }
                else
                {
                    if (number > BestKnownNumber)
                    {
                        BestKnownNumber = number;
                    }

                    level = new ChainLevelInfo(false, new[] {blockInfo});
                }

                if (setAsMain)
                {
                    level.HasBlockOnMainChain = true;
                }

                _chainLevelInfoRepository.PersistLevel(number, level, batch);

                return level;
            }
        }

        private (BlockInfo Info, ChainLevelInfo Level) LoadInfo(long number, Keccak blockHash, bool forceLoad)
        {
            ChainLevelInfo chainLevelInfo = LoadLevel(number, forceLoad);
            if (chainLevelInfo is null)
            {
                return (null, null);
            }

            int? index = FindIndex(blockHash, chainLevelInfo);
            return index.HasValue ? (chainLevelInfo.BlockInfos[index.Value], chainLevelInfo) : (null, chainLevelInfo);
        }

        private static int? FindIndex(Keccak blockHash, ChainLevelInfo level)
        {
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                Keccak hashAtIndex = level.BlockInfos[i].BlockHash;
                if (hashAtIndex.Equals(blockHash))
                {
                    return i;
                }
            }

            return null;
        }

        private ChainLevelInfo? LoadLevel(long number, bool forceLoad = true)
        {
            if (number > BestKnownNumber && !forceLoad)
            {
                return null;
            }

            return _chainLevelInfoRepository.LoadLevel(number);
        }

        /// <summary>
        /// To make cache useful even when we handle sync requests
        /// </summary>
        /// <param name="number"></param>
        /// <returns></returns>
        private bool ShouldCache(long number)
        {
            return number == 0L || Head is null || number > Head.Number - CacheSize && number <= Head.Number + 1;
        }

        public ChainLevelInfo? FindLevel(long number)
        {
            return _chainLevelInfoRepository.LoadLevel(number);
        }

        public Keccak? HeadHash => Head?.Hash;
        public Keccak? GenesisHash => Genesis?.Hash;
        public Keccak? PendingHash => Head?.Hash;

        public Block? FindBlock(Keccak? blockHash, BlockTreeLookupOptions options)
        {
            if (blockHash is null || blockHash == Keccak.Zero)
            {
                return null;
            }

            Block block = _blockDb.Get(blockHash, _blockDecoder, _blockCache, false);
            if (block is null)
            {
                return null;
            }

            bool totalDifficultyNeeded = (options & BlockTreeLookupOptions.TotalDifficultyNotNeeded) == BlockTreeLookupOptions.None;
            bool requiresCanonical = (options & BlockTreeLookupOptions.RequireCanonical) == BlockTreeLookupOptions.RequireCanonical;

            if ((totalDifficultyNeeded && block.TotalDifficulty is null) || requiresCanonical)
            {
                (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(block.Number, block.Hash, true);
                if (level is null || blockInfo is null)
                {
                    // TODO: this is here because storing block data is not transactional
                    // TODO: would be great to remove it, he?
                    if (_logger.IsTrace) _logger.Trace($"Entering missing block info in {nameof(FindBlock)} scope when head is {Head?.ToString(Block.Format.Short)}");
                    SetTotalDifficulty(block.Header);
                    blockInfo = new BlockInfo(block.Hash, block.TotalDifficulty!.Value);
                    level = UpdateOrCreateLevel(block.Number, blockInfo);
                }
                else
                {
                    block.Header.TotalDifficulty = blockInfo.TotalDifficulty;
                }

                if (requiresCanonical)
                {
                    bool isMain = level.MainChainBlock?.BlockHash.Equals(blockHash) == true;
                    block = isMain ? block : null;
                }
            }

            if (block is not null && ShouldCache(block.Number))
            {
                _blockCache.Set(blockHash, block);
                _headerCache.Set(blockHash, block.Header);
            }

            return block;
        }

        private void SetTotalDifficulty(BlockHeader header)
        {
            BlockHeader GetParentHeader(BlockHeader current) =>
                // TotalDifficultyNotNeeded is by design here,
                // if it was absent this would result in recursion, as if parent doesn't already have total difficulty 
                // then it would call back to SetTotalDifficulty for it
                // This was original code but it could result in stack overflow
                this.FindParentHeader(current, BlockTreeLookupOptions.TotalDifficultyNotNeeded)
                ?? throw new InvalidOperationException($"An orphaned block on the chain {current}");

            void SetTotalDifficultyDeep(BlockHeader current)
            {
                Stack<BlockHeader> stack = new();
                while (current.TotalDifficulty is null)
                {
                    (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(current.Number, current.Hash, true);
                    if (level is null || blockInfo is null)
                    {
                        stack.Push(current);
                        if (_logger.IsTrace) _logger.Trace($"Calculating total difficulty for {current.ToString(BlockHeader.Format.Short)}");
                        current = GetParentHeader(current);
                    }
                    else
                    {
                        current.TotalDifficulty = blockInfo.TotalDifficulty;
                    }
                }

                while (stack.TryPop(out BlockHeader child))
                {
                    child.TotalDifficulty = current.TotalDifficulty + child.Difficulty;
                    BlockInfo blockInfo = new(child.Hash, child.TotalDifficulty.Value);
                    UpdateOrCreateLevel(child.Number, blockInfo);
                    if (_logger.IsTrace) _logger.Trace($"Calculated total difficulty for {child} is {child.TotalDifficulty}");
                    current = child;
                }
            }

            if (header.TotalDifficulty is not null)
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Calculating total difficulty for {header.ToString(BlockHeader.Format.Short)}");

            if (header.IsGenesis)
            {
                header.TotalDifficulty = header.Difficulty;
            }
            else
            {
                BlockHeader parentHeader = GetParentHeader(header);

                if (parentHeader.TotalDifficulty is null)
                {
                    SetTotalDifficultyDeep(parentHeader);
                }

                header.TotalDifficulty = parentHeader.TotalDifficulty + header.Difficulty;
            }

            if (_logger.IsTrace) _logger.Trace($"Calculated total difficulty for {header} is {header.TotalDifficulty}");
        }

        public event EventHandler<BlockReplacementEventArgs>? BlockAddedToMain;

        public event EventHandler<BlockEventArgs>? NewBestSuggestedBlock;
        
        public event EventHandler<BlockEventArgs>? NewSuggestedBlock;

        public event EventHandler<BlockEventArgs>? NewHeadBlock;

        /// <summary>
        /// Can delete a slice of the chain (usually invoked when the chain is corrupted in the DB).
        /// This will only allow to delete a slice starting somewhere before the head of the chain
        /// and ending somewhere after the head (in case there are some hanging levels later).
        /// </summary>
        /// <param name="startNumber">Start level of the slice to delete</param>
        /// <param name="endNumber">End level of the slice to delete</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="startNumber"/> ot <paramref name="endNumber"/> do not satisfy the slice position rules</exception>
        public int DeleteChainSlice(in long startNumber, long? endNumber)
        {
            int deleted = 0;
            endNumber ??= BestKnownNumber;

            if (endNumber - startNumber < 0)
            {
                throw new ArgumentException("Start number must be equal or greater end number.", nameof(startNumber));
            }

            if (endNumber - startNumber > 50000)
            {
                throw new ArgumentException($"Cannot delete that many blocks at once (start: {startNumber}, end {endNumber}).", nameof(startNumber));
            }

            if (startNumber < 1)
            {
                throw new ArgumentException("Start number must be strictly greater than 0", nameof(startNumber));
            }

            Block? newHeadBlock = null;

            // we are running these checks before all the deletes
            if (Head.Number >= startNumber)
            {
                // greater than zero so will not fail
                ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(startNumber - 1);
                if (chainLevelInfo is null)
                {
                    throw new InvalidDataException($"Chain level {startNumber - 1} does not exist when {startNumber} level exists.");
                }

                // there may be no canonical block marked on this level - then we just hack to genesis
                Keccak? newHeadHash = chainLevelInfo.HasBlockOnMainChain ? chainLevelInfo.BlockInfos[0].BlockHash : Genesis?.Hash;
                newHeadBlock = newHeadHash is null ? null : FindBlock(newHeadHash, BlockTreeLookupOptions.None);
            }

            using (_chainLevelInfoRepository.StartBatch())
            {
                for (long i = endNumber.Value; i >= startNumber; i--)
                {
                    ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(i);
                    if (chainLevelInfo is null)
                    {
                        continue;
                    }

                    _chainLevelInfoRepository.Delete(i);
                    deleted++;

                    foreach (BlockInfo blockInfo in chainLevelInfo.BlockInfos)
                    {
                        Keccak blockHash = blockInfo.BlockHash;
                        _blockInfoDb.Delete(blockHash);
                        _blockDb.Delete(blockHash);
                        _headerDb.Delete(blockHash);
                    }
                }
            }

            if (newHeadBlock is not null)
            {
                UpdateHeadBlock(newHeadBlock);
            }

            return deleted;
        }

        internal void BlockAcceptingNewBlocks()
        {
            Interlocked.Increment(ref _canAcceptNewBlocksCounter);
        }

        internal void ReleaseAcceptingNewBlocks()
        {
            Interlocked.Decrement(ref _canAcceptNewBlocksCounter);
        }

        public void SavePruningReorganizationBoundary(long blockNumber)
        {
            _blockInfoDb.Set(StateHeadHashDbEntryAddress, Rlp.Encode(blockNumber).Bytes);
        }
    }
}
