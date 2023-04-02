// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
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

        private readonly LruCache<ValueKeccak, Block>
            _blockCache = new(CacheSize, CacheSize, "blocks");

        private readonly LruCache<ValueKeccak, BlockHeader> _headerCache =
            new(CacheSize, CacheSize, "headers");

        private const int BestKnownSearchLimit = 256_000_000;

        private readonly object _batchInsertLock = new();

        private readonly IDb _blockDb;
        private readonly IDb _headerDb;
        private readonly IDb _blockInfoDb;
        private readonly IDb _metadataDb;

        private readonly LruCache<ValueKeccak, Block> _invalidBlocks =
            new(128, 128, "invalid blocks");

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

        private long? _lowestInsertedReceiptBlock;
        private long? _highestPersistedState;

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

        public long BestKnownBeaconNumber { get; private set; }

        public ulong NetworkId => _specProvider.NetworkId;

        public ulong ChainId => _specProvider.ChainId;

        private int _canAcceptNewBlocksCounter;
        public bool CanAcceptNewBlocks => _canAcceptNewBlocksCounter == 0;

        private TaskCompletionSource<bool>? _taskCompletionSource;

        public BlockTree(
            IDbProvider? dbProvider,
            IChainLevelInfoRepository? chainLevelInfoRepository,
            ISpecProvider? specProvider,
            IBloomStorage? bloomStorage,
            ILogManager? logManager)
            : this(dbProvider?.BlocksDb, dbProvider?.HeadersDb, dbProvider?.BlockInfosDb, dbProvider?.MetadataDb,
                chainLevelInfoRepository, specProvider, bloomStorage, new SyncConfig(), logManager)
        {
        }

        public BlockTree(
            IDbProvider? dbProvider,
            IChainLevelInfoRepository? chainLevelInfoRepository,
            ISpecProvider? specProvider,
            IBloomStorage? bloomStorage,
            ISyncConfig? syncConfig,
            ILogManager? logManager)
            : this(dbProvider?.BlocksDb, dbProvider?.HeadersDb, dbProvider?.BlockInfosDb, dbProvider?.MetadataDb,
                chainLevelInfoRepository, specProvider, bloomStorage, syncConfig, logManager)
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
            : this(blockDb, headerDb, blockInfoDb, new MemDb(), chainLevelInfoRepository, specProvider, bloomStorage,
                new SyncConfig(), logManager)
        {
        }

        public BlockTree(
            IDb? blockDb,
            IDb? headerDb,
            IDb? blockInfoDb,
            IDb? metadataDb,
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
            _metadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _bloomStorage = bloomStorage ?? throw new ArgumentNullException(nameof(bloomStorage));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _chainLevelInfoRepository = chainLevelInfoRepository ??
                                        throw new ArgumentNullException(nameof(chainLevelInfoRepository));

            byte[]? deletePointer = _blockInfoDb.Get(DeletePointerAddressInDb);
            if (deletePointer is not null)
            {
                DeleteBlocks(new Keccak(deletePointer));
            }

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
                             $"body {LowestInsertedBodyNumber}, " +
                             $"lowest sync inserted block number {LowestInsertedBeaconHeader?.Number}");
            ThisNodeInfo.AddInfo("Chain ID     :", $"{(ChainId == NetworkId ? Core.BlockchainIds.GetBlockchainName(NetworkId) : ChainId)}");
            ThisNodeInfo.AddInfo("Chain head   :", $"{Head?.Header.ToString(BlockHeader.Format.Short) ?? "0"}");
            if (ChainId != NetworkId)
            {
                ThisNodeInfo.AddInfo("Network ID   :", $"{NetworkId}");
            }
        }

        private void AttemptToFixCorruptionByMovingHeadBackwards()
        {
            if (_tryToRecoverFromHeaderBelowBodyCorruption && BestSuggestedHeader is not null)
            {
                ChainLevelInfo chainLevelInfo = LoadLevel(BestSuggestedHeader.Number);
                BlockInfo? canonicalBlock = chainLevelInfo?.MainChainBlock;
                if (canonicalBlock is not null && canonicalBlock.WasProcessed)
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
            LoadLowestInsertedBodyNumber();
            LoadLowestInsertedHeader();
            LoadLowestInsertedBeaconHeader();
            LoadBestKnown();
            LoadBeaconBestKnown();
        }

        private void LoadLowestInsertedBodyNumber()
        {
            LowestInsertedBodyNumber =
                _blockDb.Get(LowestInsertedBodyNumberDbEntryAddress)?
                    .AsRlpValueContext().DecodeLong();
        }

        public void LoadLowestInsertedBeaconHeader()
        {
            if (_metadataDb.KeyExists(MetadataDbKeys.LowestInsertedBeaconHeaderHash))
            {
                Keccak? lowestBeaconHeaderHash = _metadataDb.Get(MetadataDbKeys.LowestInsertedBeaconHeaderHash)?
                    .AsRlpStream().DecodeKeccak();
                _lowestInsertedBeaconHeader = FindHeader(lowestBeaconHeaderHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }
        }

        private void LoadLowestInsertedHeader()
        {
            long left = 1L;
            long right = _syncConfig.PivotNumberParsed;

            LowestInsertedHeader = BinarySearchBlockHeader(left, right, LevelExists, BinarySearchDirection.Down);
        }

        private bool LevelExists(long blockNumber, bool findBeacon = false)
        {
            ChainLevelInfo? level = LoadLevel(blockNumber);
            if (findBeacon)
            {
                return level is not null && level.HasBeaconBlocks;
            }

            return level is not null && level.HasNonBeaconBlocks;
        }

        private bool HeaderExists(long blockNumber, bool findBeacon = false)
        {
            ChainLevelInfo level = LoadLevel(blockNumber);
            if (level is null)
            {
                return false;
            }

            foreach (BlockInfo blockInfo in level.BlockInfos)
            {
                BlockHeader? header = FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None);
                if (header is not null)
                {
                    if (findBeacon && blockInfo.IsBeaconHeader)
                    {
                        return true;
                    }

                    if (!findBeacon && !blockInfo.IsBeaconHeader)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool BodyExists(long blockNumber, bool findBeacon = false)
        {
            ChainLevelInfo level = LoadLevel(blockNumber);
            if (level is null)
            {
                return false;
            }

            foreach (BlockInfo blockInfo in level.BlockInfos)
            {
                Block? block = FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None);
                if (block is not null)
                {
                    if (findBeacon && blockInfo.IsBeaconBody)
                    {
                        return true;
                    }

                    if (!findBeacon && !blockInfo.IsBeaconBody)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void LoadBestKnown()
        {
            long left = (Head?.Number ?? 0) == 0
                ? Math.Max(_syncConfig.PivotNumberParsed, LowestInsertedHeader?.Number ?? 0) - 1
                : Head.Number;

            long right = Math.Max(0, left) + BestKnownSearchLimit;

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

            bestKnownNumberFound = Math.Max(bestKnownNumberFound, bestKnownNumberAlternative);
            bestSuggestedHeaderNumber = Math.Max(bestSuggestedHeaderNumber, bestSuggestedHeaderNumberAlternative);
            bestSuggestedBodyNumber = Math.Max(bestSuggestedBodyNumber, bestSuggestedBodyNumberAlternative);

            if (bestKnownNumberFound < 0 ||
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
                                                   $"best known: {bestKnownNumberFound}|" +
                                                   $"best header: {bestSuggestedHeaderNumber}|" +
                                                   $"best body: {bestSuggestedBodyNumber}|");
                }
            }

            BestKnownNumber = Math.Max(bestKnownNumberFound, bestKnownNumberAlternative);
            BestSuggestedHeader = FindHeader(bestSuggestedHeaderNumber, BlockTreeLookupOptions.None);
            BlockHeader? bestSuggestedBodyHeader = FindHeader(bestSuggestedBodyNumber, BlockTreeLookupOptions.None);
            BestSuggestedBody = bestSuggestedBodyHeader is null
                ? null
                : FindBlock(bestSuggestedBodyHeader.Hash, BlockTreeLookupOptions.None);
        }

        private void LoadBeaconBestKnown()
        {
            long left = Math.Max(Head?.Number ?? 0, LowestInsertedBeaconHeader?.Number ?? 0) - 1;
            long right = Math.Max(0, left) + BestKnownSearchLimit;
            long bestKnownNumberFound = BinarySearchBlockNumber(left, right, LevelExists, findBeacon: true) ?? 0;

            left = Math.Max(
                Math.Max(
                    Head?.Number ?? 0,
                    LowestInsertedBeaconHeader?.Number ?? 0),
                BestSuggestedHeader?.Number ?? 0
                ) - 1;

            right = Math.Max(0, left) + BestKnownSearchLimit;
            long bestBeaconHeaderNumber = BinarySearchBlockNumber(left, right, HeaderExists, findBeacon: true) ?? 0;

            long? beaconPivotNumber = _metadataDb.Get(MetadataDbKeys.BeaconSyncPivotNumber)?.AsRlpValueContext().DecodeLong();
            left = Math.Max(Head?.Number ?? 0, beaconPivotNumber ?? 0) - 1;
            right = Math.Max(0, left) + BestKnownSearchLimit;
            long bestBeaconBodyNumber = BinarySearchBlockNumber(left, right, BodyExists, findBeacon: true) ?? 0;

            if (_logger.IsInfo)
                _logger.Info("Beacon Numbers resolved, " +
                             $"level = {bestKnownNumberFound}, " +
                             $"header = {bestBeaconHeaderNumber}, " +
                             $"body = {bestBeaconBodyNumber}");

            if (bestKnownNumberFound < 0 ||
                bestBeaconHeaderNumber < 0 ||
                bestBeaconBodyNumber < 0 ||
                bestBeaconHeaderNumber < bestBeaconBodyNumber)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Detected corrupted block tree data ({bestBeaconHeaderNumber} < {bestBeaconBodyNumber}) (possibly due to an unexpected shutdown). Attempting to fix by moving head backwards. This may fail and you may need to resync the node.");
                if (bestBeaconHeaderNumber < bestBeaconBodyNumber)
                {
                    bestBeaconBodyNumber = bestBeaconHeaderNumber;
                    _tryToRecoverFromHeaderBelowBodyCorruption = true;
                }
                else
                {
                    throw new InvalidDataException("Invalid initial block tree state loaded - " +
                                                   $"best known: {bestKnownNumberFound}|" +
                                                   $"best header: {bestBeaconHeaderNumber}|" +
                                                   $"best body: {bestBeaconBodyNumber}|");
                }
            }

            BestKnownBeaconNumber = bestKnownNumberFound;
            BestSuggestedBeaconHeader = FindHeader(bestBeaconHeaderNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            BlockHeader? bestBeaconBodyHeader = FindHeader(bestBeaconBodyNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            BestSuggestedBeaconBody = bestBeaconBodyHeader is null
                ? null
                : FindBlock(bestBeaconBodyHeader.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        private enum BinarySearchDirection
        {
            Up,
            Down
        }

        private BlockHeader? BinarySearchBlockHeader(long left, long right, Func<long, bool, bool> isBlockFound,
            BinarySearchDirection direction = BinarySearchDirection.Up)
        {
            long? blockNumber = BinarySearchBlockNumber(left, right, isBlockFound, direction);
            if (blockNumber.HasValue)
            {
                ChainLevelInfo? level = LoadLevel(blockNumber.Value);
                if (level is null)
                {
                    throw new InvalidDataException(
                        $"Missing chain level at number {blockNumber.Value}");
                }

                BlockInfo blockInfo = level.BlockInfos[0];
                return FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None);
            }

            return null;
        }

        private static long? BinarySearchBlockNumber(long left, long right, Func<long, bool, bool> isBlockFound,
            BinarySearchDirection direction = BinarySearchDirection.Up, bool findBeacon = false)
        {
            if (left > right)
            {
                return null;
            }

            long? result = null;
            while (left != right)
            {
                long index = direction == BinarySearchDirection.Up
                    ? left + (right - left) / 2
                    : right - (right - left) / 2;
                if (isBlockFound(index, findBeacon))
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

            if (isBlockFound(left, findBeacon))
            {
                result = direction == BinarySearchDirection.Up ? left : right;
            }

            return result;
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

            // validate hash here
            // using previously received header RLPs would allows us to save 2GB allocations on a sample
            // 3M Goerli blocks fast sync
            using (NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header))
            {
                _headerDb.Set(header.Hash, newRlp.AsSpan());
            }

            bool isOnMainChain = (headerOptions & BlockTreeInsertHeaderOptions.NotOnMainChain) == 0;
            BlockInfo blockInfo = new(header.Hash, header.TotalDifficulty ?? 0);

            if (header.Number < (LowestInsertedHeader?.Number ?? long.MaxValue))
            {
                LowestInsertedHeader = header;
            }

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

            UpdateOrCreateLevel(header.Number, header.Hash, blockInfo, isOnMainChain);

            return AddBlockResult.Added;
        }

        public AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
            BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None)
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
            using (NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block))
            {
                _blockDb.Set(block.Hash, newRlp.AsSpan());
            }

            bool saveHeader = (insertBlockOptions & BlockTreeInsertBlockOptions.SaveHeader) != 0;
            if (saveHeader)
            {
                Insert(block.Header, insertHeaderOptions);
            }

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

                using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);
                _blockDb.Set(block.Hash, newRlp.AsSpan());
            }

            if (!isKnown)
            {
                using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
                _headerDb.Set(header.Hash, newRlp.AsSpan());
            }

            if (!isKnown || fillBeaconBlock)
            {
                BlockInfo blockInfo = new(header.Hash, header.TotalDifficulty ?? 0);
                UpdateOrCreateLevel(header.Number, header.Hash, blockInfo, setAsMain);
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

            BlockHeader? header = _headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: false);
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
                        level = UpdateOrCreateLevel(header.Number, header.Hash, blockInfo);
                    }
                }
                else
                {
                    if (blockInfo.TotalDifficulty != UInt256.Zero || header.IsGenesis)
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
                return new[] { startHeader };
            }

            if (skip == 0)
            {
                static BlockHeader[] FindHeadersReversedFast(BlockTree tree, BlockHeader startHeader, int numberOfBlocks, bool reverse = false)
                {
                    if (startHeader is null) throw new ArgumentNullException(nameof(startHeader));
                    if (numberOfBlocks == 1)
                    {
                        return new[] { startHeader };
                    }

                    BlockHeader[] result = new BlockHeader[numberOfBlocks];

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

        public BlockHeader? FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant,
            long maxSearchDepth)
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

            _invalidBlocks.Set(invalidBlock.Hash, invalidBlock);

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
                if (_logger.IsWarn) _logger.Warn($"Cannot delete invalid block {deletePointer} - block has not been added to the database or has already been deleted.");
                return;
            }

            if (deleteHeader.Hash is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot delete invalid block {deletePointer} - black has a null hash.");
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
                else if (currentLevel is not null)
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

        public bool IsMainChain(BlockHeader blockHeader)
        {
            ChainLevelInfo? chainLevelInfo = LoadLevel(blockHeader.Number);
            bool isMain = chainLevelInfo is not null && chainLevelInfo.MainChainBlock?.BlockHash.Equals(blockHeader.Hash) == true;
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
                    _blockCache.Set(block.Hash, blocks[i]);
                    _headerCache.Set(block.Hash, block.Header);
                }

                ChainLevelInfo? level = LoadLevel(block.Number);
                int? index = level is null ? null : FindIndex(block.Hash, level);
                if (index is null)
                {
                    throw new InvalidOperationException($"Cannot mark unknown block {block.ToString(Block.Format.FullHashAndNumber)} as processed");
                }

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
                    _blockCache.Set(block.Hash, blocks[i]);
                    _headerCache.Set(block.Hash, block.Header);
                }

                // we only force update head block for last block in processed blocks
                bool lastProcessedBlock = i == blocks.Count - 1;
                MoveToMain(blocks[i], batch, wereProcessed, forceUpdateHeadBlock && lastProcessedBlock);
            }

            OnUpdateMainChain?.Invoke(this, new OnUpdateMainChainArgs(blocks, wereProcessed));
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

        public bool IsBetterThanHead(BlockHeader? header)
        {
            bool result = false;
            if (header is not null)
            {
                if (header.IsGenesis && Genesis is null)
                {
                    result = true;
                }
                else
                {
                    result = header.TotalDifficulty > (Head?.TotalDifficulty ?? 0)
                             // so above is better and more correct but creates an impression of the node staying behind on stats page
                             // so we are okay to process slightly more
                             // and below is less correct but potentially reporting well
                             // || totalDifficulty >= (_blockTree.Head?.TotalDifficulty ?? 0)
                             // below are some new conditions under test
                             || (header.TotalDifficulty == Head?.TotalDifficulty &&
                                 ((Head?.Hash ?? Keccak.Zero).CompareTo(header.Hash) > 0))
                             || (header.TotalDifficulty == Head?.TotalDifficulty &&
                                 ((Head?.Number ?? 0L).CompareTo(header.Number) > 0))
                             || (header.TotalDifficulty >= _specProvider.TerminalTotalDifficulty);
                }
            }

            return result;
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
            if (level.BlockInfos.Length > 1)
            {
            }

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
                (level.BlockInfos[index.Value], level.BlockInfos[0]) =
                    (level.BlockInfos[0], level.BlockInfos[index.Value]);
            }

            _bloomStorage.Store(block.Number, block.Bloom);
            level.HasBlockOnMainChain = true;
            _chainLevelInfoRepository.PersistLevel(block.Number, level, batch);

            Block previous = hashOfThePreviousMainBlock is not null && hashOfThePreviousMainBlock != block.Hash
                ? FindBlock(hashOfThePreviousMainBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded)
                : null;

            if (_logger.IsTrace) _logger.Trace($"Block added to main {block}, block TD {block.TotalDifficulty}");
            BlockAddedToMain?.Invoke(this, new BlockReplacementEventArgs(block, previous));

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

        private void LoadStartBlock()
        {
            Block? startBlock = null;
            byte[] persistedNumberData = _blockInfoDb.Get(StateHeadHashDbEntryAddress);
            BestPersistedState = persistedNumberData is null ? null : new RlpStream(persistedNumberData).DecodeLong();
            long? persistedNumber = BestPersistedState;
            if (persistedNumber is not null)
            {
                startBlock = FindBlock(persistedNumber.Value, BlockTreeLookupOptions.None);
                if (_logger.IsInfo) _logger.Info(
                    $"Start block loaded from reorg boundary - {persistedNumber} - {startBlock?.ToString(Block.Format.Short)}");
            }
            else
            {
                byte[] data = _blockInfoDb.Get(HeadAddressInDb);
                if (data is not null)
                {
                    startBlock = FindBlock(new Keccak(data), BlockTreeLookupOptions.None);
                    if (_logger.IsInfo) _logger.Info($"Start block loaded from HEAD - {startBlock?.ToString(Block.Format.Short)}");
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
                throw new InvalidOperationException(
                    "An attempt to set a head block that has not been stored in the DB.");
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

            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(number, blockHash, false);
            if (level is null || blockInfo is null) return false;
            return !blockInfo.IsBeaconInfo;
        }

        public bool IsKnownBeaconBlock(long number, Keccak blockHash)
        {
            if (number > BestKnownBeaconNumber)
            {
                return false;
            }

            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(number, blockHash, true);
            if (level is null || blockInfo is null) return false;
            return blockInfo.IsBeaconInfo;
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

        private ChainLevelInfo UpdateOrCreateLevel(long number, Keccak hash, BlockInfo blockInfo, bool setAsMain = false)
        {
            using (BatchWrite? batch = _chainLevelInfoRepository.StartBatch())
            {
                ChainLevelInfo level = LoadLevel(number, false);

                if (!blockInfo.IsBeaconInfo && number > BestKnownNumber)
                {
                    BestKnownNumber = number;
                }

                if (level is not null)
                {
                    BlockInfo[] blockInfos = level.BlockInfos;

                    int? foundIndex = FindIndex(hash, level);
                    if (!foundIndex.HasValue)
                    {
                        Array.Resize(ref blockInfos, blockInfos.Length + 1);
                    }
                    else
                    {
                        if (blockInfo.IsBeaconInfo && blockInfos[foundIndex.Value].IsBeaconMainChain)
                            blockInfo.Metadata |= BlockMetadata.BeaconMainChain;
                    }

                    int index = foundIndex ?? blockInfos.Length - 1;

                    if (setAsMain)
                    {
                        blockInfos[index] = blockInfos[0];
                        blockInfos[0] = blockInfo;
                    }
                    else
                    {
                        blockInfos[index] = blockInfo;
                    }

                    level.BlockInfos = blockInfos;
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
        }

        public (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Keccak blockHash) => LoadInfo(number, blockHash, true);

        private (BlockInfo? Info, ChainLevelInfo? Level) LoadInfo(long number, Keccak blockHash, bool forceLoad)
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
            return number == 0L || Head is null || number > Head.Number - CacheSize && number <= Head.Number + 1;
        }

        public ChainLevelInfo? FindLevel(long number)
        {
            return _chainLevelInfoRepository.LoadLevel(number);
        }

        public Keccak? HeadHash => Head?.Hash;
        public Keccak? GenesisHash => Genesis?.Hash;
        public Keccak? PendingHash => Head?.Hash;
        public Keccak? FinalizedHash { get; private set; }
        public Keccak? SafeHash { get; private set; }

        public Block? FindBlock(Keccak? blockHash, BlockTreeLookupOptions options)
        {
            if (blockHash is null || blockHash == Keccak.Zero)
            {
                return null;
            }

            Block block = _blockDb.Get(blockHash, _blockDecoder, _blockCache, shouldCache: false);
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
                        level = UpdateOrCreateLevel(block.Number, block.Hash, blockInfo);
                    }
                }
                else
                {
                    if (blockInfo.TotalDifficulty != UInt256.Zero || block.IsGenesis)
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
            if (header.IsGenesis)
            {
                header.TotalDifficulty = header.Difficulty;
                if (_logger.IsTrace) _logger.Trace($"Genesis total difficulty is {header.TotalDifficulty}");
                return;
            }

            // In some Ethereum tests and possible testnets difficulty of all blocks might be zero
            // We also checking TTD is zero to ensure that block after genesis have zero difficulty
            if (Genesis!.Difficulty == 0 && _specProvider.TerminalTotalDifficulty == 0)
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
                    UpdateOrCreateLevel(current.Number, current.Hash, blockInfo);
                }

                while (stack.TryPop(out BlockHeader child))
                {
                    child.TotalDifficulty = current.TotalDifficulty + child.Difficulty;
                    BlockInfo blockInfo = new(child.Hash, child.TotalDifficulty.Value);
                    UpdateOrCreateLevel(child.Number, child.Hash, blockInfo);
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
            if (Head.Number >= startNumber)
            {
                // greater than zero so will not fail
                ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(startNumber - 1);
                if (chainLevelInfo is null)
                {
                    throw new InvalidDataException(
                        $"Chain level {startNumber - 1} does not exist when {startNumber} level exists.");
                }

                // there may be no canonical block marked on this level - then we just hack to genesis
                Keccak? newHeadHash = chainLevelInfo.HasBlockOnMainChain
                    ? chainLevelInfo.BlockInfos[0].BlockHash
                    : Genesis?.Hash;
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
            }
        }

        public void ForkChoiceUpdated(Keccak? finalizedBlockHash, Keccak? safeBlockHash)
        {
            FinalizedHash = finalizedBlockHash;
            SafeHash = safeBlockHash;
            using (_metadataDb.StartBatch())
            {
                _metadataDb.Set(MetadataDbKeys.FinalizedBlockHash, Rlp.Encode(FinalizedHash!).Bytes);
                _metadataDb.Set(MetadataDbKeys.SafeBlockHash, Rlp.Encode(SafeHash!).Bytes);
            }
        }
    }
}
