//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Synchronization.BeamSync;
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
            var (_get, _set) = _api.ForBlockchain;
            
            if (_get.ChainSpec == null) throw new StepDependencyException(nameof(_get.ChainSpec));
            if (_get.DbProvider == null) throw new StepDependencyException(nameof(_get.DbProvider));
            if (_get.SpecProvider == null) throw new StepDependencyException(nameof(_get.SpecProvider));

            ILogger logger = _get.LogManager.GetClassLogger();
            IInitConfig initConfig = _get.Config<IInitConfig>();
            ISyncConfig syncConfig = _get.Config<ISyncConfig>();
            IPruningConfig pruningConfig = _get.Config<IPruningConfig>();
            if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
            {
                logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
                syncConfig.DownloadBodiesInFastSync = true;
            }

            Account.AccountStartNonce = _get.ChainSpec.Parameters.AccountStartNonce;
            
            if (pruningConfig.Enabled)
            {
                _api.TrieStore = new TrieStore(
                    _get.DbProvider!.StateDb.Innermost, // TODO: PRUNING what a hack here just to pass the actual DB
                    new MemoryLimit(pruningConfig.PruningCacheMb * 1.MB()), // TODO: memory hint should define this
                    new ConstantInterval(pruningConfig.PruningPersistenceInterval), // TODO: this should be based on time
                    _api.LogManager);
            }
            else
            {
                _api.TrieStore = new TrieStore(
                    _get.DbProvider!.StateDb.Innermost, // TODO: PRUNING what a hack here just to pass the actual DB
                    new MemoryLimit(512.MB()),
                    Full.Archive,
                    _api.LogManager);
            }

            _api.DisposeStack.Push(_api.TrieStore);
            _api.ReadOnlyTrieStore = new ReadOnlyTrieStore(_api.TrieStore);
            _api.TrieStore.ReorgBoundaryReached += ReorgBoundaryReached;

            IStateProvider stateProvider = _api.StateProvider = new StateProvider(
                _api.TrieStore,
                _get.DbProvider.CodeDb,
                _get.LogManager);
            
            IReadOnlyDbProvider readOnly = new ReadOnlyDbProvider(_api.DbProvider, false);
            var stateReader = _set.StateReader = new StateReader(_api.ReadOnlyTrieStore, readOnly.CodeDb, _api.LogManager);
            _set.ChainHeadStateProvider = new ChainHeadReadOnlyStateProvider(_get.BlockTree, stateReader);

            PersistentTxStorage txStorage = new PersistentTxStorage(_get.DbProvider.PendingTxsDb);

            _api.StateProvider.StateRoot = _get.BlockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;

            if (_api.Config<IInitConfig>().DiagnosticMode == DiagnosticMode.VerifyTrie)
            {
                logger.Info("Collecting trie stats and verifying that no nodes are missing...");
                TrieStats stats = _api.StateProvider.CollectStats(_get.DbProvider.CodeDb, _api.LogManager);
                logger.Info($"Starting from {_get.BlockTree.Head?.Number} {_get.BlockTree.Head?.StateRoot}{Environment.NewLine}" + stats);
            }

            // Init state if we need system calls before actual processing starts
            if (_get.BlockTree!.Head != null)
            {
                stateProvider.StateRoot = _get.BlockTree.Head.StateRoot;
            }

            ITxPool txPool = _api.TxPool = CreateTxPool(txStorage);
            
            var onChainTxWatcher = new OnChainTxWatcher(_get.BlockTree, txPool, _get.SpecProvider);
            _get.DisposeStack.Push(onChainTxWatcher);

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(_get.EthereumEcdsa, txPool, _get.SpecProvider, _get.LogManager));

            IStorageProvider storageProvider = _api.StorageProvider = new StorageProvider(
                _api.TrieStore,
                _api.StateProvider,
                _api.LogManager);

            // blockchain processing
            BlockhashProvider blockhashProvider = new BlockhashProvider(
                _get.BlockTree, _get.LogManager);

            VirtualMachine virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                _get.SpecProvider,
                _get.LogManager);

            _api.TransactionProcessor = new TransactionProcessor(
                _get.SpecProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                _get.LogManager);

            InitSealEngine();
            if (_api.SealValidator == null) throw new StepDependencyException(nameof(_api.SealValidator));

            /* validation */
            IHeaderValidator headerValidator = _set.HeaderValidator = CreateHeaderValidator();

            OmmersValidator ommersValidator = new OmmersValidator(
                _get.BlockTree,
                headerValidator,
                _get.LogManager);

            TxValidator txValidator = new TxValidator(_get.SpecProvider.ChainId);

            IBlockValidator blockValidator = _set.BlockValidator = new BlockValidator(
                txValidator,
                headerValidator,
                ommersValidator,
                _get.SpecProvider,
                _get.LogManager);
            
            _api.TxPoolInfoProvider = new TxPoolInfoProvider(_api.StateReader, _api.TxPool);

            IBlockProcessor mainBlockProcessor = _set.MainBlockProcessor = CreateBlockProcessor();

            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                _get.BlockTree,
                mainBlockProcessor,
                _api.BlockPreprocessor,
                _get.LogManager,
                new BlockchainProcessor.Options
                {
                    AutoProcess = !syncConfig.BeamSync,
                    StoreReceiptsByDefault = initConfig.StoreReceipts,
                });

            _set.BlockProcessingQueue = blockchainProcessor;
            _set.BlockchainProcessor = blockchainProcessor;

            if (syncConfig.BeamSync)
            {
                BeamBlockchainProcessor beamBlockchainProcessor = new BeamBlockchainProcessor(
                    new ReadOnlyDbProvider(_api.DbProvider, false),
                    _get.BlockTree,
                    _get.SpecProvider,
                    _get.LogManager,
                    blockValidator,
                    _api.BlockPreprocessor,
                    _api.RewardCalculatorSource!, // TODO: does it work with AuRa?
                    blockchainProcessor,
                    _get.SyncModeSelector!);

                _api.DisposeStack.Push(beamBlockchainProcessor);
            }

            // TODO: can take the tx sender from plugin here maybe
            ITxSigner txSigner = new WalletTxSigner(_get.Wallet, _get.SpecProvider.ChainId);
            TxSealer standardSealer = new TxSealer(txSigner, _get.Timestamper);
            NonceReservingTxSealer nonceReservingTxSealer =
                new NonceReservingTxSealer(txSigner, _get.Timestamper, txPool);
            _set.TxSender = new TxPoolSender(txPool, nonceReservingTxSealer, standardSealer);

            // TODO: possibly hide it (but need to confirm that NDM does not really need it)
            _api.FilterStore = new FilterStore();
            _api.FilterManager = new FilterManager(_api.FilterStore, _api.MainBlockProcessor, _api.TxPool, _api.LogManager);

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
                _api.LogManager);
        }

        // TODO: remove from here - move to consensus?
        protected virtual void InitSealEngine()
        {
        }
    }
}
