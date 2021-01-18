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
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Witnesses;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.Witness;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializePlugins), typeof(InitializeBlockTree), typeof(SetupKeyStore))]
    public class InitializeBlockchain : IStep
    {
        private readonly INethermindApi _api;

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
            var (getApi, setApi) = _api.ForBlockchain;
            
            if (getApi.ChainSpec == null) throw new StepDependencyException(nameof(getApi.ChainSpec));
            if (getApi.DbProvider == null) throw new StepDependencyException(nameof(getApi.DbProvider));
            if (getApi.SpecProvider == null) throw new StepDependencyException(nameof(getApi.SpecProvider));
            
            ILogger logger = getApi.LogManager.GetClassLogger();
            IInitConfig initConfig = getApi.Config<IInitConfig>();
            ISyncConfig syncConfig = getApi.Config<ISyncConfig>();
            IPruningConfig pruningConfig = getApi.Config<IPruningConfig>();

            if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
            {
                logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
                syncConfig.DownloadBodiesInFastSync = true;
            }
            
            Account.AccountStartNonce = getApi.ChainSpec.Parameters.AccountStartNonce;

            IWitnessCollector witnessCollector = setApi.WitnessCollector = syncConfig.WitnessProtocolEnabled
                ? new WitnessCollector(getApi.DbProvider.WitnessDb, _api.LogManager)
                    .WithPruning(getApi.BlockTree!, getApi.LogManager)
                : NullWitnessCollector.Instance;

            setApi.MainStateDbWithCache = getApi.DbProvider.StateDb
                .Innermost // TODO: PRUNING what a hack here just to pass the actual DB
                .Cached(Trie.MemoryAllowance.TrieNodeCacheCount)
                .WitnessedBy(witnessCollector);
            IKeyValueStore? codeDb = getApi.DbProvider.CodeDb
                .Innermost // TODO: PRUNING what a hack here just to pass the actual DB
                .WitnessedBy(witnessCollector);

            ITrieStore trieStore;
            if (pruningConfig.Enabled)
            {
                trieStore = setApi.TrieStore = new TrieStore(
                    setApi.MainStateDbWithCache,
                    Prune.WhenCacheReaches(pruningConfig.CacheMb.MB()), // TODO: memory hint should define this
                    Persist.IfBlockOlderThan(pruningConfig.PersistenceInterval), // TODO: this should be based on time
                    getApi.LogManager);
            }
            else
            {
                trieStore = setApi.TrieStore = new TrieStore(
                    setApi.MainStateDbWithCache, // TODO: PRUNING what a hack here just to pass the actual DB
                    No.Pruning,
                    Persist.EveryBlock,
                    getApi.LogManager);
            }
            
            getApi.DisposeStack.Push(trieStore);
            trieStore.ReorgBoundaryReached += ReorgBoundaryReached;
            var readOnlyTrieStore = setApi.ReadOnlyTrieStore = new ReadOnlyTrieStore(trieStore);

            IStateProvider stateProvider = setApi.StateProvider = new StateProvider(
                trieStore,
                codeDb,
                getApi.LogManager);

            ReadOnlyDbProvider readOnly = new ReadOnlyDbProvider(getApi.DbProvider, false);
            
            PersistentTxStorage txStorage = new PersistentTxStorage(getApi.DbProvider.PendingTxsDb);
            IStateReader stateReader = setApi.StateReader = new StateReader(readOnlyTrieStore, readOnly.GetDb<ISnapshotableDb>(DbNames.Code), getApi.LogManager);
            
            setApi.ChainHeadStateProvider = new ChainHeadReadOnlyStateProvider(getApi.BlockTree, stateReader);
            Account.AccountStartNonce = getApi.ChainSpec.Parameters.AccountStartNonce;

            stateProvider.StateRoot = getApi.BlockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;

            if (_api.Config<IInitConfig>().DiagnosticMode == DiagnosticMode.VerifyTrie)
            {
                logger.Info("Collecting trie stats and verifying that no nodes are missing...");
                TrieStats stats = _api.StateProvider.CollectStats(getApi.DbProvider.CodeDb, _api.LogManager);
                logger.Info($"Starting from {getApi.BlockTree.Head?.Number} {getApi.BlockTree.Head?.StateRoot}{Environment.NewLine}" + stats);
            }

            // Init state if we need system calls before actual processing starts
            if (getApi.BlockTree!.Head != null)
            {
                stateProvider.StateRoot = getApi.BlockTree.Head.StateRoot;
            }
            
            var txPool = _api.TxPool = CreateTxPool(txStorage);

            var onChainTxWatcher = new OnChainTxWatcher(getApi.BlockTree, txPool, getApi.SpecProvider, _api.LogManager);
            getApi.DisposeStack.Push(onChainTxWatcher);

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(getApi.EthereumEcdsa, txPool, getApi.SpecProvider, getApi.LogManager));
            
            var storageProvider = setApi.StorageProvider = new StorageProvider(
                _api.TrieStore,
                stateProvider,
                getApi.LogManager);

            // blockchain processing
            BlockhashProvider blockhashProvider = new BlockhashProvider(
                getApi.BlockTree, getApi.LogManager);

            VirtualMachine virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                getApi.SpecProvider,
                getApi.LogManager);

            _api.TransactionProcessor = new TransactionProcessor(
                getApi.SpecProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                getApi.LogManager);

            InitSealEngine();
            if (_api.SealValidator == null) throw new StepDependencyException(nameof(_api.SealValidator));

            /* validation */
            var headerValidator = setApi.HeaderValidator = CreateHeaderValidator();

            OmmersValidator ommersValidator = new OmmersValidator(
                getApi.BlockTree,
                headerValidator,
                getApi.LogManager);

            TxValidator txValidator = new TxValidator(getApi.SpecProvider.ChainId);
            
            var blockValidator = setApi.BlockValidator = new BlockValidator(
                txValidator,
                headerValidator,
                ommersValidator,
                getApi.SpecProvider,
                getApi.LogManager);
                
            setApi.TxPoolInfoProvider = new TxPoolInfoProvider(stateReader, txPool);
            var mainBlockProcessor = setApi.MainBlockProcessor = CreateBlockProcessor();

            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(
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
                BeamBlockchainProcessor beamBlockchainProcessor = new BeamBlockchainProcessor(
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
            TxSealer standardSealer = new TxSealer(txSigner, getApi.Timestamper);
            NonceReservingTxSealer nonceReservingTxSealer =
                new NonceReservingTxSealer(txSigner, getApi.Timestamper, txPool);
            setApi.TxSender = new TxPoolSender(txPool, nonceReservingTxSealer, standardSealer);

            // TODO: possibly hide it (but need to confirm that NDM does not really need it)
            var filterStore = setApi.FilterStore = new FilterStore();
            setApi.FilterManager = new FilterManager(filterStore, mainBlockProcessor, txPool, getApi.LogManager);
            return Task.CompletedTask;
        }
        
        private void ReorgBoundaryReached(object? sender, ReorgBoundaryReached e)
        {
            _api.LogManager.GetClassLogger().Warn($"Saving reorg boundary {e.BlockNumber}");
            (_api.BlockTree as BlockTree)!.SavePruningReorganizationBoundary(e.BlockNumber);
        }

        protected virtual TxPool.TxPool CreateTxPool(PersistentTxStorage txStorage) =>
            new TxPool.TxPool(
                txStorage,
                _api.EthereumEcdsa,
                _api.SpecProvider,
                _api.Config<ITxPoolConfig>(),
                _api.ChainHeadStateProvider,
                _api.LogManager,
                CreateTxPoolTxComparer());

        protected IComparer<Transaction> CreateTxPoolTxComparer() => TxPool.TxPool.DefaultComparer;

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
                _api.RewardCalculatorSource.Get(_api.TransactionProcessor),
                _api.TransactionProcessor,
                _api.StateProvider,
                _api.StorageProvider,
                _api.TxPool,
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
