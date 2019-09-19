/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;

namespace Nethermind.Blockchain
{
    [Todo(Improve.Refactor, "After the fast sync work there are some duplicated code parts for the 'by header' and 'by block' approaches.")]
    public class BlockTree : IBlockTree
    {
        private const int CacheSize = 64;
        private readonly LruCache<Keccak, Block> _blockCache = new LruCache<Keccak, Block>(CacheSize);
        private readonly LruCache<Keccak, BlockHeader> _headerCache = new LruCache<Keccak, BlockHeader>(CacheSize);
        
        private const int BestKnownSearchLimit = 256_000_000;
        public const int DbLoadBatchSize = 4000;

        private long _currentDbLoadBatchEnd;

        private readonly object _batchInsertLock = new object();

        private readonly IDb _blockDb;
        private readonly IDb _headerDb;
        private readonly IDb _blockInfoDb;

        private ConcurrentDictionary<long, HashSet<Keccak>> _invalidBlocks = new ConcurrentDictionary<long, HashSet<Keccak>>();
        private readonly BlockDecoder _blockDecoder = new BlockDecoder();
        private readonly HeaderDecoder _headerDecoder = new HeaderDecoder();
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly ITxPool _txPool;
        private readonly ISyncConfig _syncConfig;
        private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
        
        internal static Keccak DeletePointerAddressInDb = new Keccak(new BitArray(32 * 8, true).ToBytes());
        internal static Keccak HeadAddressInDb = Keccak.Zero;

        public BlockHeader Genesis { get; private set; }
        public BlockHeader Head { get; private set; }
        public BlockHeader BestSuggestedHeader { get; private set; }
        public Block BestSuggestedBody { get; private set; }
        public BlockHeader LowestInsertedHeader { get; private set; }
        public Block LowestInsertedBody { get; private set; }
        public long BestKnownNumber { get; private set; }
        public int ChainId => _specProvider.ChainId;

        public bool CanAcceptNewBlocks { get; private set; } = true; // no need to sync it at the moment

        public BlockTree(
            IDb blockDb,
            IDb headerDb,
            IDb blockInfoDb,
            IChainLevelInfoRepository chainLevelInfoRepository,
            ISpecProvider specProvider,
            ITxPool txPool,
            ILogManager logManager)
            : this(blockDb, headerDb, blockInfoDb, chainLevelInfoRepository, specProvider, txPool, new SyncConfig(), logManager)
        {
        }

        public BlockTree(
            IDb blockDb,
            IDb headerDb,
            IDb blockInfoDb,
            IChainLevelInfoRepository chainLevelInfoRepository,
            ISpecProvider specProvider,
            ITxPool txPool,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockDb = blockDb ?? throw new ArgumentNullException(nameof(blockDb));
            _headerDb = headerDb ?? throw new ArgumentNullException(nameof(headerDb));
            _blockInfoDb = blockInfoDb ?? throw new ArgumentNullException(nameof(blockInfoDb));
            _specProvider = specProvider;
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new ArgumentNullException(nameof(chainLevelInfoRepository));

            ChainLevelInfo genesisLevel = LoadLevel(0, true);
            if (genesisLevel != null)
            {
                if (genesisLevel.BlockInfos.Length != 1)
                {
                    // just for corrupted test bases
                    genesisLevel.BlockInfos = new[] {genesisLevel.BlockInfos[0]};
                    _chainLevelInfoRepository.PersistLevel(0, genesisLevel);
                    //throw new InvalidOperationException($"Genesis level in DB has {genesisLevel.BlockInfos.Length} blocks");
                }

                LoadLowestInsertedHeader();
                LoadLowestInsertedBody();
                LoadBestKnown();

                if (genesisLevel.BlockInfos[0].WasProcessed)
                {
                    BlockHeader genesisHeader = FindHeader(genesisLevel.BlockInfos[0].BlockHash, BlockTreeLookupOptions.None);
                    Genesis = genesisHeader;
                    LoadHeadBlockAtStart();
                }
            }

            if (_logger.IsInfo) _logger.Info($"Block tree initialized, last processed is {Head?.ToString(BlockHeader.Format.Short) ?? "0"}, best queued is {BestSuggestedHeader?.Number.ToString() ?? "0"}, best known is {BestKnownNumber}, lowest inserted header {LowestInsertedHeader?.Number}, body {LowestInsertedBody?.Number}");
        }

        private void LoadBestKnown()
        {
            long headNumber = Head?.Number ?? -1;
            long left = Math.Max(LowestInsertedHeader?.Number ?? 0, headNumber);
            long right = headNumber + BestKnownSearchLimit;

            while (left != right)
            {
                long index = left + (right - left) / 2;
                ChainLevelInfo level = LoadLevel(index, true);
                if (level == null)
                {
                    right = index;
                }
                else
                {
                    left = index + 1;
                }
            }

            long result = left - 1;

            BestKnownNumber = result;

            if (BestKnownNumber < 0)
            {
                throw new InvalidOperationException($"Best known is {BestKnownNumber}");
            }
        }

        private void LoadLowestInsertedHeader()
        {
            long left = 0L;
            long right = LongConverter.FromString(_syncConfig.PivotNumber ?? "0x0");

            ChainLevelInfo lowestInsertedLevel = null;
            while (left != right)
            {
                if (_logger.IsTrace) _logger.Trace($"Finding lowest inserted header - L {left} | R {right}");
                long index = left + (right - left) / 2 + 1;
                ChainLevelInfo level = LoadLevel(index, true);
                if (level == null)
                {
                    left = index;
                }
                else
                {
                    lowestInsertedLevel = level;
                    right = index - 1L;
                }
            }

            if (lowestInsertedLevel == null)
            {
                if (_logger.IsTrace) _logger.Trace($"Lowest inserted header is null - L {left} | R {right}");
                LowestInsertedHeader = null;
            }
            else
            {
                BlockInfo blockInfo = lowestInsertedLevel.BlockInfos[0];
                LowestInsertedHeader = FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None);
                if (_logger.IsDebug) _logger.Debug($"Lowest inserted header is {LowestInsertedHeader?.ToString(BlockHeader.Format.Short)} {right} - L {left} | R {right}");
            }
        }

        private void LoadLowestInsertedBody()
        {
            long left = 0L;
            long right = LongConverter.FromString(_syncConfig.PivotNumber ?? "0x0");

            Block lowestInsertedBlock = null;
            while (left != right)
            {
                if (_logger.IsDebug) _logger.Debug($"Finding lowest inserted body - L {left} | R {right}");
                long index = left + (right - left) / 2 + 1;
                ChainLevelInfo level = LoadLevel(index, true);
                Block block = level == null ? null : FindBlock(level.BlockInfos[0].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (block == null)
                {
                    left = index;
                }
                else
                {
                    lowestInsertedBlock = block;
                    right = index - 1;
                }
            }

            if (lowestInsertedBlock == null)
            {
                if (_logger.IsTrace) _logger.Trace($"Lowest inserted body is null - L {left} | R {right}");
                LowestInsertedBody = null;
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Lowest inserted body is {LowestInsertedBody?.ToString(Block.Format.Short)} {right} - L {left} | R {right}");
                LowestInsertedBody = lowestInsertedBlock;
            }
        }

        private async Task VisitBlocks(long startNumber, long blocksToVisit, Func<Block, Task<bool>> blockFound, Func<BlockHeader, Task<bool>> headerFound, Func<long, Task<bool>> noneFound, CancellationToken cancellationToken)
        {
            long blockNumber = startNumber;
            for (long i = 0; i < blocksToVisit; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                ChainLevelInfo level = LoadLevel(blockNumber);
                if (level == null)
                {
                    _logger.Warn($"Missing level - {blockNumber}");
                    break;
                }

                BigInteger maxDifficultySoFar = 0;
                BlockInfo maxDifficultyBlock = null;
                for (int blockIndex = 0; blockIndex < level.BlockInfos.Length; blockIndex++)
                {
                    if (level.BlockInfos[blockIndex].TotalDifficulty > maxDifficultySoFar)
                    {
                        maxDifficultyBlock = level.BlockInfos[blockIndex];
                        maxDifficultySoFar = maxDifficultyBlock.TotalDifficulty;
                    }
                }

                level = null;
                // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                if (level != null)
                    // ReSharper disable once HeuristicUnreachableCode
                {
                    // ReSharper disable once HeuristicUnreachableCode
                    throw new InvalidOperationException("just be aware that this level can be deleted by another thread after here");
                }

                if (maxDifficultyBlock == null)
                {
                    throw new InvalidOperationException($"Expected at least one block at level {blockNumber}");
                }

                Block block = FindBlock(maxDifficultyBlock.BlockHash, BlockTreeLookupOptions.None);
                if (block == null)
                {
                    BlockHeader header = FindHeader(maxDifficultyBlock.BlockHash, BlockTreeLookupOptions.None);
                    if (header == null)
                    {
                        bool shouldContinue = await noneFound(blockNumber);
                        if (!shouldContinue)
                        {
                            break;
                        }
                    }
                    else
                    {
                        bool shouldContinue = await headerFound(header);
                        if (!shouldContinue)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    bool shouldContinue = await blockFound(block);
                    if (!shouldContinue)
                    {
                        break;
                    }
                }

                blockNumber++;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"Canceled visiting blocks in DB at block {blockNumber}");
            }

            if (_logger.IsInfo)
            {
                _logger.Info($"Completed visiting blocks in DB at block {blockNumber} - best known {BestKnownNumber}");
            }
        }

        public async Task LoadBlocksFromDb(
            CancellationToken cancellationToken,
            long? startBlockNumber = null,
            int batchSize = DbLoadBatchSize,
            int maxBlocksToLoad = int.MaxValue)
        {
            try
            {
                CanAcceptNewBlocks = false;

                byte[] deletePointer = _blockInfoDb.Get(DeletePointerAddressInDb);
                if (deletePointer != null)
                {
                    Keccak deletePointerHash = new Keccak(deletePointer);
                    if (_logger.IsInfo) _logger.Info($"Cleaning invalid blocks starting from {deletePointer}");
                    DeleteBlocks(deletePointerHash);
                }

                if (startBlockNumber == null)
                {
                    startBlockNumber = Head?.Number ?? 0;
                }
                else
                {
                    Head = startBlockNumber == 0 ? null : FindBlock(startBlockNumber.Value - 1, BlockTreeLookupOptions.RequireCanonical)?.Header;
                }

                long blocksToLoad = Math.Min(CountKnownAheadOfHead(), maxBlocksToLoad);
                if (blocksToLoad == 0)
                {
                    if (_logger.IsInfo) _logger.Info("Found no blocks to load from DB");
                    return;
                }

                if (_logger.IsInfo) _logger.Info($"Found {blocksToLoad} blocks to load from DB starting from current head block {Head?.ToString(BlockHeader.Format.Short)}");

                Task<bool> NoneFound(long number)
                {
                    _chainLevelInfoRepository.Delete(number);
                    BestKnownNumber = number - 1;
                    return Task.FromResult(false);
                }

                Task<bool> HeaderFound(BlockHeader header)
                {
                    BestSuggestedHeader = header;
                    long i = header.Number - startBlockNumber.Value;
                    // copy paste from below less batching
                    if (i % batchSize == batchSize - 1 && i != blocksToLoad - 1 && Head.Number + batchSize < header.Number)
                    {
                        if (_logger.IsInfo) _logger.Info($"Loaded {i + 1} out of {blocksToLoad} headers from DB.");
                    }

                    return Task.FromResult(true);
                }

                async Task<bool> BlockFound(Block block)
                {
                    BestSuggestedHeader = block.Header;
                    BestSuggestedBody = block;
                    NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));

                    long i = block.Number - startBlockNumber.Value;
                    if (i % batchSize == batchSize - 1 && i != blocksToLoad - 1 && Head.Number + batchSize < block.Number)
                    {
                        if (_logger.IsInfo)
                        {
                            _logger.Info($"Loaded {i + 1} out of {blocksToLoad} blocks from DB into processing queue, waiting for processor before loading more.");
                        }

                        _dbBatchProcessed = new TaskCompletionSource<object>();
                        using (cancellationToken.Register(() => _dbBatchProcessed.SetCanceled()))
                        {
                            _currentDbLoadBatchEnd = block.Number - batchSize;
                            await _dbBatchProcessed.Task;
                        }
                    }

                    return true;
                }

                await VisitBlocks(startBlockNumber.Value, blocksToLoad, BlockFound, HeaderFound, NoneFound, cancellationToken);
            }
            finally
            {
                CanAcceptNewBlocks = true;
            }
        }

        public AddBlockResult Insert(BlockHeader header)
        {
            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (header.Number == 0)
            {
                throw new InvalidOperationException("Genesis block should not be inserted.");
            }

            // validate hash here
            Rlp newRlp = _headerDecoder.Encode(header);
            _headerDb.Set(header.Hash, newRlp.Bytes);

            BlockInfo blockInfo = new BlockInfo(header.Hash, header.TotalDifficulty ?? 0);
            ChainLevelInfo chainLevel = new ChainLevelInfo(false, blockInfo);
            _chainLevelInfoRepository.PersistLevel(header.Number, chainLevel);
            
            if (header.Number < (LowestInsertedHeader?.Number ?? long.MaxValue))
            {
                LowestInsertedHeader = header;
            }

            if (header.Number > BestKnownNumber)
            {
                BestKnownNumber = header.Number;
            }

            return AddBlockResult.Added;
        }

        public AddBlockResult Insert(Block block)
        {
            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (block.Number == 0)
            {
                throw new InvalidOperationException("Genesis block should not be inserted.");
            }

            Rlp newRlp = _blockDecoder.Encode(block);
            _blockDb.Set(block.Hash, newRlp.Bytes);

            long expectedNumber = (LowestInsertedBody?.Number - 1 ?? LongConverter.FromString(_syncConfig.PivotNumber ?? "0"));
            if (block.Number != expectedNumber)
            {
                throw new InvalidOperationException($"Trying to insert out of order block {block.Number} when expected number was {expectedNumber}");
            }

            if (block.Number < (LowestInsertedBody?.Number ?? long.MaxValue))
            {
                LowestInsertedBody = block;
            }

            return AddBlockResult.Added;
        }

        public void Insert(IEnumerable<Block> blocks)
        {
            lock (_batchInsertLock)
            {
                try
                {
                    _blockDb.StartBatch();
                    foreach (Block block in blocks)
                    {
                        Insert(block);
                    }
                }
                finally
                {
                    _blockDb.CommitBatch();
                }
            }
        }

        private AddBlockResult Suggest(Block block, BlockHeader header, bool shouldProcess = true)
        {
#if DEBUG
            /* this is just to make sure that we do not fall into this trap when creating tests */
            if (header.StateRoot == null && !header.IsGenesis)
            {
                throw new InvalidDataException($"State root is null in {header.ToString(BlockHeader.Format.Short)}");
            }
#endif

            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (_invalidBlocks.ContainsKey(header.Number) && _invalidBlocks[header.Number].Contains(header.Hash))
            {
                return AddBlockResult.InvalidBlock;
            }

            bool isKnown = IsKnownBlock(header.Number, header.Hash);
            if (header.Number == 0)
            {
                if (BestSuggestedHeader != null)
                {
                    throw new InvalidOperationException("Genesis block should be added only once");
                }
            }
            else if (isKnown && (BestSuggestedHeader?.Number ?? 0) >= header.Number)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Block {header.Hash} already known.");
                }

                return AddBlockResult.AlreadyKnown;
            }
            else if (!IsKnownBlock(header.Number - 1, header.ParentHash))
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Could not find parent ({header.ParentHash}) of block {header.Hash}");
                }

                return AddBlockResult.UnknownParent;
            }

            SetTotalDifficulty(header);

            if (block != null && !isKnown)
            {
                Rlp newRlp = _blockDecoder.Encode(block);
                _blockDb.Set(block.Hash, newRlp.Bytes);
            }

            if (!isKnown)
            {
                Rlp newRlp = _headerDecoder.Encode(header);
                _headerDb.Set(header.Hash, newRlp.Bytes);

                BlockInfo blockInfo = new BlockInfo(header.Hash, header.TotalDifficulty ?? 0);
                UpdateOrCreateLevel(header.Number, blockInfo);
            }

            if (header.IsGenesis || header.TotalDifficulty > (BestSuggestedHeader?.TotalDifficulty ?? 0))
            {
                if (header.IsGenesis)
                {
                    Genesis = header;
                }

                BestSuggestedHeader = header;
                if (block != null && shouldProcess)
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

        public AddBlockResult SuggestBlock(Block block, bool shouldProcess = true)
        {
            if (Genesis == null && !block.IsGenesis)
            {
                throw new InvalidOperationException("Block tree should be initialized with genesis before suggesting other blocks.");
            }

            return Suggest(block, block.Header, shouldProcess);
        }

        public BlockHeader FindHeader(long number, BlockTreeLookupOptions options)
        {
            Keccak blockHash = GetBlockHashOnMainOrOnlyHash(number);
            return blockHash == null ? null : FindHeader(blockHash, options);
        }

        public BlockHeader FindHeader(Keccak blockHash, BlockTreeLookupOptions options)
        {
            if (blockHash == null || blockHash == Keccak.Zero)
            {
                // TODO: would be great to check why this is still needed (maybe it is something archaic)
                return null;
            }

            BlockHeader header = _headerCache.Get(blockHash);
            if (header == null)
            {
                IDbWithSpan spanHeaderDb = _headerDb as IDbWithSpan;
                if (spanHeaderDb != null)
                {
                    Span<byte> data = spanHeaderDb.GetSpan(blockHash);
                    if (data == null)
                    {
                        return null;
                    }

                    header = _headerDecoder.Decode(data.AsRlpValueContext(), RlpBehaviors.AllowExtraData);
                    spanHeaderDb.DangerousReleaseMemory(data);
                }
                else
                {
                    byte[] data = _headerDb.Get(blockHash);
                    if (data == null)
                    {
                        return null;
                    }

                    header = _headerDecoder.Decode(data.AsRlpStream(), RlpBehaviors.AllowExtraData);
                }
            }

            bool totalDifficultyNeeded = (options & BlockTreeLookupOptions.TotalDifficultyNotNeeded) == BlockTreeLookupOptions.None;
            bool requiresCanonical = (options & BlockTreeLookupOptions.RequireCanonical) == BlockTreeLookupOptions.RequireCanonical;

            if ((totalDifficultyNeeded && header.TotalDifficulty == null) || requiresCanonical)
            {
                (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(header.Number, header.Hash);
                if (level == null || blockInfo == null)
                {
                    // TODO: this is here because storing block data is not transactional
                    // TODO: would be great to remove it, he?
                    SetTotalDifficulty(header);
                    blockInfo = new BlockInfo(header.Hash, header.TotalDifficulty.Value);
                    UpdateOrCreateLevel(header.Number, blockInfo);

                    (_, level) = LoadInfo(header.Number, header.Hash);
                }
                else
                {
                    header.TotalDifficulty = blockInfo.TotalDifficulty;
                }

                if (requiresCanonical)
                {
                    bool isMain = level.MainChainBlock?.BlockHash.Equals(blockHash) == true;
                    header = isMain ? header : null;
                }
            }

            if (header != null && ShouldCache(header.Number))
            {
                _headerCache.Set(blockHash, header);
            }

            return header;
        }

        public Keccak FindHash(long number)
        {
            return GetBlockHashOnMainOrOnlyHash(number);
        }

        public BlockHeader[] FindHeaders(Keccak blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            if (numberOfBlocks == 0)
            {
                return Array.Empty<BlockHeader>();
            }

            if (blockHash == null)
            {
                return new BlockHeader[numberOfBlocks];
            }

            BlockHeader startHeader = FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (startHeader == null)
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
                if (endHeader != null)
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
            } while (current != null && responseIndex < numberOfBlocks);

            return result;
        }

        private BlockHeader[] FindHeadersReversedFull(BlockHeader startHeader, int numberOfBlocks)
        {
            if (startHeader == null) throw new ArgumentNullException(nameof(startHeader));
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

                current = FindHeader(current.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            } while (current != null && responseIndex < numberOfBlocks);

            return result;
        }

        private Keccak GetBlockHashOnMainOrOnlyHash(long blockNumber)
        {
            if (blockNumber < 0)
            {
                throw new ArgumentException($"{nameof(blockNumber)} must be greater or equal zero and is {blockNumber}",
                    nameof(blockNumber));
            }

            ChainLevelInfo level = LoadLevel(blockNumber);
            if (level == null)
            {
                return null;
            }

            if (level.HasBlockOnMainChain)
            {
                return level.BlockInfos[0].BlockHash;
            }

            if (level.BlockInfos.Length != 1)
            {
                if (_logger.IsError) _logger.Error($"Invalid request for block {blockNumber} ({level.BlockInfos.Length} blocks at the same level).");
                throw new InvalidOperationException($"Unexpected request by number for a block {blockNumber} that is not on the main chain and is not the only hash on chain");
            }

            return level.BlockInfos[0].BlockHash;
        }

        public Block FindBlock(long blockNumber, BlockTreeLookupOptions options)
        {
            Keccak hash = GetBlockHashOnMainOrOnlyHash(blockNumber);
            return FindBlock(hash, options);
        }

        public void DeleteInvalidBlock(Block invalidBlock)
        {
            if (_logger.IsDebug) _logger.Debug($"Deleting invalid block {invalidBlock.ToString(Block.Format.FullHashAndNumber)}");

            _invalidBlocks.AddOrUpdate(
                invalidBlock.Number,
                number => new HashSet<Keccak> {invalidBlock.Hash},
                (number, set) =>
                {
                    set.Add(invalidBlock.Hash);
                    return set;
                });

            BestSuggestedHeader = Head;
            BestSuggestedBody = Head == null ? null : FindBlock(Head.Hash, BlockTreeLookupOptions.None);

            try
            {
                CanAcceptNewBlocks = false;
            }
            finally
            {
                DeleteBlocks(invalidBlock.Hash);
                CanAcceptNewBlocks = true;
            }
        }

        private void DeleteBlocks(Keccak deletePointer)
        {
            BlockHeader deleteHeader = FindHeader(deletePointer, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            long currentNumber = deleteHeader.Number;
            Keccak currentHash = deleteHeader.Hash;
            Keccak nextHash = null;
            ChainLevelInfo nextLevel = null;

            using (var batch = _chainLevelInfoRepository.StartBatch())
            {
                while (true)
                {
                    ChainLevelInfo currentLevel = nextLevel ?? LoadLevel(currentNumber);
                    nextLevel = LoadLevel(currentNumber + 1);

                    bool shouldRemoveLevel = false;
                    if (currentLevel != null) // preparing update of the level (removal of the invalid branch block)
                    {
                        if (currentLevel.BlockInfos.Length == 1)
                        {
                            shouldRemoveLevel = true;
                        }
                        else
                        {
                            for (int i = 0; i < currentLevel.BlockInfos.Length; i++)
                            {
                                if (currentLevel.BlockInfos[0].BlockHash == currentHash)
                                {
                                    currentLevel.BlockInfos = currentLevel.BlockInfos.Where(bi => bi.BlockHash != currentHash).ToArray();
                                    break;
                                }
                            }
                        }
                    }

                    // just finding what the next descendant will be
                    if (nextLevel != null)
                    {
                        nextHash = FindChild(nextLevel, currentHash);
                    }

                    UpdateDeletePointer(nextHash);


                    if (shouldRemoveLevel)
                    {
                        BestKnownNumber = Math.Min(BestKnownNumber, currentNumber - 1);
                        _chainLevelInfoRepository.Delete(currentNumber, batch);
                    }
                    else
                    {
                        _chainLevelInfoRepository.PersistLevel(currentNumber, currentLevel, batch);
                    }                    
                    

                    if (_logger.IsInfo) _logger.Info($"Deleting invalid block {currentHash} at level {currentNumber}");
                    _blockCache.Delete(currentHash);
                    _blockDb.Delete(currentHash);
                    _headerCache.Delete(currentHash);
                    _headerDb.Delete(currentHash);

                    if (nextHash == null)
                    {
                        break;
                    }

                    currentNumber++;
                    currentHash = nextHash;
                    nextHash = null;
                }
            }
        }

        private Keccak FindChild(ChainLevelInfo level, Keccak parentHash)
        {
            Keccak childHash = null;
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                BlockHeader potentialChild = FindHeader(level.BlockInfos[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (potentialChild.ParentHash == parentHash)
                {
                    childHash = potentialChild.Hash;
                    break;
                }
            }

            return childHash;
        }

        public bool IsMainChain(Keccak blockHash)
        {
            long number = LoadNumberOnly(blockHash);
            ChainLevelInfo level = LoadLevel(number);
            return level.MainChainBlock?.BlockHash.Equals(blockHash) == true;
        }

        public bool WasProcessed(long number, Keccak blockHash)
        {
            ChainLevelInfo levelInfo = LoadLevel(number);
            int? index = FindIndex(blockHash, levelInfo);
            if (index == null)
            {
                throw new InvalidOperationException($"Not able to find block {blockHash} index on the chain level");
            }

            return levelInfo.BlockInfos[index.Value].WasProcessed;
        }

        public void UpdateMainChain(Block[] processedBlocks)
        {
            if (processedBlocks.Length == 0)
            {
                return;
            }

            bool ascendingOrder = true;
            if (processedBlocks.Length > 1)
            {
                if (processedBlocks[processedBlocks.Length - 1].Number < processedBlocks[0].Number)
                {
                    ascendingOrder = false;
                }
            }

#if DEBUG
            for (int i = 0; i < processedBlocks.Length; i++)
            {
                if (i != 0)
                {
                    if (ascendingOrder && processedBlocks[i].Number != processedBlocks[i - 1].Number + 1)
                    {
                        throw new InvalidOperationException("Update main chain invoked with gaps");
                    }

                    if (!ascendingOrder && processedBlocks[i - 1].Number != processedBlocks[i].Number + 1)
                    {
                        throw new InvalidOperationException("Update main chain invoked with gaps");
                    }
                }
            }
#endif

            long lastNumber = ascendingOrder ? processedBlocks[processedBlocks.Length - 1].Number : processedBlocks[0].Number;
            long previousHeadNumber = Head?.Number ?? 0L;
            using (var batch = _chainLevelInfoRepository.StartBatch())
            {
                if (previousHeadNumber > lastNumber)
                {
                    for (long i = 0; i < previousHeadNumber - lastNumber; i++)
                    {
                        long levelNumber = previousHeadNumber - i;

                        ChainLevelInfo level = LoadLevel(levelNumber);
                        level.HasBlockOnMainChain = false;
                        _chainLevelInfoRepository.PersistLevel(levelNumber, level, batch);
                    }
                }

                for (int i = 0; i < processedBlocks.Length; i++)
                {
                    Block block = processedBlocks[i];
                    if (ShouldCache(block.Number))
                    {
                        _blockCache.Set(block.Hash, processedBlocks[i]);
                        _headerCache.Set(block.Hash, block.Header);
                    }

                    MoveToMain(processedBlocks[i], batch);
                }
            }
        }

        private TaskCompletionSource<object> _dbBatchProcessed;

        private void MoveToMain(Block block, BatchWrite batch)
        {
            if (_logger.IsTrace) _logger.Trace($"Moving {block.ToString(Block.Format.Short)} to main");

            ChainLevelInfo level = LoadLevel(block.Number);
            int? index = FindIndex(block.Hash, level);
            if (index == null)
            {
                throw new InvalidOperationException($"Cannot move unknown block {block.ToString(Block.Format.FullHashAndNumber)} to main");
            }

            Keccak hashOfThePreviousMainBlock = level.MainChainBlock?.BlockHash;

            BlockInfo info = level.BlockInfos[index.Value];
            info.WasProcessed = true;
            if (index.Value != 0)
            {
                (level.BlockInfos[index.Value], level.BlockInfos[0]) = (level.BlockInfos[0], level.BlockInfos[index.Value]);
            }

            level.HasBlockOnMainChain = true;
            _chainLevelInfoRepository.PersistLevel(block.Number, level, batch);

            BlockAddedToMain?.Invoke(this, new BlockEventArgs(block));

            if (block.IsGenesis || block.TotalDifficulty > (Head?.TotalDifficulty ?? 0))
            {
                if (block.Number == 0)
                {
                    Genesis = block.Header;
                }

                if (block.TotalDifficulty == null)
                {
                    throw new InvalidOperationException("Head block with null total difficulty");
                }

                UpdateHeadBlock(block);
            }

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                _txPool.RemoveTransaction(block.Transactions[i].Hash, block.Number);
            }

            // the hash will only be the same during perf test runs / modified DB states
            if (hashOfThePreviousMainBlock != null && hashOfThePreviousMainBlock != block.Hash)
            {
                Block previous = FindBlock(hashOfThePreviousMainBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                for (int i = 0; i < previous.Transactions.Length; i++)
                {
                    Transaction tx = previous.Transactions[i];
                    _txPool.AddTransaction(tx, previous.Number);
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Block {block.ToString(Block.Format.Short)} added to main chain");
        }

        [Todo(Improve.Refactor, "Look at this magic -1 behaviour, never liked it, now when it is split between BestKnownNumber and Head it is even worse")]
        private long CountKnownAheadOfHead()
        {
            long headNumber = Head?.Number ?? -1;
            return BestKnownNumber - headNumber;
        }

        private void LoadHeadBlockAtStart()
        {
            byte[] data = _blockInfoDb.Get(HeadAddressInDb);
            if (data != null)
            {
                BlockHeader headBlockHeader = data.Length == 32
                    ? FindHeader(new Keccak(data), BlockTreeLookupOptions.None)
                    : Rlp.Decode<BlockHeader>(data.AsRlpStream(), RlpBehaviors.AllowExtraData);

                ChainLevelInfo level = LoadLevel(headBlockHeader.Number);
                int? index = FindIndex(headBlockHeader.Hash, level);
                if (!index.HasValue)
                {
                    throw new InvalidDataException("Head block data missing from chain info");
                }

                headBlockHeader.TotalDifficulty = level.BlockInfos[index.Value].TotalDifficulty;

                Head = BestSuggestedHeader = headBlockHeader;
                BestSuggestedBody = FindBlock(headBlockHeader.Hash, BlockTreeLookupOptions.None);
            }
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

            if (_headerCache.Get(blockHash) != null)
            {
                return true;
            }

            ChainLevelInfo level = LoadLevel(number);
            return level != null && FindIndex(blockHash, level).HasValue;
        }

        private void UpdateDeletePointer(Keccak hash)
        {
            if (hash == null)
            {
                _blockInfoDb.Delete(DeletePointerAddressInDb);
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Deleting an invalid block or its descendant {hash}");
                _blockInfoDb.Set(DeletePointerAddressInDb, hash.Bytes);
            }
        }

        private void UpdateHeadBlock(Block block)
        {
            if (block.IsGenesis)
            {
                Genesis = block.Header;
            }

            Head = block.Header;
            _blockInfoDb.Set(HeadAddressInDb, Head.Hash.Bytes);
            NewHeadBlock?.Invoke(this, new BlockEventArgs(block));
            if (_dbBatchProcessed != null)
            {
                if (block.Number == _currentDbLoadBatchEnd)
                {
                    TaskCompletionSource<object> completionSource = _dbBatchProcessed;
                    _dbBatchProcessed = null;
                    completionSource.SetResult(null);
                }
            }
        }

        private void UpdateOrCreateLevel(long number, BlockInfo blockInfo)
        {
            using (var batch = _chainLevelInfoRepository.StartBatch())
            {
                ChainLevelInfo level = LoadLevel(number, false);

                if (level != null)
                {
                    BlockInfo[] blockInfos = level.BlockInfos;
                    Array.Resize(ref blockInfos, blockInfos.Length + 1);
                    blockInfos[blockInfos.Length - 1] = blockInfo;
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

                _chainLevelInfoRepository.PersistLevel(number, level, batch);                
            }
        }

        private (BlockInfo Info, ChainLevelInfo Level) LoadInfo(long number, Keccak blockHash)
        {
            ChainLevelInfo chainLevelInfo = LoadLevel(number);
            if (chainLevelInfo == null)
            {
                return (null, null);
            }

            int? index = FindIndex(blockHash, chainLevelInfo);
            return index.HasValue ? (chainLevelInfo.BlockInfos[index.Value], chainLevelInfo) : (null, chainLevelInfo);
        }

        private int? FindIndex(Keccak blockHash, ChainLevelInfo level)
        {
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                if (level.BlockInfos[i].BlockHash.Equals(blockHash))
                {
                    return i;
                }
            }

            return null;
        }

        private ChainLevelInfo LoadLevel(long number, bool forceLoad = true)
        {
            if (number > BestKnownNumber && !forceLoad)
            {
                return null;
            }

            return _chainLevelInfoRepository.LoadLevel(number);
        }

        private long LoadNumberOnly(Keccak blockHash)
        {
            BlockHeader header = FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header == null)
            {
                throw new InvalidOperationException(
                    $"Not able to retrieve block number for an unknown block {blockHash}");
            }

            return header.Number;
        }

        /// <summary>
        /// To make cache useful even when we handle sync requests
        /// </summary>
        /// <param name="number"></param>
        /// <returns></returns>
        private bool ShouldCache(long number)
        {
            return number == 0L || Head == null || number > Head.Number - CacheSize && number <= Head.Number + 1;
        }

        public Block FindBlock(Keccak blockHash, BlockTreeLookupOptions options)
        {
            if (blockHash == null || blockHash == Keccak.Zero)
            {
                return null;
            }

            Block block = _blockCache.Get(blockHash);
            if (block == null)
            {
                byte[] data = _blockDb.Get(blockHash);
                if (data == null)
                {
                    return null;
                }

                block = _blockDecoder.Decode(data.AsRlpStream(), RlpBehaviors.AllowExtraData);
            }

            bool totalDifficultyNeeded = (options & BlockTreeLookupOptions.TotalDifficultyNotNeeded) == BlockTreeLookupOptions.None;
            bool requiresCanonical = (options & BlockTreeLookupOptions.RequireCanonical) == BlockTreeLookupOptions.RequireCanonical;

            if ((totalDifficultyNeeded && block.TotalDifficulty == null) || requiresCanonical)
            {
                (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(block.Number, block.Hash);
                if (level == null || blockInfo == null)
                {
                    // TODO: this is here because storing block data is not transactional
                    // TODO: would be great to remove it, he?
                    SetTotalDifficulty(block.Header);
                    blockInfo = new BlockInfo(block.Hash, block.TotalDifficulty.Value);
                    UpdateOrCreateLevel(block.Number, blockInfo);

                    (_, level) = LoadInfo(block.Number, block.Hash);
                }
                else
                {
                    block.Header.TotalDifficulty = blockInfo.TotalDifficulty;
                }

                if (requiresCanonical)
                {
                    bool isMain = level.MainChainBlock?.BlockHash.Equals(blockHash) == true;;
                    block = isMain ? block : null;
                }
            }

            if (block != null && ShouldCache(block.Number))
            {
                _blockCache.Set(blockHash, block);
                _headerCache.Set(blockHash, block.Header);
            }

            return block;
        }

        private void SetTotalDifficulty(BlockHeader header)
        {
            if (_logger.IsTrace)
            {
                _logger.Trace($"Calculating total difficulty for {header}");
            }

            if (header.Number == 0)
            {
                header.TotalDifficulty = header.Difficulty;
            }
            else
            {
                BlockHeader parentHeader = this.FindParentHeader(header, BlockTreeLookupOptions.None);
                if (parentHeader == null)
                {
                    throw new InvalidOperationException($"An orphaned block on the chain {header}");
                }

                if (parentHeader.TotalDifficulty == null)
                {
                    throw new InvalidOperationException(
                        $"Parent's {nameof(parentHeader.TotalDifficulty)} unknown when calculating for {header}");
                }

                header.TotalDifficulty = parentHeader.TotalDifficulty + header.Difficulty;
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"Calculated total difficulty for {header} is {header.TotalDifficulty}");
            }
        }

        public event EventHandler<BlockEventArgs> BlockAddedToMain;

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock;

        public event EventHandler<BlockEventArgs> NewHeadBlock;

        public async Task FixFastSyncGaps(CancellationToken cancellationToken)
        {
            try
            {
                CanAcceptNewBlocks = false;
                long startNumber = Head?.Number ?? 0;
                if (startNumber == 0)
                {
                    return;
                }

                long blocksToLoad = CountKnownAheadOfHead();
                if (blocksToLoad == 0)
                {
                    return;
                }
                
                long? gapStart = null;
                long? gapEnd = null;

                Keccak firstInvalidHash = null;
                bool shouldDelete = false;

                Task<bool> NoneFound(long number) => Task.FromResult(false);

                Task<bool> HeaderFound(BlockHeader header)
                {
                    if (firstInvalidHash == null)
                    {
                        gapStart = header.Number;
                        firstInvalidHash = header.Hash;
                    }

                    return Task.FromResult(true);
                }

                Task<bool> BlockFound(Block block)
                {
                    if (firstInvalidHash != null && !shouldDelete)
                    {
                        gapEnd = block.Number;
                        shouldDelete = true;
                    }

                    return Task.FromResult(true);
                }

                await VisitBlocks(startNumber + 1, blocksToLoad, BlockFound, HeaderFound, NoneFound, cancellationToken);

                if (shouldDelete)
                {
                    if (_logger.IsWarn) _logger.Warn($"Deleting blocks starting with {firstInvalidHash} due to the gap found between {gapStart} and {gapEnd}");
                    DeleteBlocks(firstInvalidHash);
                    BestSuggestedHeader = Head;
                    BestSuggestedBody = Head == null ? null : FindBlock(Head.Hash, BlockTreeLookupOptions.None);
                }
            }
            finally
            {
                CanAcceptNewBlocks = true;
            }
        }
    }
}