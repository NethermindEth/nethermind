// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(
        typeof(InitializeStateDb),
        typeof(InitializePlugins),
        typeof(InitializeBlockTree),
        typeof(SetupKeyStore),
        typeof(InitializePrecompiles)
    )]
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
        protected virtual Task InitBlockchain()
        {
            (IApiWithStores getApi, IApiWithBlockchain setApi) = _api.ForBlockchain;
            setApi.TransactionComparerProvider = new TransactionComparerProvider(getApi.SpecProvider!, getApi.BlockTree!.AsReadOnly());
            setApi.TxValidator = CreateTxValidator(_api.SpecProvider!.ChainId);

            IInitConfig initConfig = getApi.Config<IInitConfig>();
            IBlocksConfig blocksConfig = getApi.Config<IBlocksConfig>();
            IReceiptConfig receiptConfig = getApi.Config<IReceiptConfig>();

            IStateReader stateReader = setApi.StateReader!;
            PreBlockCaches? preBlockCaches = (_api.WorldStateManager?.GlobalWorldState as IPreBlockCaches)?.Caches;
            CodeInfoRepository codeInfoRepository = new(preBlockCaches?.PrecompileCache);
            ITxPool txPool = _api.TxPool = CreateTxPool(codeInfoRepository);

            ReceiptCanonicalityMonitor receiptCanonicalityMonitor = new(getApi.ReceiptStorage, _api.LogManager);
            getApi.DisposeStack.Push(receiptCanonicalityMonitor);
            _api.ReceiptMonitor = receiptCanonicalityMonitor;

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(getApi.EthereumEcdsa, txPool, getApi.SpecProvider, getApi.LogManager));


            VirtualMachine virtualMachine = CreateVirtualMachine(codeInfoRepository);
            _api.TransactionProcessor = CreateTransactionProcessor(codeInfoRepository, virtualMachine);

            InitSealEngine();
            if (_api.SealValidator is null) throw new StepDependencyException(nameof(_api.SealValidator));

            setApi.HeaderValidator = CreateHeaderValidator();
            setApi.UnclesValidator = CreateUnclesValidator();
            setApi.BlockValidator = CreateBlockValidator();

            IChainHeadInfoProvider chainHeadInfoProvider =
                new ChainHeadInfoProvider(getApi.SpecProvider!, getApi.BlockTree!, stateReader, codeInfoRepository);

            // TODO: can take the tx sender from plugin here maybe
            ITxSigner txSigner = new WalletTxSigner(getApi.Wallet, getApi.SpecProvider!.ChainId);
            TxSealer nonceReservingTxSealer =
                new(txSigner, getApi.Timestamper);
            INonceManager nonceManager = new NonceManager(chainHeadInfoProvider.ReadOnlyStateProvider);
            setApi.NonceManager = nonceManager;
            setApi.TxSender = new TxPoolSender(txPool, nonceReservingTxSealer, nonceManager, getApi.EthereumEcdsa!);

            setApi.TxPoolInfoProvider = new TxPoolInfoProvider(chainHeadInfoProvider.ReadOnlyStateProvider, txPool);
            setApi.GasPriceOracle = new GasPriceOracle(getApi.BlockTree!, getApi.SpecProvider, _api.LogManager, blocksConfig.MinGasPrice);
            BlockCachePreWarmer? preWarmer = blocksConfig.PreWarmStateOnBlockProcessing
                ? new(new(
                        _api.WorldStateManager!,
                        _api.BlockTree!,
                        _api.SpecProvider,
                        _api.LogManager,
                        _api.WorldStateManager!.GlobalWorldState),
                    _api.SpecProvider!,
                    _api.LogManager,
                    preBlockCaches)
                : null;
            IBlockProcessor mainBlockProcessor = setApi.MainBlockProcessor = CreateBlockProcessor(preWarmer);

            BlockchainProcessor blockchainProcessor = new(
                getApi.BlockTree,
                mainBlockProcessor,
                _api.BlockPreprocessor,
                stateReader,
                getApi.LogManager,
                new BlockchainProcessor.Options
                {
                    StoreReceiptsByDefault = receiptConfig.StoreReceipts,
                    DumpOptions = initConfig.AutoDump
                })
            {
                IsMainProcessor = true
            };

            setApi.BlockProcessingQueue = blockchainProcessor;
            setApi.BlockchainProcessor = blockchainProcessor;

            IJsonRpcConfig rpcConfig = _api.Config<IJsonRpcConfig>();
            IFilterStore filterStore = setApi.FilterStore = new FilterStore(getApi.TimerFactory, rpcConfig.FiltersTimeout);
            setApi.FilterManager = new FilterManager(filterStore, mainBlockProcessor, txPool, getApi.LogManager);
            setApi.HealthHintService = CreateHealthHintService();
            setApi.BlockProductionPolicy = CreateBlockProductionPolicy();
            _api.DisposeStack.Push(filterStore);

            BackgroundTaskScheduler backgroundTaskScheduler = new BackgroundTaskScheduler(
                mainBlockProcessor,
                initConfig.BackgroundTaskConcurrency,
                initConfig.BackgroundTaskMaxNumber,
                _api.LogManager);
            setApi.BackgroundTaskScheduler = backgroundTaskScheduler;
            _api.DisposeStack.Push(backgroundTaskScheduler);

            ICensorshipDetectorConfig censorshipDetectorConfig = _api.Config<ICensorshipDetectorConfig>();
            if (censorshipDetectorConfig.Enabled)
            {
                CensorshipDetector censorshipDetector = new(
                    _api.BlockTree!,
                    txPool,
                    CreateTxPoolTxComparer(),
                    mainBlockProcessor,
                    _api.LogManager,
                    censorshipDetectorConfig
                );
                setApi.CensorshipDetector = censorshipDetector;
                _api.DisposeStack.Push(censorshipDetector);
            }

            return Task.CompletedTask;
        }

        protected virtual TxValidator? CreateTxValidator(ulong v)
        {
            return new TxValidator(_api.SpecProvider!.ChainId);
        }

        protected virtual IBlockValidator CreateBlockValidator()
        {
            return new BlockValidator(
                _api.TxValidator,
                _api.HeaderValidator,
                _api.UnclesValidator,
                _api.SpecProvider,
                _api.LogManager);
        }

        protected virtual IUnclesValidator CreateUnclesValidator()
        {
            return new UnclesValidator(
                _api.BlockTree,
                _api.HeaderValidator,
                _api.LogManager);
        }

        protected virtual ITransactionProcessor CreateTransactionProcessor(CodeInfoRepository codeInfoRepository, VirtualMachine virtualMachine)
        {
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

            var txPoolConfig = _api.Config<ITxPoolConfig>();

            if (txPoolConfig.BlackListedSenderAddresses.Length != 0)
            {
                HashSet<AddressAsKey> blacklist = new(txPoolConfig.BlackListedSenderAddresses.Length);
                foreach (string address in txPoolConfig.BlackListedSenderAddresses)
                {
                    blacklist.Add(new AddressAsKey(new Address(address)));
                }

                return new TransactionProcessorWithBlocklist(_api.SpecProvider,
                    _api.WorldStateManager!.GlobalWorldState,
                    virtualMachine,
                    codeInfoRepository,
                    _api.LogManager, blacklist);
            }

            return new TransactionProcessor(
                _api.SpecProvider,
                _api.WorldStateManager!.GlobalWorldState,
                virtualMachine,
                codeInfoRepository,
                _api.LogManager);
        }

        protected VirtualMachine CreateVirtualMachine(CodeInfoRepository codeInfoRepository)
        {
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.WorldStateManager is null) throw new StepDependencyException(nameof(_api.WorldStateManager));

            // blockchain processing
            BlockhashProvider blockhashProvider = new(
                _api.BlockTree, _api.SpecProvider, _api.WorldStateManager.GlobalWorldState, _api.LogManager);

            VirtualMachine virtualMachine = new(
                blockhashProvider,
                _api.SpecProvider,
                codeInfoRepository,
                _api.LogManager);

            return virtualMachine;
        }

        protected virtual IHealthHintService CreateHealthHintService() =>
            new HealthHintService(_api.ChainSpec!);

        protected virtual IBlockProductionPolicy CreateBlockProductionPolicy() =>
            new BlockProductionPolicy(_api.Config<IMiningConfig>());

        protected virtual ITxPool CreateTxPool(CodeInfoRepository codeInfoRepository)
        {
            DebugModuleFactory debugModuleFactory = new(
                _api.WorldStateManager!,
                _api.DbProvider!,
                _api.BlockTree!,
                _api.ConfigProvider.GetConfig<JsonRpcConfig>(),
                _api.BlockValidator!,
                _api.BlockPreprocessor,
                _api.RewardCalculatorSource!,
                _api.ReceiptStorage!,
                new ReceiptMigration(_api),
                _api.ConfigProvider,
                _api.SpecProvider!,
                _api.SyncModeSelector,
                _api.BadBlocksStore!,
                _api.FileSystem,
                _api.LogManager);

            var callFilter = new CallFilter(_api.Config<ITxPoolConfig>().BlacklistedFunctionCalls, debugModuleFactory.CreateDebugBridge());
            return new TxPool.TxPool(_api.EthereumEcdsa!,
                _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
                new ChainHeadInfoProvider(_api.SpecProvider!, _api.BlockTree!, _api.StateReader!, codeInfoRepository),
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator!,
                _api.LogManager,
                CreateTxPoolTxComparer(),
                _api.TxGossipPolicy,
                preHashFilter: callFilter);
        }

        protected IComparer<Transaction> CreateTxPoolTxComparer() => _api.TransactionComparerProvider!.GetDefaultComparer();

        // TODO: we should not have the create header -> we should have a header that also can use the information about the transitions
        protected virtual IHeaderValidator CreateHeaderValidator() => new HeaderValidator(
            _api.BlockTree,
            _api.SealValidator,
            _api.SpecProvider,
            _api.LogManager);

        // TODO: remove from here - move to consensus?
        protected virtual BlockProcessor CreateBlockProcessor(BlockCachePreWarmer? preWarmer)
        {
            if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.TransactionProcessor is null) throw new StepDependencyException(nameof(_api.TransactionProcessor));
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.WorldStateManager is null) throw new StepDependencyException(nameof(_api.WorldStateManager));
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

            IWorldState worldState = _api.WorldStateManager.GlobalWorldState!;
            return new BlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(_api.TransactionProcessor!),
                new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor, worldState),
                worldState,
                _api.ReceiptStorage,
                _api.TransactionProcessor,
                new BeaconBlockRootHandler(_api.TransactionProcessor, worldState),
                new BlockhashStore(_api.SpecProvider!, worldState),
                _api.LogManager,
                preWarmer: preWarmer
            );
        }

        // TODO: remove from here - move to consensus?
        protected virtual void InitSealEngine()
        {
        }
    }
}
