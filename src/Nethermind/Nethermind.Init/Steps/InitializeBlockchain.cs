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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Services;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Witnesses;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.Witness;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializePlugins), typeof(InitializeBlockTree), typeof(SetupKeyStore))]
    public class InitializeBlockchain : IStep
    {
        private readonly INethermindApi _api;
        private ILogger? _logger;

        // ReSharper disable once MemberCanBeProtected.Global
        public InitializeBlockchain(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            await InitBlockchain();
        }

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        private Task InitBlockchain()
        {
            BlockTraceDumper.Converters.AddRange(DebugModuleFactory.Converters);
            BlockTraceDumper.Converters.AddRange(TraceModuleFactory.Converters);
            
            var (getApi, setApi) = _api.ForBlockchain;
            
            if (getApi.ChainSpec == null) throw new StepDependencyException(nameof(getApi.ChainSpec));
            if (getApi.DbProvider == null) throw new StepDependencyException(nameof(getApi.DbProvider));
            if (getApi.SpecProvider == null) throw new StepDependencyException(nameof(getApi.SpecProvider));
            
            _logger = getApi.LogManager.GetClassLogger();
            IInitConfig initConfig = getApi.Config<IInitConfig>();
            ISyncConfig syncConfig = getApi.Config<ISyncConfig>();
            IPruningConfig pruningConfig = getApi.Config<IPruningConfig>();

            if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
            {
                _logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
                syncConfig.DownloadBodiesInFastSync = true;
            }
            
            Account.AccountStartNonce = getApi.ChainSpec.Parameters.AccountStartNonce;

            IWitnessCollector witnessCollector;
            if (syncConfig.WitnessProtocolEnabled)
            {
                WitnessCollector witnessCollectorImpl = new(getApi.DbProvider.WitnessDb, _api.LogManager);
                witnessCollector = setApi.WitnessCollector = witnessCollectorImpl;
                setApi.WitnessRepository = witnessCollectorImpl.WithPruning(getApi.BlockTree!, getApi.LogManager);
            }
            else
            {
                witnessCollector = setApi.WitnessCollector = NullWitnessCollector.Instance;
                setApi.WitnessRepository = NullWitnessCollector.Instance;
            }

            IKeyValueStoreWithBatching cachedStateDb = getApi.DbProvider.StateDb
                .Cached(Trie.MemoryAllowance.TrieNodeCacheCount);
            setApi.MainStateDbWithCache = cachedStateDb;
            IKeyValueStore codeDb = getApi.DbProvider.CodeDb
                .WitnessedBy(witnessCollector);

            TrieStore trieStore;
            if (pruningConfig.Enabled)
            {
                setApi.TrieStore = trieStore = new TrieStore(
                    setApi.MainStateDbWithCache.WitnessedBy(witnessCollector),
                    Prune.WhenCacheReaches(pruningConfig.CacheMb.MB()), // TODO: memory hint should define this
                    Persist.IfBlockOlderThan(pruningConfig.PersistenceInterval), // TODO: this should be based on time
                    getApi.LogManager);
            }
            else
            {
                setApi.TrieStore = trieStore = new TrieStore(
                    setApi.MainStateDbWithCache.WitnessedBy(witnessCollector),
                    No.Pruning,
                    Persist.EveryBlock,
                    getApi.LogManager);
            }
            
            getApi.DisposeStack.Push(trieStore);
            trieStore.ReorgBoundaryReached += ReorgBoundaryReached;
            ITrieStore readOnlyTrieStore = setApi.ReadOnlyTrieStore = trieStore.AsReadOnly(cachedStateDb);

            IStateProvider stateProvider = setApi.StateProvider = new StateProvider(
                trieStore,
                codeDb,
                getApi.LogManager);

            ReadOnlyDbProvider readOnly = new(getApi.DbProvider, false);
            
            IStateReader stateReader = setApi.StateReader = new StateReader(readOnlyTrieStore, readOnly.GetDb<IDb>(DbNames.Code), getApi.LogManager);
            
            setApi.TransactionComparerProvider =
                new TransactionComparerProvider(getApi.SpecProvider!, getApi.BlockTree.AsReadOnly());
            setApi.ChainHeadStateProvider = new ChainHeadReadOnlyStateProvider(getApi.BlockTree, stateReader);
            Account.AccountStartNonce = getApi.ChainSpec.Parameters.AccountStartNonce;

            stateProvider.StateRoot = getApi.BlockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;

            if (_api.Config<IInitConfig>().DiagnosticMode == DiagnosticMode.VerifyTrie)
            {
                _logger.Info("Collecting trie stats and verifying that no nodes are missing...");
                TrieStats stats = stateProvider.CollectStats(getApi.DbProvider.CodeDb, _api.LogManager);
                _logger.Info($"Starting from {getApi.BlockTree.Head?.Number} {getApi.BlockTree.Head?.StateRoot}{Environment.NewLine}" + stats);
            }

            // Init state if we need system calls before actual processing starts
            if (getApi.BlockTree!.Head?.StateRoot != null)
            {
                stateProvider.StateRoot = getApi.BlockTree.Head.StateRoot;
            }
            
            var txValidator = setApi.TxValidator = new TxValidator(getApi.SpecProvider.ChainId);
            
            ITxPool txPool = _api.TxPool = CreateTxPool();

            ReceiptCanonicalityMonitor receiptCanonicalityMonitor = new(getApi.BlockTree, getApi.ReceiptStorage, _api.LogManager);
            getApi.DisposeStack.Push(receiptCanonicalityMonitor);

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(getApi.EthereumEcdsa, txPool, getApi.SpecProvider, getApi.LogManager));
            
            IStorageProvider storageProvider = setApi.StorageProvider = new StorageProvider(
                trieStore,
                stateProvider,
                getApi.LogManager);

            // blockchain processing
            BlockhashProvider blockhashProvider = new (
                getApi.BlockTree, getApi.LogManager);

            VirtualMachine virtualMachine = new (
                stateProvider,
                storageProvider,
                blockhashProvider,
                getApi.SpecProvider,
                getApi.LogManager);

            ITransactionProcessor transactionProcessor = _api.TransactionProcessor = new TransactionProcessor(
                getApi.SpecProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                getApi.LogManager);

            InitSealEngine();
            if (_api.SealValidator == null) throw new StepDependencyException(nameof(_api.SealValidator));

            /* validation */
            IHeaderValidator? headerValidator = setApi.HeaderValidator = CreateHeaderValidator();

            OmmersValidator ommersValidator = new(
                getApi.BlockTree,
                headerValidator,
                getApi.LogManager);

            IBlockValidator? blockValidator = setApi.BlockValidator = new BlockValidator(
                txValidator,
                headerValidator,
                ommersValidator,
                getApi.SpecProvider,
                getApi.LogManager);

            IChainHeadInfoProvider chainHeadInfoProvider =
                new ChainHeadInfoProvider(getApi.SpecProvider, getApi.BlockTree, stateReader);
            setApi.TxPoolInfoProvider = new TxPoolInfoProvider(chainHeadInfoProvider.AccountStateProvider, txPool);
            IBlockProcessor? mainBlockProcessor = setApi.MainBlockProcessor = CreateBlockProcessor();

            BlockchainProcessor blockchainProcessor = new(
                getApi.BlockTree,
                mainBlockProcessor,
                _api.BlockPreprocessor,
                getApi.LogManager,
                new BlockchainProcessor.Options
                {
                    AutoProcess = !syncConfig.BeamSync,
                    StoreReceiptsByDefault = initConfig.StoreReceipts,
                });

            setApi.BlockProcessingQueue = blockchainProcessor;
            setApi.BlockchainProcessor = blockchainProcessor;

            if (syncConfig.BeamSync)
            {
                BeamBlockchainProcessor beamBlockchainProcessor = new(
                    new ReadOnlyDbProvider(_api.DbProvider, false),
                    getApi.BlockTree,
                    getApi.SpecProvider,
                    getApi.LogManager,
                    blockValidator,
                    _api.BlockPreprocessor,
                    _api.RewardCalculatorSource!, // TODO: does it work with AuRa?
                    blockchainProcessor,
                    getApi.SyncModeSelector!);

                _api.DisposeStack.Push(beamBlockchainProcessor);
            }

            // TODO: can take the tx sender from plugin here maybe
            ITxSigner txSigner = new WalletTxSigner(getApi.Wallet, getApi.SpecProvider.ChainId);
            TxSealer standardSealer = new(txSigner, getApi.Timestamper);
            NonceReservingTxSealer nonceReservingTxSealer =
                new(txSigner, getApi.Timestamper, txPool);
            setApi.TxSender = new TxPoolSender(txPool, nonceReservingTxSealer, standardSealer);

            // TODO: possibly hide it (but need to confirm that NDM does not really need it)
            IFilterStore? filterStore = setApi.FilterStore = new FilterStore();
            setApi.FilterManager = new FilterManager(filterStore, mainBlockProcessor, txPool, getApi.LogManager);
            setApi.HealthHintService = CreateHealthHintService();
            return Task.CompletedTask;
        }
        
        private void ReorgBoundaryReached(object? sender, ReorgBoundaryReached e)
        {
            if (_logger.IsDebug) _logger.Debug($"Saving reorg boundary {e.BlockNumber}");
            (_api.BlockTree as BlockTree)!.SavePruningReorganizationBoundary(e.BlockNumber);
        }
        
        protected virtual IHealthHintService CreateHealthHintService() =>
            new HealthHintService(_api.ChainSpec);


        protected virtual TxPool.TxPool CreateTxPool() =>
            new TxPool.TxPool(
                _api.EthereumEcdsa,
                new ChainHeadInfoProvider(_api.SpecProvider, _api.BlockTree, _api.StateReader),
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator,
                _api.LogManager,
                CreateTxPoolTxComparer());

        protected IComparer<Transaction> CreateTxPoolTxComparer() => _api.TransactionComparerProvider.GetDefaultComparer();

        protected virtual HeaderValidator CreateHeaderValidator() =>
            new HeaderValidator(
                _api.BlockTree,
                _api.SealValidator,
                _api.SpecProvider,
                _api.LogManager);

        // TODO: remove from here - move to consensus?
        protected virtual BlockProcessor CreateBlockProcessor()
        {
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));

            return new BlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(_api.TransactionProcessor!),
                new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor, _api.StateProvider!),
                _api.StateProvider,
                _api.StorageProvider,
                _api.ReceiptStorage,
                _api.WitnessCollector,
                _api.LogManager);
        }

        // TODO: remove from here - move to consensus?
        protected virtual void InitSealEngine()
        {
        }
    }
}
