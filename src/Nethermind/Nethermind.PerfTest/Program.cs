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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AuRa;
using Nethermind.AuRa.Rewards;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Store;
using Nethermind.Store.Repositories;

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
                _blockTree.BlockAddedToMain += (sender, args) => BlockAddedToMain?.Invoke(sender, args);
            }

            public int ChainId => _blockTree.ChainId;
            public BlockHeader Genesis => _blockTree.Genesis;
            public BlockHeader BestSuggestedHeader => _blockTree.BestSuggestedHeader;
            public BlockHeader LowestInsertedHeader => _blockTree.LowestInsertedHeader;
            public Block LowestInsertedBody => _blockTree.LowestInsertedBody;
            public Block BestSuggestedBody => _blockTree.BestSuggestedBody;
            public long BestKnownNumber => _blockTree.BestKnownNumber;
            public BlockHeader Head => _blockTree.Head;
            public bool CanAcceptNewBlocks { get; } = true;

            public async Task LoadBlocksFromDb(CancellationToken cancellationToken, long? startBlockNumber, int batchSize = BlockTree.DbLoadBatchSize, int maxBlocksToLoad = int.MaxValue)
            {
                await _blockTree.LoadBlocksFromDb(cancellationToken, startBlockNumber, batchSize, maxBlocksToLoad);
            }

            public async Task FixFastSyncGaps(CancellationToken cancellationToken)
            {
                await _blockTree.FixFastSyncGaps(cancellationToken);
            }

            public AddBlockResult Insert(Block block)
            {
                return _blockTree.Insert(block);
            }

            public void Insert(IEnumerable<Block> blocks)
            {
                _blockTree.Insert(blocks);
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

            public Keccak FindHash(long blockNumber)
            {
                return _blockTree.FindHash(blockNumber);
            }

            public BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse)
            {
                return _blockTree.FindHeaders(hash, numberOfBlocks, skip, reverse);
            }

            public void DeleteInvalidBlock(Block invalidBlock)
            {
                _blockTree.DeleteInvalidBlock(invalidBlock);
            }

            public bool IsMainChain(Keccak blockHash)
            {
                return _blockTree.IsMainChain(blockHash);
            }

            public bool IsKnownBlock(long number, Keccak blockHash)
            {
                return _blockTree.IsKnownBlock(number, blockHash);
            }

            public void UpdateMainChain(Block[] blocks)
            {
                _blockTree.UpdateMainChain(blocks);
            }

            public bool WasProcessed(long number, Keccak blockHash)
            {
                return false;
            }

            public event EventHandler<BlockEventArgs> NewBestSuggestedBlock;
            public event EventHandler<BlockEventArgs> BlockAddedToMain;
            public event EventHandler<BlockEventArgs> NewHeadBlock;
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
            Rlp.RegisterDecoders(typeof(ParityTraceDecoder).Assembly);
            
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
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            _logger.Info($"ChainSpec loaded");
            
            var specProvider = new ChainSpecBasedSpecProvider(chainSpec);
            IRewardCalculator rewardCalculator = new RewardCalculator(specProvider);

            var dbProvider = new RocksDbProvider(DbBasePath, DbConfig.Default, _logManager, true, true);
            var stateDb = dbProvider.StateDb;
            var codeDb = dbProvider.CodeDb;
            var traceDb = dbProvider.TraceDb;
            var blocksDb = dbProvider.BlocksDb;
            var headersDb = dbProvider.HeadersDb;
            var blockInfosDb = dbProvider.BlockInfosDb;
            var receiptsDb = dbProvider.ReceiptsDb;
            
            /* state & storage */
            var stateProvider = new StateProvider(stateDb, codeDb, _logManager);
            var storageProvider = new StorageProvider(stateDb, stateProvider, _logManager);

            var ethereumSigner = new EthereumEcdsa(specProvider, _logManager);
            
            var transactionPool = new TxPool(
                NullTxStorage.Instance, 
                Timestamper.Default,
                ethereumSigner, 
                specProvider, 
                new TxPoolConfig(),
                stateProvider,
                _logManager);

            var blockInfoRepository = new ChainLevelInfoRepository(blockInfosDb);
            var blockTree = new UnprocessedBlockTreeWrapper(new BlockTree(blocksDb, headersDb, blockInfosDb, blockInfoRepository, specProvider, transactionPool, _logManager));

            IBlockDataRecoveryStep recoveryStep = new TxSignaturesRecoveryStep(ethereumSigner, transactionPool, _logManager);
           
            /* blockchain processing */
            IList<IAdditionalBlockProcessor> blockProcessors = new List<IAdditionalBlockProcessor>();
            var blockhashProvider = new BlockhashProvider(blockTree, LimboLogs.Instance);
            var virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, specProvider, _logManager);
            var processor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, _logManager);
            
            ISealValidator sealValidator;
            if (specProvider.ChainId == RopstenSpecProvider.Instance.ChainId)
            {
                var difficultyCalculator = new DifficultyCalculator(specProvider);
                sealValidator = new EthashSealValidator(_logManager, difficultyCalculator, new Ethash(_logManager));
            }
            else if (chainSpec.SealEngineType == SealEngineType.Clique)
            {
                var snapshotManager = new SnapshotManager(CliqueConfig.Default, blocksDb, blockTree, ethereumSigner, _logManager);
                sealValidator = new CliqueSealValidator(CliqueConfig.Default, snapshotManager, _logManager);
                rewardCalculator = NoBlockRewards.Instance;
                recoveryStep = new CompositeDataRecoveryStep(recoveryStep, new AuthorRecoveryStep(snapshotManager));
            }
            else if (chainSpec.SealEngineType == SealEngineType.AuRa)
            {
                var abiEncoder = new AbiEncoder();
                var validatorProcessor = new AuRaAdditionalBlockProcessorFactory(dbProvider.StateDb, stateProvider, abiEncoder, processor, blockTree, _logManager)
                    .CreateValidatorProcessor(chainSpec.AuRa.Validators);
                    
                sealValidator = new AuRaSealValidator(validatorProcessor, ethereumSigner, _logManager);
                rewardCalculator = new AuRaRewardCalculator(chainSpec.AuRa, abiEncoder, processor);
                blockProcessors.Add(validatorProcessor);
            }
            else
            {
                throw new NotSupportedException();
            }

            /* store & validation */
            
            var receiptStorage = new InMemoryReceiptStorage();
            var headerValidator = new HeaderValidator(blockTree, sealValidator, specProvider, _logManager);
            var ommersValidator = new OmmersValidator(blockTree, headerValidator, _logManager);
            var transactionValidator = new TxValidator(chainSpec.ChainId);
            var blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, specProvider, _logManager);
            
            /* blockchain processing */
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, processor, stateDb, codeDb, traceDb, stateProvider, storageProvider, transactionPool, receiptStorage, _logManager, blockProcessors);
            var blockchainProcessor = new BlockchainProcessor(blockTree, blockProcessor, recoveryStep, _logManager, true, false);
            
            if (chainSpec.SealEngineType == SealEngineType.AuRa)
            {
                stateProvider.CreateAccount(Address.Zero, UInt256.Zero);
                storageProvider.Commit();
                stateProvider.Commit(Homestead.Instance);
                var finalizationManager = new AuRaBlockFinalizationManager(blockTree,blockInfoRepository, blockProcessor, blockProcessors.OfType<IAuRaValidator>().First(), _logManager);
            }
            
            foreach ((Address address, ChainSpecAllocation allocation) in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(address, allocation.Balance);
                if (allocation.Code != null)
                {
                    Keccak codeHash = stateProvider.UpdateCode(allocation.Code);
                    stateProvider.UpdateCodeHash(address, codeHash, specProvider.GenesisSpec);
                }
                
                if (allocation.Constructor != null)
                {
                    Transaction constructorTransaction = new Transaction(true)
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
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot;
            chainSpec.Genesis.Header.Hash = BlockHeader.CalculateHash(chainSpec.Genesis.Header);

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
                if ((BigInteger) args.Block.Number % 10000 == 9999)
                {
                    stopwatch.Stop();
                    long ms = 1_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
                    BigInteger number = args.Block.Number + 1;
                    _logger.Warn($"TOTAL after {number} (ms)       : " + ms);
                    _logger.Warn($"TOTAL after {number} blocks/s   : {(decimal) currentHead.Number / (ms / 1000m),5}");
                    _logger.Warn($"TOTAL after {number} Mgas/s     : {((decimal) totalGas / 1000000) / (ms / 1000m),5}");
                    _logger.Warn($"TOTAL after {number} max mem    : {maxMemory}");
                    _logger.Warn($"TOTAL after {number} GC (0/1/2) : {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                    _logger.Warn($"Is server GC {number}           : {System.Runtime.GCSettings.IsServerGC}");
                    _logger.Warn($"GC latency mode {number}        : {System.Runtime.GCSettings.LatencyMode}");

                    _logger.Warn($"TOTAL after {number} blocks DB reads      : {Store.Metrics.BlocksDbReads}");
                    _logger.Warn($"TOTAL after {number} blocks DB writes     : {Store.Metrics.BlocksDbWrites}");
                    _logger.Warn($"TOTAL after {number} infos DB reads       : {Store.Metrics.BlockInfosDbReads}");
                    _logger.Warn($"TOTAL after {number} infos DB writes      : {Store.Metrics.BlockInfosDbWrites}");
                    _logger.Warn($"TOTAL after {number} state tree reads     : {Store.Metrics.StateTreeReads}");
                    _logger.Warn($"TOTAL after {number} state tree writes    : {Store.Metrics.StateTreeWrites}");
                    _logger.Warn($"TOTAL after {number} state DB reads       : {Store.Metrics.StateDbReads}");
                    _logger.Warn($"TOTAL after {number} state DB writes      : {Store.Metrics.StateDbWrites}");
                    _logger.Warn($"TOTAL after {number} storage tree reads   : {Store.Metrics.StorageTreeReads}");
                    _logger.Warn($"TOTAL after {number} storage tree writes  : {Store.Metrics.StorageTreeWrites}");
                    _logger.Warn($"TOTAL after {number} tree node hash       : {Store.Metrics.TreeNodeHashCalculations}");
                    _logger.Warn($"TOTAL after {number} tree node RLP decode : {Store.Metrics.TreeNodeRlpDecodings}");
                    _logger.Warn($"TOTAL after {number} tree node RLP encode : {Store.Metrics.TreeNodeRlpEncodings}");
                    _logger.Warn($"TOTAL after {number} code DB reads        : {Store.Metrics.CodeDbReads}");
                    _logger.Warn($"TOTAL after {number} code DB writes       : {Store.Metrics.CodeDbWrites}");
                    _logger.Warn($"TOTAL after {number} receipts DB reads    : {Store.Metrics.ReceiptsDbReads}");
                    _logger.Warn($"TOTAL after {number} receipts DB writes   : {Store.Metrics.ReceiptsDbWrites}");
                    _logger.Warn($"TOTAL after {number} other DB reads       : {Store.Metrics.OtherDbReads}");
                    _logger.Warn($"TOTAL after {number} other DB writes      : {Store.Metrics.OtherDbWrites}");
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

            await Task.WhenAny(completionSource.Task, blockTree.LoadBlocksFromDb(CancellationToken.None, 0, 10000, BlocksToLoad));

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