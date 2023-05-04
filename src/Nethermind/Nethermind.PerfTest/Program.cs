// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Blockchain.Visitors;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Metrics = Nethermind.Trie.Metrics;

namespace Nethermind.PerfTest
{
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            await RunBenchmarkBlocks().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error("test failed", t.Exception);
                    _logger.Error("inner", t.Exception?.InnerException);
                    Console.ReadLine();
                }
            });
        }

        private static ILogger _logger;
        private static ILogManager _logManager;

        private class UnprocessedBlockTreeWrapper : IBlockTree
        {
            private readonly IBlockTree _blockTree;

            public UnprocessedBlockTreeWrapper(IBlockTree blockTree)
            {
                _blockTree = blockTree;
                _blockTree.NewHeadBlock += (sender, args) => NewHeadBlock?.Invoke(sender, args);
                _blockTree.NewBestSuggestedBlock += (sender, args) => NewBestSuggestedBlock?.Invoke(sender, args);
            }

            public int ChainId => _blockTree.ChainId;
            public BlockHeader Genesis => _blockTree.Genesis;
            public BlockHeader BestSuggestedHeader => _blockTree.BestSuggestedHeader;
            public BlockHeader LowestInsertedHeader => _blockTree.LowestInsertedHeader;
            public long? LowestInsertedBodyNumber
            {
                get => _blockTree.LowestInsertedBodyNumber;
                set => _blockTree.LowestInsertedBodyNumber = value;
            }

            public Block BestSuggestedBody => _blockTree.BestSuggestedBody;
            public long BestKnownNumber => _blockTree.BestKnownNumber;
            public Block Head => _blockTree.Head;
            public bool CanAcceptNewBlocks { get; } = true;

            public async Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken)
            {
                await _blockTree.Accept(blockTreeVisitor, cancellationToken);
            }

            public ChainLevelInfo FindLevel(long number) => _blockTree.FindLevel(number);
            public BlockInfo FindCanonicalBlockInfo(long blockNumber)
            {
                throw new NotImplementedException();
            }

            public AddBlockResult Insert(Block block)
            {
                return _blockTree.Insert(block);
            }

            public void Insert(IEnumerable<Block> blocks)
            {
                _blockTree.Insert(blocks);
            }

            public void UpdateHeadBlock(Keccak blockHash)
            {
                _blockTree.UpdateHeadBlock(blockHash);
            }

            public AddBlockResult SuggestBlock(Block block, bool shouldProcess = true)
            {
                return _blockTree.SuggestBlock(block, shouldProcess);
            }

            public AddBlockResult Insert(BlockHeader header)
            {
                return _blockTree.Insert(header);
            }

            public AddBlockResult SuggestHeader(BlockHeader header)
            {
                return _blockTree.SuggestHeader(header);
            }

            public Keccak HeadHash => _blockTree.HeadHash;
            public Keccak GenesisHash => _blockTree.GenesisHash;
            public Keccak PendingHash => _blockTree.PendingHash;

            public Block FindBlock(Keccak blockHash, BlockTreeLookupOptions option)
            {
                return _blockTree.FindBlock(blockHash, option);
            }

            public BlockHeader FindHeader(Keccak blockHash, BlockTreeLookupOptions options)
            {
                return _blockTree.FindHeader(blockHash, options);
            }

            public Block FindBlock(long blockNumber, BlockTreeLookupOptions options)
            {
                return _blockTree.FindBlock(blockNumber, options);
            }

            public BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options)
            {
                return _blockTree.FindHeader(blockNumber, options);
            }

            public Keccak FindBlockHash(long blockNumber)
            {
                return _blockTree.FindBlockHash(blockNumber);
            }

            public bool IsMainChain(BlockHeader blockHeader)
            {
                return _blockTree.IsMainChain(blockHeader);
            }

            public Keccak FindHash(long blockNumber)
            {
                return _blockTree.FindHash(blockNumber);
            }

            public BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse)
            {
                return _blockTree.FindHeaders(hash, numberOfBlocks, skip, reverse);
            }

            public BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant, long maxSearchDepth)
            {
                return _blockTree.FindLowestCommonAncestor(firstDescendant, secondDescendant, maxSearchDepth);
            }

            public void DeleteInvalidBlock(Block invalidBlock)
            {
                _blockTree.DeleteInvalidBlock(invalidBlock);
            }

            public bool IsMainChain(Keccak blockHash)
            {
                return _blockTree.IsMainChain(blockHash);
            }

            public BlockHeader FindBestSuggestedHeader()
            {
                return _blockTree.BestSuggestedHeader;
            }

            public bool IsKnownBlock(long number, Keccak blockHash)
            {
                return _blockTree.IsKnownBlock(number, blockHash);
            }

            public void UpdateMainChain(Block[] blocks, bool wereProcessed)
            {
                _blockTree.UpdateMainChain(blocks, wereProcessed);
            }

            public bool WasProcessed(long number, Keccak blockHash)
            {
                return false;
            }

            public event EventHandler<BlockEventArgs> NewBestSuggestedBlock;

            public event EventHandler<BlockEventArgs> BlockAddedToMain
            {
                add { }
                remove { }
            }

            public event EventHandler<BlockEventArgs> NewHeadBlock;

            public int DeleteChainSlice(in long startNumber, long? endNumber)
            {
                return _blockTree.DeleteChainSlice(startNumber, endNumber);
            }
        }

        private const string DbBasePath = @"C:\perf_db";
        //        private const string DbBasePath = @"C:\chains\blocks_1M";

        private static void DeleteDb(string dbPath)
        {
            if (Directory.Exists(dbPath)) Directory.Delete(dbPath, true);
        }

        private static readonly string FullStateDbPath = Path.Combine(DbBasePath, "state");
        private static readonly string FullCodeDbPath = Path.Combine(DbBasePath, "code");
        private static readonly string FullReceiptsDbPath = Path.Combine(DbBasePath, "receipts");
        private static readonly string FullPendingTxsDbPath = Path.Combine(DbBasePath, "pendingtxs");
        private static readonly string FullBlocksDbPath = Path.Combine(DbBasePath, "blocks");
        private static readonly string FullBlockInfosDbPath = Path.Combine(DbBasePath, "blockInfos");

        private const int BlocksToLoad = 100_000;

        private static async Task RunBenchmarkBlocks()
        {
            /* logging & instrumentation */
            _logManager = new NLogManager("perfTest.logs.txt", null);
            _logger = _logManager.GetClassLogger();

            if (_logger.IsInfo) _logger.Info("Deleting state DBs");

            DeleteDb(FullStateDbPath);
            DeleteDb(FullCodeDbPath);
            DeleteDb(FullReceiptsDbPath);
            DeleteDb(FullPendingTxsDbPath);
            if (_logger.IsInfo) _logger.Info("State DBs deleted");

            /* load spec */
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            string path = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"chainspec", "ropsten.json"));
            _logger.Info($"Loading ChainSpec from {path}");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            _logger.Info($"ChainSpec loaded");

            var specProvider = new ChainSpecBasedSpecProvider(chainSpec);
            IRewardCalculator rewardCalculator = new RewardCalculator(specProvider);

            var dbProvider = new RocksDbProvider(_logManager);
            await dbProvider.Init(DbBasePath, DbConfig.Default, true);
            var stateDb = dbProvider.StateDb;
            var codeDb = dbProvider.CodeDb;
            var blocksDb = dbProvider.BlocksDb;
            var headersDb = dbProvider.HeadersDb;
            var blockInfosDb = dbProvider.BlockInfosDb;
            var receiptsDb = dbProvider.ReceiptsDb;

            /* state & storage */
            var trieStore = new TrieStore(stateDb, new DepthAndMemoryBased(8192, 1.GB()), new ConstantInterval(8192), _logManager);
            var stateProvider = new StateProvider(trieStore, codeDb, _logManager);
            var storageProvider = new StorageProvider(trieStore, stateProvider, _logManager);

            var ethereumSigner = new EthereumEcdsa(specProvider.ChainId, _logManager);

            var transactionPool = new TxPool.TxPool(
                NullTxStorage.Instance,
                Timestamper.Default,
                ethereumSigner,
                specProvider,
                new TxPoolConfig(),
                stateProvider,
                _logManager);

            var blockInfoRepository = new ChainLevelInfoRepository(blockInfosDb);
            var blockTree = new UnprocessedBlockTreeWrapper(new BlockTree(blocksDb, headersDb, blockInfosDb, blockInfoRepository, specProvider, transactionPool, new BloomStorage(new BloomConfig(), dbProvider.HeadersDb, new InMemoryDictionaryFileStoreFactory()), _logManager));
            var receiptStorage = new InMemoryReceiptStorage();

            IBlockDataRecoveryStep recoveryStep = new TxSignaturesRecoveryStep(ethereumSigner, transactionPool, _logManager);

            /* blockchain processing */
            var blockhashProvider = new BlockhashProvider(blockTree, LimboLogs.Instance);
            var virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, specProvider, _logManager);
            var processor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, _logManager);

            ISealValidator sealValidator;
            if (specProvider.ChainId == RopstenSpecProvider.Instance.ChainId)
            {
                var difficultyCalculator = new DifficultyCalculator(specProvider);
                sealValidator = new EthashSealValidator(_logManager, difficultyCalculator, new CryptoRandom(), new Ethash(_logManager));
            }
            else if (chainSpec.SealEngineType == SealEngineType.Clique)
            {
                var snapshotManager = new SnapshotManager(CliqueConfig.Default, blocksDb, blockTree, ethereumSigner, _logManager);
                sealValidator = new CliqueSealValidator(CliqueConfig.Default, snapshotManager, _logManager);
                rewardCalculator = NoBlockRewards.Instance;
                recoveryStep = new CompositeDataRecoveryStep(recoveryStep, new AuthorRecoveryStep(snapshotManager));
            }
            else
            {
                throw new NotSupportedException();
            }

            /* store & validation */
            var headerValidator = new HeaderValidator(blockTree, sealValidator, specProvider, _logManager);
            var unclesValidator = new UnclesValidator(blockTree, headerValidator, _logManager);
            var transactionValidator = new TxValidator(chainSpec.ChainId);
            var blockValidator = new BlockValidator(transactionValidator, headerValidator, unclesValidator, specProvider, _logManager);

            /* blockchain processing */
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, processor, stateProvider, storageProvider, transactionPool, receiptStorage, _logManager);
            var blockchainProcessor = new BlockchainProcessor(blockTree, blockProcessor, recoveryStep, _logManager, BlockchainProcessor.Options.Default);

            foreach ((Address address, ChainSpecAllocation allocation) in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(address, allocation.Balance);
                if (allocation.Code != null)
                {
                    Keccak codeHash = stateProvider.UpdateCode(allocation.Code);
                    stateProvider.InsertCode(address, codeHash, specProvider.GenesisSpec);
                }

                if (allocation.Constructor != null)
                {
                    Transaction constructorTransaction = new SystemTransaction()
                    {
                        SenderAddress = address,
                        Init = allocation.Constructor,
                        GasLimit = chainSpec.Genesis.GasLimit
                    };

                    processor.Execute(constructorTransaction, chainSpec.Genesis.Header, NullTxTracer.Instance);
                }
            }

            _logger.Info($"Allocations configured, committing...");

            stateProvider.Commit(specProvider.GenesisSpec);

            _logger.Info($"Finalizing genesis...");
            stateProvider.RecalculateStateRoot();
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot;
            chainSpec.Genesis.Header.Hash = chainSpec.Genesis.Header.CalculateHash();

            if (chainSpec.Genesis.Hash != blockTree.Genesis.Hash)
            {
                throw new Exception("Unexpected genesis hash");
            }

            _logger.Info($"Starting benchmark processor...");
            /* start processing */
            BigInteger totalGas = BigInteger.Zero;
            Stopwatch stopwatch = new Stopwatch();
            Block currentHead;
            long maxMemory = 0;
            blockTree.NewHeadBlock += (sender, args) =>
            {
                currentHead = args.Block;
                if (currentHead.Number == 0)
                {
                    return;
                }

                maxMemory = Math.Max(maxMemory, GC.GetTotalMemory(false));
                totalGas += currentHead.GasUsed;
                if ((BigInteger)args.Block.Number % 10000 == 9999)
                {
                    stopwatch.Stop();
                    long ms = 1_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                    BigInteger number = args.Block.Number + 1;
                    _logger.Warn($"TOTAL after {number} (ms)       : " + ms);
                    _logger.Warn($"TOTAL after {number} blocks/s   : {(decimal)currentHead.Number / (ms / 1000m),5}");
                    _logger.Warn($"TOTAL after {number} Mgas/s     : {((decimal)totalGas / 1000000) / (ms / 1000m),5}");
                    _logger.Warn($"TOTAL after {number} max mem    : {maxMemory}");
                    _logger.Warn($"TOTAL after {number} GC (0/1/2) : {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                    _logger.Warn($"Is server GC {number}           : {System.Runtime.GCSettings.IsServerGC}");
                    _logger.Warn($"GC latency mode {number}        : {System.Runtime.GCSettings.LatencyMode}");

                    _logger.Warn($"TOTAL after {number} blocks DB reads      : {Db.Metrics.BlocksDbReads}");
                    _logger.Warn($"TOTAL after {number} blocks DB writes     : {Db.Metrics.BlocksDbWrites}");
                    _logger.Warn($"TOTAL after {number} infos DB reads       : {Db.Metrics.BlockInfosDbReads}");
                    _logger.Warn($"TOTAL after {number} infos DB writes      : {Db.Metrics.BlockInfosDbWrites}");
                    _logger.Warn($"TOTAL after {number} state tree reads     : {Db.Metrics.StateTreeReads}");
                    _logger.Warn($"TOTAL after {number} state tree writes    : {Db.Metrics.StateTreeWrites}");
                    _logger.Warn($"TOTAL after {number} state DB reads       : {Db.Metrics.StateDbReads}");
                    _logger.Warn($"TOTAL after {number} state DB writes      : {Db.Metrics.StateDbWrites}");
                    _logger.Warn($"TOTAL after {number} storage tree reads   : {Db.Metrics.StorageTreeReads}");
                    _logger.Warn($"TOTAL after {number} storage tree writes  : {Db.Metrics.StorageTreeWrites}");
                    _logger.Warn($"TOTAL after {number} tree node hash       : {Metrics.TreeNodeHashCalculations}");
                    _logger.Warn($"TOTAL after {number} tree node RLP decode : {Metrics.TreeNodeRlpDecodings}");
                    _logger.Warn($"TOTAL after {number} tree node RLP encode : {Metrics.TreeNodeRlpEncodings}");
                    _logger.Warn($"TOTAL after {number} code DB reads        : {Db.Metrics.CodeDbReads}");
                    _logger.Warn($"TOTAL after {number} code DB writes       : {Db.Metrics.CodeDbWrites}");
                    _logger.Warn($"TOTAL after {number} receipts DB reads    : {Db.Metrics.ReceiptsDbReads}");
                    _logger.Warn($"TOTAL after {number} receipts DB writes   : {Db.Metrics.ReceiptsDbWrites}");
                    _logger.Warn($"TOTAL after {number} other DB reads       : {Db.Metrics.OtherDbReads}");
                    _logger.Warn($"TOTAL after {number} other DB writes      : {Db.Metrics.OtherDbWrites}");
                    _logger.Warn($"TOTAL after {number} EVM exceptions       : {Evm.Metrics.EvmExceptions}");
                    _logger.Warn($"TOTAL after {number} SLOAD opcodes        : {Evm.Metrics.SloadOpcode}");
                    _logger.Warn($"TOTAL after {number} SSTORE opcodes       : {Evm.Metrics.SstoreOpcode}");
                    _logger.Warn($"TOTAL after {number} EXP opcodes          : {Evm.Metrics.ModExpOpcode}");
                    _logger.Warn($"TOTAL after {number} BLOCKHASH opcodes    : {Evm.Metrics.BlockhashOpcode}");
                    _logger.Warn($"TOTAL after {number} EVM calls            : {Evm.Metrics.Calls}");
                    _logger.Warn($"TOTAL after {number} RIPEMD Precompiles   : {Evm.Metrics.Ripemd160Precompile}");
                    _logger.Warn($"TOTAL after {number} SHA256 Precompiles   : {Evm.Metrics.Sha256Precompile}");
                    // disk space
                    stopwatch.Start();
                }
            };

            bool isStarted = false;

            TaskCompletionSource<object> completionSource = new TaskCompletionSource<object>();
            blockTree.NewBestSuggestedBlock += (sender, args) =>
            {
                if (!isStarted)
                {
                    blockchainProcessor.Process(blockTree.FindBlock(blockTree.Genesis.Hash, BlockTreeLookupOptions.RequireCanonical), ProcessingOptions.None, NullBlockTracer.Instance);
                    stopwatch.Start();
                    blockchainProcessor.Start();
                    isStarted = true;
                }

                if (args.Block.Number == BlocksToLoad)
                {
                    completionSource.SetResult(null);
                }
            };

            DbBlocksLoader dbBlocksLoader = new DbBlocksLoader(blockTree, _logger, 0, 10000, BlocksToLoad);
            await Task.WhenAny(completionSource.Task, blockTree.Accept(dbBlocksLoader, CancellationToken.None));

            await blockchainProcessor.StopAsync(true).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Error("processing failed", t.Exception);
                        _logger.Error("inner", t.Exception.InnerException);
                        Console.ReadLine();
                    }

                    _logger.Info("Block processing completed.");
                });

            stopwatch.Stop();
            Console.ReadLine();
        }
    }
}
