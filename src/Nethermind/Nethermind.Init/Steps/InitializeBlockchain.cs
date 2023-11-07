// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Wallet;
using LifetimeScope = Autofac.Core.Lifetime.LifetimeScope;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializePlugins), typeof(InitializeBlockTree), typeof(InitializeContainer), typeof(SetupKeyStore), typeof(InitializeStateDb))]
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

            if (getApi.ChainSpec is null) throw new StepDependencyException(nameof(getApi.ChainSpec));
            if (getApi.DbProvider is null) throw new StepDependencyException(nameof(getApi.DbProvider));
            if (getApi.SpecProvider is null) throw new StepDependencyException(nameof(getApi.SpecProvider));
            if (getApi.BlockTree is null) throw new StepDependencyException(nameof(getApi.BlockTree));

            setApi.TransactionComparerProvider = _api.Container.Resolve<ITransactionComparerProvider>();
            setApi.TxValidator = _api.Container.Resolve<TxValidator>();
            _api.ReceiptMonitor = _api.Container.Resolve<IReceiptMonitor>();

            IInitConfig initConfig = getApi.Config<IInitConfig>();
            IBlocksConfig blocksConfig = getApi.Config<IBlocksConfig>();
            ITxPool txPool = _api.TxPool = CreateTxPool();

            ILifetimeScope statefulContainer = _api.Container.BeginLifetimeScope(NethermindScope.WorldState);
            _api.DisposeStack.Push((IDisposable)statefulContainer);

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(getApi.EthereumEcdsa, txPool, getApi.SpecProvider, getApi.LogManager));
            _api.TransactionProcessor = statefulContainer.Resolve<ITransactionProcessor>();
            _api.SealEngine = _api.Container.Resolve<ISealEngine>();
            _api.SealValidator = _api.Container.Resolve<ISealValidator>();
            _api.Sealer = _api.Container.Resolve<ISealer>();
            _api.RewardCalculatorSource = _api.Container.Resolve<IRewardCalculatorSource>();

            setApi.HealthHintService = _api.Container.Resolve<IHealthHintService>();
            setApi.HeaderValidator = _api.Container.Resolve<IHeaderValidator>();

            setApi.UnclesValidator = CreateUnclesValidator();
            setApi.BlockValidator = CreateBlockValidator();

            IChainHeadInfoProvider chainHeadInfoProvider =
                new ChainHeadInfoProvider(getApi.SpecProvider, getApi.BlockTree, setApi.StateReader!);

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
                setApi.StateReader!,
                getApi.LogManager,
                new BlockchainProcessor.Options
                {
                    StoreReceiptsByDefault = initConfig.StoreReceipts,
                    DumpOptions = initConfig.AutoDump
                })
            {
                IsMainProcessor = true
            };

            setApi.BlockProcessingQueue = blockchainProcessor;
            setApi.BlockchainProcessor = blockchainProcessor;

            IFilterStore filterStore = setApi.FilterStore = new FilterStore();
            setApi.FilterManager = new FilterManager(filterStore, mainBlockProcessor, txPool, getApi.LogManager);
            setApi.BlockProductionPolicy = CreateBlockProductionPolicy();

            return Task.CompletedTask;
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

        protected virtual IBlockProductionPolicy CreateBlockProductionPolicy() =>
            new BlockProductionPolicy(_api.Config<IMiningConfig>());

        protected virtual TxPool.TxPool CreateTxPool() =>
            new(_api.EthereumEcdsa!,
                _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
                new ChainHeadInfoProvider(_api.SpecProvider!, _api.BlockTree!, _api.StateReader!),
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator!,
                _api.LogManager,
                CreateTxPoolTxComparer(),
                _api.TxGossipPolicy);

        protected IComparer<Transaction> CreateTxPoolTxComparer() => _api.TransactionComparerProvider!.GetDefaultComparer();

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
    }
}
