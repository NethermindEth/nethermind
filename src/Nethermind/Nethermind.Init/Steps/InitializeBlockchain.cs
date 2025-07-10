// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
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
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
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
    public class InitializeBlockchain(INethermindApi api) : IStep
    {
        private readonly INethermindApi _api = api;

        public async Task Execute(CancellationToken _)
        {
            await InitBlockchain();
        }

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        protected virtual Task InitBlockchain()
        {
            (IApiWithStores getApi, IApiWithBlockchain setApi) = _api.ForBlockchain;
            setApi.TransactionComparerProvider = new TransactionComparerProvider(getApi.SpecProvider!, getApi.BlockTree!.AsReadOnly());

            IInitConfig initConfig = getApi.Config<IInitConfig>();
            IBlocksConfig blocksConfig = getApi.Config<IBlocksConfig>();
            IReceiptConfig receiptConfig = getApi.Config<IReceiptConfig>();

            ThisNodeInfo.AddInfo("Gaslimit     :", $"{blocksConfig.TargetBlockGasLimit:N0}");
            ThisNodeInfo.AddInfo("ExtraData    :", Utf8.IsValid(blocksConfig.GetExtraDataBytes()) ?
                blocksConfig.ExtraData :
                "- binary data -");

            IStateReader stateReader = setApi.StateReader!;
            IWorldState mainWorldState = _api.WorldStateManager!.GlobalWorldState;
            PreBlockCaches? preBlockCaches = (mainWorldState as IPreBlockCaches)?.Caches;
            CodeInfoRepository codeInfoRepository = new(preBlockCaches?.PrecompileCache);
            ITxPool txPool = _api.TxPool = CreateTxPool(codeInfoRepository);

            ReceiptCanonicalityMonitor receiptCanonicalityMonitor = new(getApi.ReceiptStorage, _api.LogManager);
            getApi.DisposeStack.Push(receiptCanonicalityMonitor);
            _api.ReceiptMonitor = receiptCanonicalityMonitor;

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(getApi.EthereumEcdsa, getApi.SpecProvider, getApi.LogManager));

            VirtualMachine.WarmUpEvmInstructions();
            VirtualMachine virtualMachine = CreateVirtualMachine(codeInfoRepository, mainWorldState);
            ITransactionProcessor transactionProcessor = CreateTransactionProcessor(codeInfoRepository, virtualMachine, mainWorldState);

            InitSealEngine();
            if (_api.SealValidator is null) throw new StepDependencyException(nameof(_api.SealValidator));

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
                ? new(new ReadOnlyTxProcessingEnvFactory(
                        _api.WorldStateManager!,
                        _api.BlockTree!.AsReadOnly(),
                        _api.SpecProvider!,
                        _api.LogManager),
                    mainWorldState,
                    _api.SpecProvider!,
                    blocksConfig,
                    _api.LogManager,
                    preBlockCaches)
                : null;

            IBlockProcessor mainBlockProcessor = CreateBlockProcessor(preWarmer, transactionProcessor, mainWorldState);

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

            getApi.DisposeStack.Push(blockchainProcessor);

            setApi.MainProcessingContext = new MainProcessingContext(
                transactionProcessor,
                mainBlockProcessor,
                blockchainProcessor,
                mainWorldState);
            setApi.BlockProcessingQueue = blockchainProcessor;

            IFilterStore filterStore = setApi.FilterStore = new FilterStore();
            setApi.FilterManager = new FilterManager(filterStore, mainBlockProcessor, txPool, getApi.LogManager);
            setApi.HealthHintService = CreateHealthHintService();
            setApi.BlockProductionPolicy = CreateBlockProductionPolicy();

            BackgroundTaskScheduler backgroundTaskScheduler = new BackgroundTaskScheduler(
                mainBlockProcessor,
                chainHeadInfoProvider,
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

        protected virtual ITransactionProcessor CreateTransactionProcessor(CodeInfoRepository codeInfoRepository, IVirtualMachine virtualMachine, IWorldState worldState)
        {
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

            return new TransactionProcessor(
                _api.SpecProvider,
                worldState,
                virtualMachine,
                codeInfoRepository,
                _api.LogManager);
        }

        protected VirtualMachine CreateVirtualMachine(CodeInfoRepository codeInfoRepository, IWorldState worldState)
        {
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.WorldStateManager is null) throw new StepDependencyException(nameof(_api.WorldStateManager));

            // blockchain processing
            BlockhashProvider blockhashProvider = new(
                _api.BlockTree, _api.SpecProvider, worldState, _api.LogManager);

            VirtualMachine virtualMachine = new(
                blockhashProvider,
                _api.SpecProvider,
                _api.LogManager);

            return virtualMachine;
        }

        protected virtual IHealthHintService CreateHealthHintService() =>
            new HealthHintService(_api.ChainSpec!);

        protected virtual IBlockProductionPolicy CreateBlockProductionPolicy() =>
            new BlockProductionPolicy(_api.Config<IMiningConfig>());

        protected virtual ITxPool CreateTxPool(CodeInfoRepository codeInfoRepository)
        {
            TxPool.TxPool txPool = new(_api.EthereumEcdsa!,
                _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
                new ChainHeadInfoProvider(_api.SpecProvider!, _api.BlockTree!, _api.StateReader!, codeInfoRepository),
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator!,
                _api.LogManager,
                CreateTxPoolTxComparer(),
                _api.TxGossipPolicy);

            _api.DisposeStack.Push(txPool);
            return txPool;
        }

        protected IComparer<Transaction> CreateTxPoolTxComparer() => _api.TransactionComparerProvider!.GetDefaultComparer();

        // TODO: remove from here - move to consensus?
        protected virtual BlockProcessor CreateBlockProcessor(BlockCachePreWarmer? preWarmer, ITransactionProcessor transactionProcessor, IWorldState worldState)
        {
            if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.WorldStateManager is null) throw new StepDependencyException(nameof(_api.WorldStateManager));
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

            return new BlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(transactionProcessor),
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, worldState),
                worldState,
                _api.ReceiptStorage,
                transactionProcessor,
                new BeaconBlockRootHandler(transactionProcessor, worldState),
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
