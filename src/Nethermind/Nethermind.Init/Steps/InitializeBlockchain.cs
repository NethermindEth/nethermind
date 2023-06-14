// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Services;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Converters;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.State.Witnesses;
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
            InitBlockTraceDumper();

            (IApiWithStores getApi, IApiWithBlockchain setApi) = _api.ForBlockchain;

            if (getApi.ChainSpec is null) throw new StepDependencyException(nameof(getApi.ChainSpec));
            if (getApi.DbProvider is null) throw new StepDependencyException(nameof(getApi.DbProvider));
            if (getApi.SpecProvider is null) throw new StepDependencyException(nameof(getApi.SpecProvider));
            if (getApi.BlockTree is null) throw new StepDependencyException(nameof(getApi.BlockTree));

            _logger = getApi.LogManager.GetClassLogger();
            IInitConfig initConfig = getApi.Config<IInitConfig>();
            ISyncConfig syncConfig = getApi.Config<ISyncConfig>();
            IPruningConfig pruningConfig = getApi.Config<IPruningConfig>();
            IBlocksConfig blocksConfig = getApi.Config<IBlocksConfig>();
            IMiningConfig miningConfig = getApi.Config<IMiningConfig>();

            if (syncConfig.DownloadReceiptsInFastSync && !syncConfig.DownloadBodiesInFastSync)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(syncConfig.DownloadReceiptsInFastSync)} is selected but {nameof(syncConfig.DownloadBodiesInFastSync)} - enabling bodies to support receipts download.");
                syncConfig.DownloadBodiesInFastSync = true;
            }

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

            CachingStore cachedStateDb = getApi.DbProvider.StateDb
                .Cached(Trie.MemoryAllowance.TrieNodeCacheCount);
            setApi.MainStateDbWithCache = cachedStateDb;
            IKeyValueStore codeDb = getApi.DbProvider.CodeDb
                .WitnessedBy(witnessCollector);

            TrieStore trieStore;
            IKeyValueStoreWithBatching stateWitnessedBy = setApi.MainStateDbWithCache.WitnessedBy(witnessCollector);
            if (pruningConfig.Mode.IsMemory())
            {
                IPersistenceStrategy persistenceStrategy = Persist.IfBlockOlderThan(pruningConfig.PersistenceInterval); // TODO: this should be based on time
                if (pruningConfig.Mode.IsFull())
                {
                    PruningTriggerPersistenceStrategy triggerPersistenceStrategy = new((IFullPruningDb)getApi.DbProvider!.StateDb, getApi.BlockTree!, getApi.LogManager);
                    getApi.DisposeStack.Push(triggerPersistenceStrategy);
                    persistenceStrategy = persistenceStrategy.Or(triggerPersistenceStrategy);
                }

                setApi.TrieStore = trieStore = new TrieStore(
                    stateWitnessedBy,
                    Prune.WhenCacheReaches(pruningConfig.CacheMb.MB()), // TODO: memory hint should define this
                    persistenceStrategy,
                    getApi.LogManager);

                if (pruningConfig.Mode.IsFull())
                {
                    IFullPruningDb fullPruningDb = (IFullPruningDb)getApi.DbProvider!.StateDb;
                    fullPruningDb.PruningStarted += (_, args) =>
                    {
                        cachedStateDb.PersistCache(args.Context);
                        trieStore.PersistCache(args.Context, args.Context.CancellationTokenSource.Token);
                    };
                }
            }
            else
            {
                setApi.TrieStore = trieStore = new TrieStore(
                    stateWitnessedBy,
                    No.Pruning,
                    Persist.EveryBlock,
                    getApi.LogManager);
            }

            TrieStoreBoundaryWatcher trieStoreBoundaryWatcher = new(trieStore, _api.BlockTree!, _api.LogManager);
            getApi.DisposeStack.Push(trieStoreBoundaryWatcher);
            getApi.DisposeStack.Push(trieStore);

            ITrieStore readOnlyTrieStore = setApi.ReadOnlyTrieStore = trieStore.AsReadOnly(cachedStateDb);

            IWorldState worldState = setApi.WorldState = new WorldState(
                trieStore,
                codeDb,
                getApi.LogManager);

            ReadOnlyDbProvider readOnly = new(getApi.DbProvider, false);

            IStateReader stateReader = setApi.StateReader = new StateReader(readOnlyTrieStore, readOnly.GetDb<IDb>(DbNames.Code), getApi.LogManager);

            setApi.TransactionComparerProvider = new TransactionComparerProvider(getApi.SpecProvider!, getApi.BlockTree.AsReadOnly());
            setApi.ChainHeadStateProvider = new ChainHeadReadOnlyStateProvider(getApi.BlockTree, stateReader);

            worldState.StateRoot = getApi.BlockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash;

            if (_api.Config<IInitConfig>().DiagnosticMode == DiagnosticMode.VerifyTrie)
            {
                Task.Run(() =>
                {
                    try
                    {
                        _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
                        TrieStore noPruningStore = new(stateWitnessedBy, No.Pruning, Persist.EveryBlock, getApi.LogManager);
                        IWorldState diagStateProvider = new WorldState(noPruningStore, codeDb, getApi.LogManager)
                        {
                            StateRoot = getApi.BlockTree!.Head?.StateRoot ?? Keccak.EmptyTreeHash
                        };
                        TrieStats stats = diagStateProvider.CollectStats(getApi.DbProvider.CodeDb, _api.LogManager);
                        _logger.Info($"Starting from {getApi.BlockTree.Head?.Number} {getApi.BlockTree.Head?.StateRoot}{Environment.NewLine}" + stats);
                    }
                    catch (Exception ex)
                    {
                        _logger!.Error(ex.ToString());
                    }
                });
            }

            // Init state if we need system calls before actual processing starts
            if (getApi.BlockTree!.Head?.StateRoot is not null)
            {
                worldState.StateRoot = getApi.BlockTree.Head.StateRoot;
            }

            TxValidator txValidator = setApi.TxValidator = new TxValidator(getApi.SpecProvider.ChainId);

            ITxPool txPool = _api.TxPool = CreateTxPool();

            ReceiptCanonicalityMonitor receiptCanonicalityMonitor = new(getApi.BlockTree, getApi.ReceiptStorage, _api.LogManager);
            getApi.DisposeStack.Push(receiptCanonicalityMonitor);
            _api.ReceiptMonitor = receiptCanonicalityMonitor;

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(getApi.EthereumEcdsa, txPool, getApi.SpecProvider, getApi.LogManager));

            // blockchain processing
            BlockhashProvider blockhashProvider = new(
                getApi.BlockTree, getApi.LogManager);

            VirtualMachine virtualMachine = new(
                blockhashProvider,
                getApi.SpecProvider,
                getApi.LogManager);

            _api.TransactionProcessor = new TransactionProcessor(
                getApi.SpecProvider,
                worldState,
                virtualMachine,
                getApi.LogManager);

            InitSealEngine();
            if (_api.SealValidator is null) throw new StepDependencyException(nameof(_api.SealValidator));

            setApi.HeaderValidator = CreateHeaderValidator();

            IHeaderValidator? headerValidator = setApi.HeaderValidator;
            IUnclesValidator unclesValidator = setApi.UnclesValidator = new UnclesValidator(
                getApi.BlockTree,
                headerValidator,
                getApi.LogManager);

            setApi.BlockValidator = new BlockValidator(
                txValidator,
                headerValidator,
                unclesValidator,
                getApi.SpecProvider,
                getApi.LogManager);

            IChainHeadInfoProvider chainHeadInfoProvider =
                new ChainHeadInfoProvider(getApi.SpecProvider, getApi.BlockTree, stateReader);

            // TODO: can take the tx sender from plugin here maybe
            ITxSigner txSigner = new WalletTxSigner(getApi.Wallet, getApi.SpecProvider.ChainId);
            TxSealer nonceReservingTxSealer =
                new(txSigner, getApi.Timestamper);
            INonceManager nonceManager = new NonceManager(chainHeadInfoProvider.AccountStateProvider);
            setApi.NonceManager = nonceManager;
            setApi.TxSender = new TxPoolSender(txPool, nonceReservingTxSealer, nonceManager, getApi.EthereumEcdsa!);

            setApi.TxPoolInfoProvider = new TxPoolInfoProvider(chainHeadInfoProvider.AccountStateProvider, txPool);
            setApi.GasPriceOracle = new GasPriceOracle(getApi.BlockTree, getApi.SpecProvider, _api.LogManager, blocksConfig.MinGasPrice);
            IBlockProcessor mainBlockProcessor = setApi.MainBlockProcessor = CreateBlockProcessor();

            BlockchainProcessor blockchainProcessor = new(
                getApi.BlockTree,
                mainBlockProcessor,
                _api.BlockPreprocessor,
                stateReader,
                getApi.LogManager,
                new BlockchainProcessor.Options
                {
                    StoreReceiptsByDefault = initConfig.StoreReceipts,
                    DumpOptions = initConfig.AutoDump
                });

            setApi.BlockProcessingQueue = blockchainProcessor;
            setApi.BlockchainProcessor = blockchainProcessor;

            IFilterStore filterStore = setApi.FilterStore = new FilterStore();
            setApi.FilterManager = new FilterManager(filterStore, mainBlockProcessor, txPool, getApi.LogManager);
            setApi.HealthHintService = CreateHealthHintService();
            setApi.BlockProductionPolicy = new BlockProductionPolicy(miningConfig);

            InitializeFullPruning(pruningConfig, initConfig, _api, stateReader);

            return Task.CompletedTask;
        }

        private static void InitializeFullPruning(
            IPruningConfig pruningConfig,
            IInitConfig initConfig,
            INethermindApi api,
            IStateReader stateReader)
        {
            IPruningTrigger? CreateAutomaticTrigger(string dbPath)
            {
                long threshold = pruningConfig.FullPruningThresholdMb.MB();

                switch (pruningConfig.FullPruningTrigger)
                {
                    case FullPruningTrigger.StateDbSize:
                        return new PathSizePruningTrigger(dbPath, threshold, api.TimerFactory, api.FileSystem);
                    case FullPruningTrigger.VolumeFreeSpace:
                        return new DiskFreeSpacePruningTrigger(dbPath, threshold, api.TimerFactory, api.FileSystem);
                    default:
                        return null;
                }
            }

            if (pruningConfig.Mode.IsFull())
            {
                IDb stateDb = api.DbProvider!.StateDb;
                if (stateDb is IFullPruningDb fullPruningDb)
                {
                    string pruningDbPath = fullPruningDb.GetPath(initConfig.BaseDbPath);
                    IPruningTrigger? pruningTrigger = CreateAutomaticTrigger(pruningDbPath);
                    if (pruningTrigger is not null)
                    {
                        api.PruningTrigger.Add(pruningTrigger);
                    }

                    IDriveInfo drive = api.FileSystem.GetDriveInfos(pruningDbPath)[0];
                    FullPruner pruner = new(fullPruningDb, api.PruningTrigger, pruningConfig, api.BlockTree!,
                        stateReader, api.ProcessExit!, ChainSizes.CreateChainSizeInfo(api.ChainSpec.ChainId),
                        drive, api.LogManager);
                    api.DisposeStack.Push(pruner);
                }
            }
        }

        private static void InitBlockTraceDumper()
        {
            EthereumJsonSerializer.AddConverter(new TxReceiptConverter());
        }

        protected virtual IHealthHintService CreateHealthHintService() =>
            new HealthHintService(_api.ChainSpec!);

        protected virtual TxPool.TxPool CreateTxPool() =>
            new(_api.EthereumEcdsa!,
                new ChainHeadInfoProvider(_api.SpecProvider!, _api.BlockTree!, _api.StateReader!),
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator!,
                _api.LogManager,
                CreateTxPoolTxComparer());

        protected IComparer<Transaction> CreateTxPoolTxComparer() => _api.TransactionComparerProvider!.GetDefaultComparer();

        // TODO: we should not have the create header -> we should have a header that also can use the information about the transitions
        protected virtual IHeaderValidator CreateHeaderValidator() => new HeaderValidator(
            _api.BlockTree,
            _api.SealValidator,
            _api.SpecProvider,
            _api.LogManager);

        // TODO: remove from here - move to consensus?
        protected virtual BlockProcessor CreateBlockProcessor()
        {
            if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.TransactionProcessor is null) throw new StepDependencyException(nameof(_api.TransactionProcessor));

            return new BlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(_api.TransactionProcessor!),
                new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor, _api.WorldState!),
                _api.WorldState,
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
