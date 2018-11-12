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
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.TransactionPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.JsonRpc.Module;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Runner.Config;
using Nethermind.Store;

namespace Nethermind.Runner.Runners
{
    public class ReceiptsFiller : IEthereumRunner
    {
        private static ILogManager _logManager;
        private static ILogger _logger;

        private readonly IConfigProvider _configProvider;
        private readonly IInitConfig _initConfig;

        private IBlockchainProcessor _blockchainProcessor;
        private BlockTree _blockTree;
        private ISpecProvider _specProvider;

        public ReceiptsFiller(IConfigProvider configurationProvider, ILogManager logManager)
        {
            _configProvider = configurationProvider;
            _initConfig = configurationProvider.GetConfig<IInitConfig>();
            _logManager = logManager;
        }

        public IBlockchainBridge BlockchainBridge => null;
        public INetBridge NetBridge => null;
        public IDebugBridge DebugBridge => null;

        public async Task Start()
        {
            ConfigureTools();
            await InitBlockchain();
            if (_logger.IsDebug) _logger.Debug("Ethereum initialization completed");
        }

        private void ConfigureTools()
        {
            _logger = _logManager.GetClassLogger();

            if (_logger.IsInfo) _logger.Info("Initializing Ethereum");
            if (_logger.IsDebug) _logger.Debug($"Server GC           : {System.Runtime.GCSettings.IsServerGC}");
            if (_logger.IsDebug) _logger.Debug($"GC latency mode     : {System.Runtime.GCSettings.LatencyMode}");
            if (_logger.IsDebug) _logger.Debug($"LOH compaction mode : {System.Runtime.GCSettings.LargeObjectHeapCompactionMode}");
        }

        public async Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info("Shutting down...");

            if (_logger.IsInfo) _logger.Info("Stopping blockchain processor...");
            var blockchainProcessorTask = _blockchainProcessor?.StopAsync() ?? Task.CompletedTask;

            await Task.WhenAll(blockchainProcessorTask);

            if (_logger.IsInfo) _logger.Info("Closing DBs...");
            _dbProvider.Dispose();
            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }

        private ChainSpec LoadChainSpec(string chainSpecFile)
        {
            _logger.Info($"Loading chain spec from {chainSpecFile}");
            IJsonSerializer jsonSerializer = new UnforgivingJsonSerializer();
            ChainSpecLoader loader = new ChainSpecLoader(jsonSerializer);
            ChainSpec chainSpec = loader.LoadFromFile(chainSpecFile);
            return chainSpec;
        }

        private async Task InitBlockchain()
        {
            ChainSpec chainSpec = LoadChainSpec(_initConfig.ChainSpecPath);

            /* spec */
            if (chainSpec.ChainId == RopstenSpecProvider.Instance.ChainId)
            {
                _specProvider = RopstenSpecProvider.Instance;
            }
            else if (chainSpec.ChainId == MainNetSpecProvider.Instance.ChainId)
            {
                _specProvider = MainNetSpecProvider.Instance;
            }
            else if (chainSpec.ChainId == RinkebySpecProvider.Instance.ChainId)
            {
                _specProvider = RinkebySpecProvider.Instance;
            }
            else
            {
                _specProvider = new SingleReleaseSpecProvider(LatestRelease.Instance, chainSpec.ChainId);
            }

            var ethereumSigner = new EthereumSigner(
                _specProvider,
                _logManager);

            /* DB */
            IDbConfig dbConfig = _configProvider.GetConfig<IDbConfig>();
            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                _logger.Info($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            string dbBasePath = _initConfig.BaseDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db");
            IDbProvider writableDbProvider = new RocksDbProvider(dbBasePath, dbConfig);
            _dbProvider = new ReadOnlyDbProvider(writableDbProvider, true);

            var transactionPool = new TransactionPool(new PersistentTransactionStorage(_dbProvider.PendingTxsDb, _specProvider),
                new PendingTransactionThresholdValidator(), new Timestamp(),
                ethereumSigner, _logManager);

            var receiptStorage = new PersistentReceiptStorage(writableDbProvider.ReceiptsDb, _specProvider);

            /* blockchain */
            _blockTree = new BlockTree(
                _dbProvider.BlocksDb,
                _dbProvider.BlockInfosDb,
                _specProvider,
                transactionPool,
                _logManager);

            var sealEngine =
                (_specProvider is MainNetSpecProvider) ? ConfigureSealEngine() :
                (_specProvider is RopstenSpecProvider) ? ConfigureSealEngine() :
                (_specProvider is RinkebySpecProvider) ? ConfigureCliqueSealEngine() :
                NullSealEngine.Instance;

            /* validation */
            var headerValidator = new HeaderValidator(
                _blockTree,
                sealEngine,
                _specProvider,
                _logManager);

            var ommersValidator = new OmmersValidator(
                _blockTree,
                headerValidator,
                _logManager);

            var txValidator = new TransactionValidator(
                new SignatureValidator(_specProvider.ChainId));

            var blockValidator = new BlockValidator(
                txValidator,
                headerValidator,
                ommersValidator,
                _specProvider,
                _logManager);

            /* state */

            var stateTree = new StateTree(_dbProvider.StateDb);

            var stateProvider = new StateProvider(
                stateTree,
                _dbProvider.CodeDb,
                _logManager);

            var storageProvider = new StorageProvider(
                _dbProvider.StateDb,
                stateProvider,
                _logManager);

            /* blockchain processing */
            var blockhashProvider = new BlockhashProvider(
                _blockTree);

            var virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                _logManager);

            var transactionProcessor = new TransactionProcessor(
                _specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                _logManager);

            var rewardCalculator = (_specProvider is RinkebySpecProvider)
                ? (IRewardCalculator) new NoBlockRewards()
                : new RewardCalculator(_specProvider);

            var blockProcessor = new BlockProcessor(
                _specProvider,
                blockValidator,
                rewardCalculator,
                transactionProcessor,
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                stateProvider,
                storageProvider,
                transactionPool,
                receiptStorage,
                _logManager);

            blockProcessor.BlockProcessed += (sender, args) =>
            {
                if (_logger.IsInfo) _logger.Info($"Filled receipts for {args.Block.ToString(Block.Format.Short)}");
                _dbProvider.ClearTempChanges();
            };

            _blockchainProcessor = new BlockchainProcessor(
                _blockTree,
                blockProcessor,
                new TxSignaturesRecoveryStep(ethereumSigner), 
                _logManager,
                true);

            _blockchainProcessor.Start();
            Feeder feeder = new Feeder(_blockchainProcessor, _initConfig.ReceiptsFillerStart, _initConfig.ReceiptsFillerEnd);
            feeder.Start();

            await Task.CompletedTask;
        }

        private class Feeder
        {
            private readonly IBlockchainProcessor _processor;
            private readonly int _rangeLow;
            private readonly int _rangeHigh;

            public Feeder(IBlockchainProcessor processor, int rangeLow, int rangeHigh)
            {
                _processor = processor;
                _rangeLow = rangeLow;
                _rangeHigh = rangeHigh;
            }

            public void Start()
            {
                Task.Run(() =>
                {
                    for (int i = _rangeLow; i < _rangeHigh; i++)
                    {
                        _processor.SuggestBlock((UInt256) i, ProcessingOptions.ForceProcessing | ProcessingOptions.StoreReceipts | ProcessingOptions.ReadOnlyChain);
                    }
                });
            }
        }

        private IReadOnlyDbProvider _dbProvider;

        private ISealEngine ConfigureSealEngine()
        {
            var difficultyCalculator = new DifficultyCalculator(_specProvider);
            var sealEngine = new EthashSealEngine(new Ethash(_logManager), difficultyCalculator, _logManager);
            return sealEngine;
        }

        private ISealEngine ConfigureCliqueSealEngine()
        {
            throw new NotSupportedException();
        }
    }
}