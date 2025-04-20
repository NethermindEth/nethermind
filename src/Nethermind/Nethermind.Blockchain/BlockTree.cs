// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain
{
    [Todo(Improve.Refactor, "After the fast sync work there are some duplicated code parts for the 'by header' and 'by block' approaches.")]
    public partial class BlockTree : IBlockTree
    {
        // there is not much logic in the addressing here
        private static readonly byte[] StateHeadHashDbEntryAddress = new byte[16];
        internal static Hash256 DeletePointerAddressInDb = new(new BitArray(32 * 8, true).ToBytes());
        internal static Hash256 HeadAddressInDb = Keccak.Zero;

        private const int BestKnownSearchLimit = 256_000_000;

        private readonly IBlockStore _blockStore;
        private readonly IHeaderStore _headerStore;
        private readonly IDb _blockInfoDb;
        private readonly IDb _metadataDb;
        private readonly IBadBlockStore _badBlockStore;

        private readonly LruCache<ValueHash256, Block> _invalidBlocks =
            new(128, 128, "invalid blocks");

        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IBloomStorage _bloomStorage;
        private readonly ISyncConfig _syncConfig;
        private readonly IChainLevelInfoRepository _chainLevelInfoRepository;

        public BlockHeader? Genesis { get; private set; }
        public Block? Head { get; private set; }

        public BlockHeader? BestSuggestedHeader { get; private set; }

        public Block? BestSuggestedBody { get; private set; }
        public BlockHeader? LowestInsertedHeader
        {
            get => _lowestInsertedHeader;
            set
            {
                _lowestInsertedHeader = value;
                _metadataDb.Set(MetadataDbKeys.LowestInsertedFastHeaderHash, Rlp.Encode(value?.Hash ?? value?.CalculateHash()).Bytes);
            }
        }

        private BlockHeader? _lowestInsertedHeader;

        public BlockHeader? BestSuggestedBeaconHeader { get; private set; }

        public Block? BestSuggestedBeaconBody { get; private set; }

        public BlockHeader? LowestInsertedBeaconHeader
        {
            get => _lowestInsertedBeaconHeader;
            set
            {
                _lowestInsertedBeaconHeader = value;
                _metadataDb.Set(MetadataDbKeys.LowestInsertedBeaconHeaderHash, Rlp.Encode(value?.Hash ?? value?.CalculateHash()).Bytes);
            }
        }

        private BlockHeader? _lowestInsertedBeaconHeader;

        private long? _highestPersistedState;

        public long BestKnownNumber { get; private set; }

        public long BestKnownBeaconNumber { get; private set; }

        public ulong NetworkId => _specProvider.NetworkId;

        public ulong ChainId => _specProvider.ChainId;

        private int _canAcceptNewBlocksCounter;
        public bool CanAcceptNewBlocks => _canAcceptNewBlocksCounter == 0;

        private TaskCompletionSource<bool>? _taskCompletionSource;

        public BlockTree(
            IBlockStore? blockStore,
            IHeaderStore? headerDb,
            [KeyFilter(DbNames.BlockInfos)] IDb? blockInfoDb,
            [KeyFilter(DbNames.Metadata)] IDb? metadataDb,
            IBadBlockStore? badBlockStore,
            IChainLevelInfoRepository? chainLevelInfoRepository,
            ISpecProvider? specProvider,
            IBloomStorage? bloomStorage,
            ISyncConfig? syncConfig,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockStore = blockStore ?? throw new ArgumentNullException(nameof(blockStore));
            _headerStore = headerDb ?? throw new ArgumentNullException(nameof(headerDb));
            _blockInfoDb = blockInfoDb ?? throw new ArgumentNullException(nameof(blockInfoDb));
            _metadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));
            _badBlockStore = badBlockStore ?? throw new ArgumentNullException(nameof(badBlockStore));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _bloomStorage = bloomStorage ?? throw new ArgumentNullException(nameof(bloomStorage));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _chainLevelInfoRepository = chainLevelInfoRepository ??
                                        throw new ArgumentNullException(nameof(chainLevelInfoRepository));

            LoadSyncPivot();

            byte[]? deletePointer = _blockInfoDb.Get(DeletePointerAddressInDb);
            if (deletePointer is not null)
            {
                DeleteBlocks(new Hash256(deletePointer));
            }

            // Need to be here because it still need to run even if there are no genesis to store the null entry.
            LoadLowestInsertedHeader();

            ChainLevelInfo? genesisLevel = LoadLevel(0);
            if (genesisLevel is not null)
            {
                BlockInfo genesisBlockInfo = genesisLevel.BlockInfos[0];
                if (genesisLevel.BlockInfos.Length != 1)
                {
                    // just for corrupted test bases

                    genesisLevel.BlockInfos = new[] { genesisBlockInfo };
                    _chainLevelInfoRepository.PersistLevel(0, genesisLevel);
                    //throw new InvalidOperationException($"Genesis level in DB has {genesisLevel.BlockInfos.Length} blocks");
                }

                if (genesisBlockInfo.WasProcessed)
                {
                    BlockHeader genesisHeader = FindHeader(genesisBlockInfo.BlockHash, BlockTreeLookupOptions.None);
                    Genesis = genesisHeader;
                    LoadStartBlock();
                    Head ??= FindBlock(genesisBlockInfo.BlockHash, BlockTreeLookupOptions.None);
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
                             $"lowest sync inserted block number {LowestInsertedBeaconHeader?.Number}");
            ProductInfo.Network = $"{(ChainId == NetworkId ? BlockchainIds.GetBlockchainName(NetworkId) : ChainId)}";
            ThisNodeInfo.AddInfo("Chain ID     :", ProductInfo.Network);
            ThisNodeInfo.AddInfo("Chain head   :", $"{Head?.Header.ToString(BlockHeader.Format.Short) ?? "0"}");
            if (ChainId != NetworkId)
            {
                ThisNodeInfo.AddInfo("Network ID   :", $"{NetworkId}");
            }
        }

        public AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None)
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

            bool totalDifficultyNeeded = (headerOptions & BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded) == 0;

            if (header.TotalDifficulty is null && totalDifficultyNeeded)
            {
                SetTotalDifficulty(header);
            }

            _bloomStorage.Store(header.Number, header.Bloom);
            _headerStore.Insert(header);

            bool isOnMainChain = (headerOptions & BlockTreeInsertHeaderOptions.NotOnMainChain) == 0;
            BlockInfo blockInfo = new(header.Hash, header.TotalDifficulty ?? 0);

            bool beaconInsert = (headerOptions & BlockTreeInsertHeaderOptions.BeaconHeaderMetadata) != 0;
            if (!beaconInsert)
            {
                if (header.Number > BestKnownNumber)
                {
                    BestKnownNumber = header.Number;
                }

                if (header.Number > (BestSuggestedHeader?.Number ?? 0))
                {
                    BestSuggestedHeader = header;
                }
            }

            if (beaconInsert)
            {
                if (header.Number > BestKnownBeaconNumber)
                {
                    BestKnownBeaconNumber = header.Number;
                }

                if (header.Number > (BestSuggestedBeaconHeader?.Number ?? 0))
                {
                    BestSuggestedBeaconHeader = header;
                }

                if (header.Number < (LowestInsertedBeaconHeader?.Number ?? long.MaxValue))
                {
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"LowestInsertedBeaconHeader changed, old: {LowestInsertedBeaconHeader?.Number}, new: {header?.Number}");
                    LowestInsertedBeaconHeader = header;
                }
            }

            bool addBeaconHeaderMetadata = (headerOptions & BlockTreeInsertHeaderOptions.BeaconHeaderMetadata) != 0;
            bool addBeaconBodyMetadata = (headerOptions & BlockTreeInsertHeaderOptions.BeaconBodyMetadata) != 0;
            bool moveToBeaconMainChain = (headerOptions & BlockTreeInsertHeaderOptions.MoveToBeaconMainChain) != 0;
            if (addBeaconHeaderMetadata)
            {
                blockInfo.Metadata |= BlockMetadata.BeaconHeader;
            }

            if (addBeaconBodyMetadata)
            {
                blockInfo.Metadata |= BlockMetadata.BeaconBody;
            }


            if (moveToBeaconMainChain)
            {
                blockInfo.Metadata |= BlockMetadata.BeaconMainChain;
            }

            UpdateOrCreateLevel(header.Number, blockInfo, isOnMainChain);

            return AddBlockResult.Added;
        }

        public void BulkInsertHeader(IReadOnlyList<BlockHeader> headers,
            BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None)
        {
            if (!CanAcceptNewBlocks)
            {
                throw new InvalidOperationException("Cannot accept new blocks at the moment.");
            }

            using ArrayPoolList<(long, Bloom)> bloomToStore = new ArrayPoolList<(long, Bloom)>(headers.Count);
            foreach (var header in headers)
            {
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

                bool totalDifficultyNeeded = (headerOptions & BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded) == 0;

                if (header.TotalDifficulty is null && totalDifficultyNeeded)
                {
                    SetTotalDifficulty(header);
                }

                bloomToStore.Add((header.Number, header.Bloom));
            }

            Task bloomStoreTask = Task.Run(() =>
            {
                _bloomStorage.Store(bloomToStore);
            });

            _headerStore.BulkInsert(headers);

            bool isOnMainChain = (headerOptions & BlockTreeInsertHeaderOptions.NotOnMainChain) == 0;
            bool beaconInsert = (headerOptions & BlockTreeInsertHeaderOptions.BeaconHeaderMetadata) != 0;
            using ArrayPoolList<(long, BlockInfo)> blockInfos = new ArrayPoolList<(long, BlockInfo)>(headers.Count);

            foreach (var header in headers)
            {
                BlockInfo blockInfo = new(header.Hash, header.TotalDifficulty ?? 0);
                if (!beaconInsert)
                {
                    if (header.Number > BestKnownNumber)
                    {
                        BestKnownNumber = header.Number;
                    }

                    if (header.Number > (BestSuggestedHeader?.Number ?? 0))
                    {
                        BestSuggestedHeader = header;
                    }
                }

                if (beaconInsert)
                {
                    if (header.Number > BestKnownBeaconNumber)
                    {
                        BestKnownBeaconNumber = header.Number;
                    }

                    if (header.Number > (BestSuggestedBeaconHeader?.Number ?? 0))
                    {
                        BestSuggestedBeaconHeader = header;
                    }

                    if (header.Number < (LowestInsertedBeaconHeader?.Number ?? long.MaxValue))
                    {
                        if (_logger.IsTrace)
                            _logger.Trace(
                                $"LowestInsertedBeaconHeader changed, old: {LowestInsertedBeaconHeader?.Number}, new: {header?.Number}");
                        LowestInsertedBeaconHeader = header;
                    }
                }

                bool addBeaconHeaderMetadata = (headerOptions & BlockTreeInsertHeaderOptions.BeaconHeaderMetadata) != 0;
                bool addBeaconBodyMetadata = (headerOptions & BlockTreeInsertHeaderOptions.BeaconBodyMetadata) != 0;
                bool moveToBeaconMainChain = (headerOptions & BlockTreeInsertHeaderOptions.MoveToBeaconMainChain) != 0;
                if (addBeaconHeaderMetadata)
                {
                    blockInfo.Metadata |= BlockMetadata.BeaconHeader;
                }

                if (addBeaconBodyMetadata)
                {
                    blockInfo.Metadata |= BlockMetadata.BeaconBody;
                }


                if (moveToBeaconMainChain)
                {
                    blockInfo.Metadata |= BlockMetadata.BeaconMainChain;
                }

                blockInfos.Add((header.Number, blockInfo));
            }

            UpdateOrCreateLevel(blockInfos, isOnMainChain);

            bloomStoreTask.Wait();
        }

        public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
            BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None, WriteFlags blockWriteFlags = WriteFlags.None)
        {
            bool skipCanAcceptNewBlocks = (insertBlockOptions & BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks) != 0;
            if (!CanAcceptNewBlocks)
            {
                if (_logger.IsTrace) _logger.Trace($"Block tree in cannot accept new blocks mode. SkipCanAcceptNewBlocks: {skipCanAcceptNewBlocks}, Block {block}");
            }

            if (!CanAcceptNewBlocks && !skipCanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (block.Number == 0)
            {
                throw new InvalidOperationException("Genesis block should not be inserted.");
            }

            _blockStore.Insert(block, writeFlags: blockWriteFlags);
            _headerStore.InsertBlockNumber(block.Hash, block.Number);

            bool saveHeader = (insertBlockOptions & BlockTreeInsertBlockOptions.SaveHeader) != 0;
            if (saveHeader)
            {
                Insert(block.Header, insertHeaderOptions);
            }

            return AddBlockResult.Added;
        }

        private AddBlockResult Suggest(Block? block, BlockHeader header, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
        {
            bool shouldProcess = options.ContainsFlag(BlockTreeSuggestOptions.ShouldProcess);
            bool fillBeaconBlock = options.ContainsFlag(BlockTreeSuggestOptions.FillBeaconBlock);
            bool setAsMain = options.ContainsFlag(BlockTreeSuggestOptions.ForceSetAsMain) ||
                             !options.ContainsFlag(BlockTreeSuggestOptions.ForceDontSetAsMain) && !shouldProcess;

            if (_logger.IsTrace) _logger.Trace($"Suggesting a new block. BestSuggestedBlock {BestSuggestedBody}, BestSuggestedBlock TD {BestSuggestedBody?.TotalDifficulty}, Block TD {block?.TotalDifficulty}, Head: {Head}, Head TD: {Head?.TotalDifficulty}, Block {block?.ToString(Block.Format.FullHashAndNumber)}. ShouldProcess: {shouldProcess}, TryProcessKnownBlock: {fillBeaconBlock}, SetAsMain {setAsMain}");

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

            if (_invalidBlocks.Contains(header.Hash))
            {
                return AddBlockResult.InvalidBlock;
            }

            bool isKnown = IsKnownBlock(header.Number, header.Hash);
            if (isKnown && (BestSuggestedHeader?.Number ?? 0) >= header.Number)
            {
                if (_logger.IsTrace) _logger.Trace($"Block {header.ToString(BlockHeader.Format.FullHashAndNumber)} already known.");
                return AddBlockResult.AlreadyKnown;
            }

            bool parentExists = IsKnownBlock(header.Number - 1, header.ParentHash!) ||
                                IsKnownBeaconBlock(header.Number - 1, header.ParentHash!);
            if (!header.IsGenesis && !parentExists)
            {
                if (_logger.IsTrace) _logger.Trace($"Could not find parent ({header.ParentHash}) of block {header.Hash}");
                return AddBlockResult.UnknownParent;
            }

            SetTotalDifficulty(header);

            if (block is not null)
            {
                if (block.Hash is null)
                {
                    throw new InvalidOperationException("An attempt to suggest block with a null hash.");
                }

                _blockStore.Insert(block);
            }

            if (!isKnown)
            {
                _headerStore.Insert(header);
            }

            if (!isKnown || fillBeaconBlock)
            {
                BlockInfo blockInfo = new(header.Hash, header.TotalDifficulty ?? 0);
                UpdateOrCreateLevel(header.Number, blockInfo, setAsMain);
                NewSuggestedBlock?.Invoke(this, new BlockEventArgs(block!));
            }

            if (header.IsGenesis)
            {
                Genesis = header;
                BestSuggestedHeader = header;
            }

            if (block is not null)
            {
                bool bestSuggestedImprovementSatisfied = BestSuggestedImprovementRequirementsSatisfied(header);
                if (bestSuggestedImprovementSatisfied)
                {
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"New best suggested block. PreviousBestSuggestedBlock {BestSuggestedBody}, BestSuggestedBlock TD {BestSuggestedBody?.TotalDifficulty}, Block TD {block?.TotalDifficulty}, Head: {Head}, Head: {Head?.TotalDifficulty}, Block {block?.ToString(Block.Format.FullHashAndNumber)}");
                    BestSuggestedHeader = block.Header;

                    if (block.IsPostMerge)
                    {
                        BestSuggestedBody = block;
                    }
                }

                if (shouldProcess && (bestSuggestedImprovementSatisfied || header.IsGenesis || fillBeaconBlock))
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

        public async ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions suggestOptions = BlockTreeSuggestOptions.ShouldProcess)
        {
            if (!WaitForReadinessToAcceptNewBlock.IsCompleted)
            {
                await WaitForReadinessToAcceptNewBlock;
            }

            return SuggestBlock(block, suggestOptions);
        }

        public AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
        {
            if (Genesis is null && !block.IsGenesis)
            {
                throw new InvalidOperationException("Block tree should be initialized with genesis before suggesting other blocks.");
            }

            return Suggest(block, block.Header, options);
        }

        public BlockHeader? FindHeader(long number, BlockTreeLookupOptions options)
        {
            Hash256 blockHash = GetBlockHashOnMainOrBestDifficultyHash(number);
            return blockHash is null ? null : FindHeader(blockHash, options, blockNumber: number);
        }

        public Hash256? FindBlockHash(long blockNumber) => GetBlockHashOnMainOrBestDifficultyHash(blockNumber);

        public bool HasBlock(long blockNumber, Hash256 blockHash) => _blockStore.HasBlock(blockNumber, blockHash);

        public BlockHeader? FindHeader(Hash256? blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
        {
            if (blockHash is null || blockHash == Keccak.Zero)
            {
                // TODO: would be great to check why this is still needed (maybe it is something archaic)
                return null;
            }

            BlockHeader? header = _headerStore.Get(blockHash, shouldCache: false, blockNumber: blockNumber);
            if (header is null)
            {
                bool allowInvalid = (options & BlockTreeLookupOptions.AllowInvalid) == BlockTreeLookupOptions.AllowInvalid;
                if (allowInvalid && _invalidBlocks.TryGet(blockHash, out Block block))
                {
                    header = block.Header;
                }

                return header;
            }

            header.Hash ??= blockHash;
            bool totalDifficultyNeeded = (options & BlockTreeLookupOptions.TotalDifficultyNotNeeded) == BlockTreeLookupOptions.None;
            bool createLevelIfMissing = (options & BlockTreeLookupOptions.DoNotCreateLevelIfMissing) == BlockTreeLookupOptions.None;
            bool requiresCanonical = (options & BlockTreeLookupOptions.RequireCanonical) == BlockTreeLookupOptions.RequireCanonical;

            if ((totalDifficultyNeeded && header.TotalDifficulty is null) || requiresCanonical)
            {
                (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(header.Number, header.Hash, true);
                if (level is null || blockInfo is null)
                {
                    // TODO: this is here because storing block data is not transactional
                    // TODO: would be great to remove it, he?
                    // TODO: we should remove it - readonly method modifies DB
                    bool isSearchingForBeaconBlock = (BestKnownBeaconNumber > BestKnownNumber && header.Number > BestKnownNumber);  // if we're searching for beacon block we don't want to create level. We're creating it in different place with beacon metadata
                    if (createLevelIfMissing == false || isSearchingForBeaconBlock)
                    {
                        if (_logger.IsInfo) _logger.Info($"Missing block info - ignoring creation of the level in {nameof(FindHeader)} scope when head is {Head?.ToString(Block.Format.Short)}. BlockHeader {header.ToString(BlockHeader.Format.FullHashAndNumber)}, CreateLevelIfMissing: {createLevelIfMissing}. BestKnownBeaconNumber: {BestKnownBeaconNumber}, BestKnownNumber: {BestKnownNumber}");
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"Missing block info - creating level in {nameof(FindHeader)} scope when head is {Head?.ToString(Block.Format.Short)}. BlockHeader {header.ToString(BlockHeader.Format.FullHashAndNumber)}, CreateLevelIfMissing: {createLevelIfMissing}. BestKnownBeaconNumber: {BestKnownBeaconNumber}, BestKnownNumber: {BestKnownNumber}");
                        SetTotalDifficulty(header);
                        blockInfo = new BlockInfo(header.Hash, header.TotalDifficulty ?? UInt256.Zero);
                        level = UpdateOrCreateLevel(header.Number, blockInfo);
                    }
                }
                else
                {
                    SetTotalDifficultyFromBlockInfo(header, blockInfo);
                }

                if (requiresCanonical)
                {
                    bool isMain = level.MainChainBlock?.BlockHash?.Equals(blockHash) == true;
                    header = isMain ? header : null;
                }
            }

            if (header is not null && ShouldCache(header.Number))
            {
                _headerStore.Cache(header);
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

        public Hash256? FindHash(long number)
        {
            return GetBlockHashOnMainOrBestDifficultyHash(number);
        }

        public IOwnedReadOnlyList<BlockHeader> FindHeaders(Hash256? blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            if (numberOfBlocks == 0)
            {
                return ArrayPoolList<BlockHeader>.Empty();
            }

            if (blockHash is null)
            {
                return new ArrayPoolList<BlockHeader>(numberOfBlocks, numberOfBlocks);
            }

            BlockHeader startHeader = FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (startHeader is null)
            {
                return new ArrayPoolList<BlockHeader>(numberOfBlocks, numberOfBlocks);
            }

            if (numberOfBlocks == 1)
            {
                return new ArrayPoolList<BlockHeader>(1) { startHeader };
            }

            if (skip == 0)
            {
                static ArrayPoolList<BlockHeader> FindHeadersReversedFast(BlockTree tree, BlockHeader startHeader, int numberOfBlocks, bool reverse = false)
                {
                    ArgumentNullException.ThrowIfNull(startHeader);
                    if (numberOfBlocks == 1)
                    {
                        return new ArrayPoolList<BlockHeader>(1) { startHeader };
                    }

                    ArrayPoolList<BlockHeader> result = new ArrayPoolList<BlockHeader>(numberOfBlocks, numberOfBlocks);

                    BlockHeader current = startHeader;
                    int responseIndex = reverse ? 0 : numberOfBlocks - 1;
                    int step = reverse ? 1 : -1;
                    do
                    {
                        result[responseIndex] = current;
                        responseIndex += step;
                        if (responseIndex < 0)
                        {
                            break;
                        }

                        current = tree.FindParentHeader(current, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    } while (current is not null && responseIndex < numberOfBlocks);

                    return result;
                }

                /* if we do not skip and we have the last block then we can assume that all the blocks are there
                   and we can use the fact that we can use parent hash and that searching by hash is much faster
                   as it does not require the step of resolving number -> hash */
                if (reverse)
                {
                    return FindHeadersReversedFast(this, startHeader, numberOfBlocks, true);
                }
                else
                {
                    BlockHeader endHeader = FindHeader(startHeader.Number + numberOfBlocks - 1, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    if (endHeader is not null)
                    {
                        return FindHeadersReversedFast(this, endHeader, numberOfBlocks);
                    }
                }
            }

            ArrayPoolList<BlockHeader> result = new ArrayPoolList<BlockHeader>(numberOfBlocks, numberOfBlocks);
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

        private BlockHeader? GetAncestorAtNumber(BlockHeader header, long number)
        {
            BlockHeader? result = header;
            while (result is not null && result.Number < number)
            {
                result = this.FindParentHeader(result, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }

            return header;
        }

        private Hash256? GetBlockHashOnMainOrBestDifficultyHash(long blockNumber)
        {
            if (blockNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blockNumber), $"Value must be greater or equal to zero but is {blockNumber}");
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
            Hash256 bestHash = null;
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                BlockInfo current = level.BlockInfos[i];
                if (level.BlockInfos[i].TotalDifficulty >= bestDifficultySoFar)
                {
                    bestDifficultySoFar = current.TotalDifficulty;
                    bestHash = current.BlockHash;
                }
            }

            return bestHash;
        }

        public Block? FindBlock(long blockNumber, BlockTreeLookupOptions options)
        {
            Hash256 hash = GetBlockHashOnMainOrBestDifficultyHash(blockNumber);
            return FindBlock(hash, options, blockNumber: blockNumber);
        }

        public void DeleteInvalidBlock(Block invalidBlock)
        {
            if (invalidBlock.Hash is null)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(DeleteInvalidBlock)} call has been made for a block without a null hash.");
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Deleting invalid block {invalidBlock.ToString(Block.Format.FullHashAndNumber)}");

            _invalidBlocks.Set(invalidBlock.Hash, invalidBlock);
            _badBlockStore.Insert(invalidBlock);

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

        private void DeleteBlocks(Hash256 deletePointer)
        {
            BlockHeader? deleteHeader = FindHeader(deletePointer, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (deleteHeader is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot delete invalid block {deletePointer} - block has not been added to the database or has already been deleted.");
                return;
            }

            if (deleteHeader.Hash is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot delete invalid block {deletePointer} - black has a null hash.");
                return;
            }

            long currentNumber = deleteHeader.Number;
            Hash256 currentHash = deleteHeader.Hash;
            Hash256? nextHash = null;
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
                else if (currentLevel is not null)
                {
                    _chainLevelInfoRepository.PersistLevel(currentNumber, currentLevel, batch);
                }

                if (_logger.IsInfo) _logger.Info($"Deleting invalid block {currentHash} at level {currentNumber}");
                _blockStore.Delete(currentNumber, currentHash);
                _headerStore.Delete(currentHash);

                if (nextHash is null)
                {
                    break;
                }

                currentNumber++;
                currentHash = nextHash;
                nextHash = null;
            }
        }

        private Hash256? FindChild(ChainLevelInfo level, Hash256 parentHash)
        {
            Hash256 childHash = null;
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                Hash256 potentialChildHash = level.BlockInfos[i].BlockHash;
                BlockHeader? potentialChild = FindHeader(potentialChildHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (potentialChild is null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Block with hash {potentialChildHash} has been found on chain level but its header is missing from the DB.");
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

        public bool IsMainChain(BlockHeader blockHeader) =>
            LoadLevel(blockHeader.Number)?.MainChainBlock?.BlockHash.Equals(blockHeader.Hash) == true;

        public bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true)
        {
            BlockHeader? header = FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return header is not null
                ? IsMainChain(header)
                : throwOnMissingHash
                    ? throw new InvalidOperationException($"Not able to retrieve block number for an unknown block {blockHash}")
                    : false;
        }

        public BlockHeader? FindBestSuggestedHeader() => BestSuggestedHeader;

        public bool WasProcessed(long number, Hash256 blockHash)
        {
            ChainLevelInfo? levelInfo = LoadLevel(number) ?? throw new InvalidOperationException($"Not able to find block {blockHash} from an unknown level {number}");
            int? index = levelInfo.FindIndex(blockHash) ?? throw new InvalidOperationException($"Not able to find block {blockHash} index on the chain level");
            return levelInfo.BlockInfos[index.Value].WasProcessed;
        }

        public void MarkChainAsProcessed(IReadOnlyList<Block> blocks)
        {
            if (blocks.Count == 0)
            {
                return;
            }

            using BatchWrite batch = _chainLevelInfoRepository.StartBatch();

            for (int i = 0; i < blocks.Count; i++)
            {
                Block block = blocks[i];
                if (ShouldCache(block.Number))
                {
                    _blockStore.Cache(block);
                    _headerStore.Cache(block.Header);
                }

                ChainLevelInfo? level = LoadLevel(block.Number);
                int? index = (level?.FindIndex(block.Hash)) ?? throw new InvalidOperationException($"Cannot mark unknown block {block.ToString(Block.Format.FullHashAndNumber)} as processed");
                BlockInfo info = level.BlockInfos[index.Value];
                info.WasProcessed = true;
                _chainLevelInfoRepository.PersistLevel(block.Number, level, batch);
            }
        }

        public void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceUpdateHeadBlock = false)
        {
            if (blocks.Count == 0)
            {
                return;
            }

            bool ascendingOrder = true;
            if (blocks.Count > 1)
            {
                if (blocks[^1].Number < blocks[0].Number)
                {
                    ascendingOrder = false;
                }
            }

#if DEBUG
            for (int i = 0; i < blocks.Count; i++)
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

            for (int i = 0; i < blocks.Count; i++)
            {
                Block block = blocks[i];
                if (ShouldCache(block.Number))
                {
                    _blockStore.Cache(block);
                    _headerStore.Cache(block.Header);
                }

                // we only force update head block for last block in processed blocks
                bool lastProcessedBlock = i == blocks.Count - 1;

                // Where head is set if wereProcessed is true
                MoveToMain(blocks[i], batch, wereProcessed, forceUpdateHeadBlock && lastProcessedBlock);
            }

            TryUpdateSyncPivot();

            OnUpdateMainChain?.Invoke(this, new OnUpdateMainChainArgs(blocks, wereProcessed));
        }

        private void TryUpdateSyncPivot()
        {
            BlockHeader? newPivotHeader = null;
            if (FinalizedHash is not null)
            {
                newPivotHeader = FindHeader(FinalizedHash, BlockTreeLookupOptions.RequireCanonical);
            }
            else
            {
                newPivotHeader = FindHeader(Math.Max(0, (Head?.Number ?? 0) - Reorganization.MaxDepth), BlockTreeLookupOptions.RequireCanonical);
            }

            if (newPivotHeader is null)
            {
                if (_logger.IsTrace) _logger.Trace("Did not update sync pivot because unable to find finalized header");
                return;
            }

            long? bestPersisted = BestPersistedState;
            if (bestPersisted is null)
            {
                if (_logger.IsTrace) _logger.Trace("Did not update sync pivot because no best persisted state");
                return;
            }

            if (bestPersisted < newPivotHeader.Number)
            {
                if (_logger.IsTrace) _logger.Trace("Best persisted is lower than sync pivot. Using best persisted stata as pivot.");
                newPivotHeader = FindHeader(bestPersisted.Value, BlockTreeLookupOptions.RequireCanonical);
            }
            if (newPivotHeader is null) return;

            if (SyncPivot.BlockNumber >= newPivotHeader.Number) return;

            (long BlockNumber, Hash256 BlockHash) newSyncPivot = (newPivotHeader.Number, newPivotHeader.Hash);
            SyncPivot = newSyncPivot;
        }

        public void UpdateBeaconMainChain(BlockInfo[]? blockInfos, long clearBeaconMainChainStartPoint)
        {
            if (blockInfos is null || blockInfos.Length == 0)
                return;

            using BatchWrite batch = _chainLevelInfoRepository.StartBatch();

            for (long j = clearBeaconMainChainStartPoint; j > blockInfos[^1].BlockNumber; --j)
            {
                ChainLevelInfo? level = LoadLevel(j);
                if (level is not null)
                {
                    for (int i = 0; i < level.BlockInfos.Length; ++i)
                    {
                        level.BlockInfos[i].Metadata &= ~BlockMetadata.BeaconMainChain;
                    }

                    _chainLevelInfoRepository.PersistLevel(j, level, batch);
                }
            }

            foreach (BlockInfo blockInfo in blockInfos)
            {
                long levelNumber = blockInfo.BlockNumber;
                ChainLevelInfo? level = LoadLevel(levelNumber);
                if (level is not null)
                {
                    for (int i = 0; i < level.BlockInfos.Length; ++i)
                    {
                        if (level.BlockInfos[i].BlockHash == blockInfo.BlockHash)
                        {
                            level.BlockInfos[i].Metadata |= BlockMetadata.BeaconMainChain;
                        }
                        else
                        {
                            level.BlockInfos[i].Metadata &= ~BlockMetadata.BeaconMainChain;
                        }
                    }

                    _chainLevelInfoRepository.PersistLevel(levelNumber, level, batch);
                }
            }
        }

        private (long BlockNumber, Hash256 BlockHash) _syncPivot;
        public (long BlockNumber, Hash256 BlockHash) SyncPivot
        {
            get => _syncPivot;
            set
            {
                if (_logger.IsTrace) _logger.Trace($"Sync pivot updated from {SyncPivot} to {value}");

                RlpStream pivotData = new(38); //1 byte (prefix) + 4 bytes (long) + 1 byte (prefix) + 32 bytes (Keccak)
                pivotData.Encode(value.BlockNumber);
                pivotData.Encode(value.BlockHash);
                _metadataDb.Set(MetadataDbKeys.UpdatedPivotData, pivotData.Data.ToArray()!);
                _syncPivot = value;
            }
        }


        public bool IsBetterThanHead(BlockHeader? header) =>
            header is not null // null is never better
            && ((header.IsGenesis && Genesis is null) // is genesis
                || header.TotalDifficulty >= _specProvider.TerminalTotalDifficulty // is post-merge block, we follow engine API
                || header.TotalDifficulty > (Head?.TotalDifficulty ?? 0) // pre-merge rules
                || (header.TotalDifficulty == Head?.TotalDifficulty // when in doubt on difficulty
                    && ((Head?.Number ?? 0L).CompareTo(header.Number) > 0 // pick longer chain
                        || (Head?.Hash ?? Keccak.Zero).CompareTo(header.Hash) > 0))); // or have a deterministic order on hash


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
            if (_logger.IsTrace) _logger.Trace($"Moving {block.ToString(Block.Format.Short)} to main");
            if (block.Hash is null)
            {
                throw new InvalidOperationException("An attempt to move to main a block with hash not set.");
            }

            if (block.Bloom is null)
            {
                throw new InvalidOperationException("An attempt to move to main a block with bloom not set.");
            }

            ChainLevelInfo? level = LoadLevel(block.Number);
            int? index = (level?.FindIndex(block.Hash)) ?? throw new InvalidOperationException($"Cannot move unknown block {block.ToString(Block.Format.FullHashAndNumber)} to main");
            Hash256 hashOfThePreviousMainBlock = level.MainChainBlock?.BlockHash;

            BlockInfo info = level.BlockInfos[index.Value];
            info.WasProcessed = wasProcessed;
            if (index.Value != 0)
            {
                level.SwapToMain(index.Value);
            }

            _bloomStorage.Store(block.Number, block.Bloom);
            level.HasBlockOnMainChain = true;
            _chainLevelInfoRepository.PersistLevel(block.Number, level, batch);

            Block previous = hashOfThePreviousMainBlock is not null && hashOfThePreviousMainBlock != block.Hash
                ? FindBlock(hashOfThePreviousMainBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded, blockNumber: block.Number)
                : null;

            if (forceUpdateHeadBlock || block.IsGenesis || HeadImprovementRequirementsSatisfied(block.Header))
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

            if (_logger.IsTrace) _logger.Trace($"Block added to main {block}, block TD {block.TotalDifficulty}");

            BlockAddedToMain?.Invoke(this, new BlockReplacementEventArgs(block, previous));

            if (_logger.IsTrace) _logger.Trace($"Block {block.ToString(Block.Format.Short)}, TD: {block.TotalDifficulty} added to main chain");
        }

        private bool HeadImprovementRequirementsSatisfied(BlockHeader header)
        {
            // before merge TD requirements are satisfied only if TD > block head
            bool preMergeImprovementRequirementSatisfied = header.TotalDifficulty > (Head?.TotalDifficulty ?? 0)
                                                           && (header.TotalDifficulty <
                                                               _specProvider.TerminalTotalDifficulty
                                                               || _specProvider.TerminalTotalDifficulty is null);

            // after the merge, we will accept only the blocks with Difficulty = 0. However, during the transition process
            // we can have terminal PoW blocks with Difficulty > 0. That is why we accept everything greater or equal
            // than current head and header.TD >= TTD.
            bool postMergeImprovementRequirementSatisfied = _specProvider.TerminalTotalDifficulty is not null &&
                                                            header.TotalDifficulty >=
                                                            _specProvider.TerminalTotalDifficulty;
            return preMergeImprovementRequirementSatisfied || postMergeImprovementRequirementSatisfied;
        }

        private bool BestSuggestedImprovementRequirementsSatisfied(BlockHeader header)
        {
            if (BestSuggestedHeader is null) return true;

            bool reachedTtd = header.IsPostTTD(_specProvider);
            bool isPostMerge = header.IsPoS();
            bool tdImproved = header.TotalDifficulty > (BestSuggestedBody?.TotalDifficulty ?? 0);
            bool preMergeImprovementRequirementSatisfied = tdImproved && !reachedTtd;
            bool terminalBlockRequirementSatisfied = tdImproved && reachedTtd && header.IsTerminalBlock(_specProvider) && !Head.IsPoS();
            bool postMergeImprovementRequirementSatisfied = reachedTtd && (BestSuggestedBody?.Number ?? 0) <= header.Number && isPostMerge;

            return preMergeImprovementRequirementSatisfied || terminalBlockRequirementSatisfied || postMergeImprovementRequirementSatisfied;
        }

        public bool IsKnownBlock(long number, Hash256 blockHash)
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

            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(number, blockHash, false);
            if (level is null || blockInfo is null) return false;
            return !blockInfo.IsBeaconInfo;
        }

        public bool IsKnownBeaconBlock(long number, Hash256 blockHash)
        {
            if (number > BestKnownBeaconNumber)
            {
                return false;
            }

            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(number, blockHash, true);
            if (level is null || blockInfo is null) return false;
            return blockInfo.IsBeaconInfo;
        }

        private void UpdateDeletePointer(Hash256? hash)
        {
            if (hash is null)
            {
                _blockInfoDb.Delete(DeletePointerAddressInDb);
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Deleting an invalid block or its descendant {hash}");
                _blockInfoDb.Set(DeletePointerAddressInDb, hash.Bytes);
            }
        }

        public void UpdateHeadBlock(Hash256 blockHash)
        {
            if (_logger.IsError) _logger.Error($"Block tree override detected - updating head block to {blockHash}.");
            _blockInfoDb.Set(HeadAddressInDb, blockHash.Bytes);
            BlockHeader? header = FindHeader(blockHash, BlockTreeLookupOptions.None);
            if (header is not null)
            {
                if (_logger.IsError) _logger.Error($"Block tree override detected - updating head block to {blockHash}.");
                _blockInfoDb.Set(HeadAddressInDb, blockHash.Bytes);
                BestPersistedState = header.Number;
            }
            else
            {
                if (_logger.IsError) _logger.Error($"Block tree override detected - cannot find block: {blockHash}.");
            }
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
            using BatchWrite? batch = _chainLevelInfoRepository.StartBatch();

            if (!blockInfo.IsBeaconInfo && number > BestKnownNumber)
            {
                BestKnownNumber = number;
            }

            ChainLevelInfo level = LoadLevel(number, false);

            if (level is not null)
            {
                level.InsertBlockInfo(blockInfo.BlockHash, blockInfo, setAsMain);
            }
            else
            {
                level = new ChainLevelInfo(false, new[] { blockInfo });
            }

            if (setAsMain)
            {
                level.HasBlockOnMainChain = true;
            }

            _chainLevelInfoRepository.PersistLevel(number, level, batch);

            return level;
        }

        private void UpdateOrCreateLevel(IReadOnlyList<(long number, BlockInfo blockInfo)> blockInfos, bool setAsMain = false)
        {
            using BatchWrite? batch = _chainLevelInfoRepository.StartBatch();

            using ArrayPoolList<long> blockNumbers = blockInfos.Select(b => b.number).ToPooledList(blockInfos.Count);

            // Yes, this is measurably faster
            using IOwnedReadOnlyList<ChainLevelInfo?> levels = _chainLevelInfoRepository.MultiLoadLevel(blockNumbers);

            for (var i = 0; i < blockInfos.Count; i++)
            {
                (long number, BlockInfo blockInfo) = blockInfos[i];

                if (!blockInfo.IsBeaconInfo && number > BestKnownNumber)
                {
                    BestKnownNumber = number;
                }

                ChainLevelInfo? level = levels[i];

                if (level is not null)
                {
                    level.InsertBlockInfo(blockInfo.BlockHash, blockInfo, setAsMain);
                }
                else
                {
                    level = new ChainLevelInfo(false, new[] { blockInfo });
                }

                if (setAsMain)
                {
                    level.HasBlockOnMainChain = true;
                }

                _chainLevelInfoRepository.PersistLevel(number, level, batch);
            }
        }

        public (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Hash256 blockHash) => LoadInfo(number, blockHash, true);

        private (BlockInfo? Info, ChainLevelInfo? Level) LoadInfo(long number, Hash256 blockHash, bool forceLoad)
        {
            ChainLevelInfo chainLevelInfo = LoadLevel(number, forceLoad);
            if (chainLevelInfo is null)
            {
                return (null, null);
            }

            return (chainLevelInfo.FindBlockInfo(blockHash), chainLevelInfo);
        }

        private ChainLevelInfo? LoadLevel(long number, bool forceLoad = true)
        {
            if (number > Math.Max(BestKnownNumber, BestKnownBeaconNumber) && !forceLoad)
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
            return number == 0L || Head is null || number >= Head.Number - BlockStore.CacheSize;
        }

        public ChainLevelInfo? FindLevel(long number)
        {
            return _chainLevelInfoRepository.LoadLevel(number);
        }

        public Hash256? HeadHash => Head?.Hash;
        public Hash256? GenesisHash => Genesis?.Hash;
        public Hash256? PendingHash => Head?.Hash;
        public Hash256? FinalizedHash { get; private set; }
        public Hash256? SafeHash { get; private set; }

        public Block? FindBlock(Hash256? blockHash, BlockTreeLookupOptions options, long? blockNumber = null)
        {
            if (blockHash is null || blockHash == Keccak.Zero)
            {
                return null;
            }

            Block? block = null;
            blockNumber ??= _headerStore.GetBlockNumber(blockHash);
            if (blockNumber is not null)
            {
                block = _blockStore.Get(
                    blockNumber.Value,
                    blockHash,
                    (options & BlockTreeLookupOptions.ExcludeTxHashes) != 0 ? RlpBehaviors.ExcludeHashes : RlpBehaviors.None,
                    shouldCache: false);
            }

            if (block is null)
            {
                bool allowInvalid = (options & BlockTreeLookupOptions.AllowInvalid) == BlockTreeLookupOptions.AllowInvalid;
                if (allowInvalid)
                {
                    _invalidBlocks.TryGet(blockHash, out block);
                }

                return block;
            }

            bool totalDifficultyNeeded = (options & BlockTreeLookupOptions.TotalDifficultyNotNeeded) ==
                                         BlockTreeLookupOptions.None;
            bool createLevelIfMissing = (options & BlockTreeLookupOptions.DoNotCreateLevelIfMissing) ==
                                        BlockTreeLookupOptions.None;
            bool requiresCanonical = (options & BlockTreeLookupOptions.RequireCanonical) ==
                                     BlockTreeLookupOptions.RequireCanonical;

            if ((totalDifficultyNeeded && block.TotalDifficulty is null) || requiresCanonical)
            {
                (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(block.Number, block.Hash, true);
                if (level is null || blockInfo is null)
                {
                    // TODO: this is here because storing block data is not transactional
                    // TODO: would be great to remove it, he?
                    // TODO: we should remove it - readonly method modifies DB
                    bool isSearchingForBeaconBlock = (BestKnownBeaconNumber > BestKnownNumber && block.Number > BestKnownNumber);  // if we're searching for beacon block we don't want to create level. We're creating it in different place with beacon metadata
                    if (createLevelIfMissing == false || isSearchingForBeaconBlock)
                    {
                        if (_logger.IsInfo) _logger.Info($"Missing block info - ignoring creation of the level in {nameof(FindBlock)} scope when head is {Head?.ToString(Block.Format.Short)}. BlockHeader {block.ToString(Block.Format.FullHashAndNumber)}, CreateLevelIfMissing: {createLevelIfMissing}. BestKnownBeaconNumber: {BestKnownBeaconNumber}, BestKnownNumber: {BestKnownNumber}");
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"Missing block info - creating level in {nameof(FindBlock)} scope when head is {Head?.ToString(Block.Format.Short)}. BlockHeader {block.ToString(Block.Format.FullHashAndNumber)}, CreateLevelIfMissing: {createLevelIfMissing}. BestKnownBeaconNumber: {BestKnownBeaconNumber}, BestKnownNumber: {BestKnownNumber}");
                        SetTotalDifficulty(block.Header);
                        blockInfo = new BlockInfo(block.Hash, block.TotalDifficulty ?? UInt256.Zero);
                        level = UpdateOrCreateLevel(block.Number, blockInfo);
                    }
                }
                else
                {
                    SetTotalDifficultyFromBlockInfo(block.Header, blockInfo);
                }

                if (requiresCanonical)
                {
                    bool isMain = level.MainChainBlock?.BlockHash.Equals(blockHash) == true;
                    block = isMain ? block : null;
                }
            }

            if (block is not null && ShouldCache(block.Number))
            {
                _blockStore.Cache(block);
                _headerStore.Cache(block.Header);
            }

            return block;
        }

        private bool IsTotalDifficultyAlwaysZero()
        {
            // In some Ethereum tests and possible testnets difficulty of all blocks might be zero
            // We also checking TTD is zero to ensure that block after genesis have zero difficulty
            return Genesis?.Difficulty == 0 && _specProvider.TerminalTotalDifficulty == 0;
        }

        private void SetTotalDifficultyFromBlockInfo(BlockHeader header, BlockInfo blockInfo)
        {
            if (header.IsGenesis)
            {
                header.TotalDifficulty = header.Difficulty;
                return;
            }

            if (blockInfo.TotalDifficulty != UInt256.Zero)
            {
                header.TotalDifficulty = blockInfo.TotalDifficulty;
                return;
            }

            if (IsTotalDifficultyAlwaysZero())
            {
                header.TotalDifficulty = 0;
            }
        }

        private void SetTotalDifficulty(BlockHeader header)
        {
            if (header.IsGenesis)
            {
                header.TotalDifficulty = header.Difficulty;
                if (_logger.IsTrace) _logger.Trace($"Genesis total difficulty is {header.TotalDifficulty}");
                return;
            }

            if (IsTotalDifficultyAlwaysZero())
            {
                header.TotalDifficulty = 0;
                if (_logger.IsTrace) _logger.Trace($"Block {header} has zero total difficulty");
                return;
            }
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
                while (!current.IsGenesis && !current.IsNonZeroTotalDifficulty())
                {
                    (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(current.Number, current.Hash, true);
                    if (level is null || blockInfo is null || blockInfo.TotalDifficulty == 0)
                    {
                        stack.Push(current);
                        if (_logger.IsTrace)
                            _logger.Trace(
                                $"Calculating total difficulty for {current.ToString(BlockHeader.Format.Short)}");
                        current = GetParentHeader(current);
                    }
                    else
                    {
                        current.TotalDifficulty = blockInfo.TotalDifficulty;
                    }
                }

                if (current.IsGenesis)
                {
                    current.TotalDifficulty = current.Difficulty;
                    BlockInfo blockInfo = new(current.Hash, current.Difficulty);
                    blockInfo.WasProcessed = true;
                    UpdateOrCreateLevel(current.Number, blockInfo);
                }

                while (stack.TryPop(out BlockHeader child))
                {
                    child.TotalDifficulty = current.TotalDifficulty + child.Difficulty;
                    BlockInfo blockInfo = new(child.Hash, child.TotalDifficulty.Value);
                    UpdateOrCreateLevel(child.Number, blockInfo);
                    if (_logger.IsTrace)
                        _logger.Trace($"Calculated total difficulty for {child} is {child.TotalDifficulty}");
                    current = child;
                }
            }

            if (header.IsNonZeroTotalDifficulty())
            {
                return;
            }

            if (_logger.IsTrace)
                _logger.Trace($"Calculating total difficulty for {header.ToString(BlockHeader.Format.Short)}");


            BlockHeader parentHeader = GetParentHeader(header);

            if (!parentHeader.IsNonZeroTotalDifficulty())
            {
                SetTotalDifficultyDeep(parentHeader);
            }

            header.TotalDifficulty = parentHeader.TotalDifficulty + header.Difficulty;

            if (_logger.IsTrace) _logger.Trace($"Calculated total difficulty for {header} is {header.TotalDifficulty}");
        }

        public event EventHandler<BlockReplacementEventArgs>? BlockAddedToMain;

        public event EventHandler<OnUpdateMainChainArgs>? OnUpdateMainChain;

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
        /// <param name="force">Should it force of deletion of valid blocks</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="startNumber"/> ot <paramref name="endNumber"/> do not satisfy the slice position rules</exception>
        public int DeleteChainSlice(in long startNumber, long? endNumber = null, bool force = false)
        {
            int deleted = 0;
            endNumber ??= BestKnownNumber;

            if (endNumber - startNumber < 0)
            {
                throw new ArgumentException("Start number must be equal or greater end number.", nameof(startNumber));
            }

            if (endNumber - startNumber > 50000)
            {
                throw new ArgumentException(
                    $"Cannot delete that many blocks at once (start: {startNumber}, end {endNumber}).",
                    nameof(startNumber));
            }

            if (startNumber < 1)
            {
                throw new ArgumentException("Start number must be strictly greater than 0", nameof(startNumber));
            }

            Block? newHeadBlock = null;

            // we are running these checks before all the deletes
            if (Head?.Number >= startNumber)
            {
                // greater than zero so will not fail
                ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(startNumber - 1) ?? throw new InvalidDataException(
                        $"Chain level {startNumber - 1} does not exist when {startNumber} level exists.");

                // there may be no canonical block marked on this level - then we just hack to genesis
                Hash256? newHeadHash = chainLevelInfo.HasBlockOnMainChain
                    ? chainLevelInfo.BlockInfos[0].BlockHash
                    : Genesis?.Hash;
                newHeadBlock = newHeadHash is null ? null : FindBlock(newHeadHash, BlockTreeLookupOptions.None, blockNumber: startNumber - 1);
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
                        Hash256 blockHash = blockInfo.BlockHash;
                        _blockInfoDb.Delete(blockHash);
                        _blockStore.Delete(i, blockHash);
                        _headerStore.Delete(blockHash);
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
            if (CanAcceptNewBlocks)
            {
                _taskCompletionSource = new TaskCompletionSource<bool>();
            }

            Interlocked.Increment(ref _canAcceptNewBlocksCounter);
        }

        internal void ReleaseAcceptingNewBlocks()
        {
            Interlocked.Decrement(ref _canAcceptNewBlocksCounter);
            if (CanAcceptNewBlocks)
            {
                _taskCompletionSource.SetResult(true);
                _taskCompletionSource = null;
            }
        }

        private Task WaitForReadinessToAcceptNewBlock => _taskCompletionSource?.Task ?? Task.CompletedTask;

        /// <inheritdoc />
        public long? BestPersistedState
        {
            get => _highestPersistedState;
            set
            {
                _highestPersistedState = value;
                if (value.HasValue)
                {
                    _blockInfoDb.Set(StateHeadHashDbEntryAddress, Rlp.Encode(value.Value).Bytes);
                }

                TryUpdateSyncPivot();
            }
        }

        public void ForkChoiceUpdated(Hash256? finalizedBlockHash, Hash256? safeBlockHash)
        {
            FinalizedHash = finalizedBlockHash;
            SafeHash = safeBlockHash;
            using (_metadataDb.StartWriteBatch())
            {
                _metadataDb.Set(MetadataDbKeys.FinalizedBlockHash, Rlp.Encode(FinalizedHash!).Bytes);
                _metadataDb.Set(MetadataDbKeys.SafeBlockHash, Rlp.Encode(SafeHash!).Bytes);
            }
            TryUpdateSyncPivot();
        }

        public long GetLowestBlock()
        {
            return _syncConfig.AncientReceiptsBarrierCalc < _syncConfig.AncientBodiesBarrierCalc ? _syncConfig.AncientReceiptsBarrierCalc : _syncConfig.AncientBodiesBarrierCalc;
        }
    }
}
