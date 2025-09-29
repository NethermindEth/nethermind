// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(
        typeof(InitializePlugins),
        typeof(InitializeBlockTree),
        typeof(SetupKeyStore),
        typeof(InitializePrecompiles)
    )]
    public class InitializeBlockchain(INethermindApi api, CompliantNodeFilters compliantNodeFilters) : IStep
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

            ThisNodeInfo.AddInfo("Gaslimit     :", $"{blocksConfig.TargetBlockGasLimit:N0}");
            ThisNodeInfo.AddInfo("ExtraData    :", Utf8.IsValid(blocksConfig.GetExtraDataBytes()) ?
                blocksConfig.ExtraData :
                "- binary data -");

            IStateReader stateReader = setApi.StateReader!;

            // The main one
            ICodeInfoRepository codeInfoRepository =
                _api.Context.ResolveNamed<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState));

            IChainHeadInfoProvider chainHeadInfoProvider =
                new ChainHeadInfoProvider(
                    new ChainHeadSpecProvider(getApi.SpecProvider!, getApi.BlockTree!),
                    getApi.BlockTree!, stateReader, codeInfoRepository);

            _api.TxGossipPolicy.Policies.Add(new SpecDrivenTxGossipPolicy(chainHeadInfoProvider));

            ITxPool txPool = _api.TxPool = CreateTxPool(chainHeadInfoProvider);

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(getApi.EthereumEcdsa, getApi.SpecProvider, getApi.LogManager));

            WarmupEvm();

            // TODO: can take the tx sender from plugin here maybe
            ITxSigner txSigner = new WalletTxSigner(getApi.Wallet, getApi.SpecProvider!.ChainId);
            TxSealer nonceReservingTxSealer =
                new(txSigner, getApi.Timestamper);
            INonceManager nonceManager = new NonceManager(chainHeadInfoProvider.ReadOnlyStateProvider);
            setApi.NonceManager = nonceManager;
            setApi.TxSender = new TxPoolSender(txPool, nonceReservingTxSealer, nonceManager, getApi.EthereumEcdsa!);
            setApi.BlockProductionPolicy = CreateBlockProductionPolicy();

            var mainBranchProcessor = setApi.MainProcessingContext.BranchProcessor;

            BackgroundTaskScheduler backgroundTaskScheduler = new BackgroundTaskScheduler(
                mainBranchProcessor,
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
                    mainBranchProcessor,
                    _api.LogManager,
                    censorshipDetectorConfig
                );
                setApi.CensorshipDetector = censorshipDetector;
                _api.DisposeStack.Push(censorshipDetector);
            }

            return Task.CompletedTask;
        }

        private void WarmupEvm()
        {
            IWorldState state = _api.WorldStateManager!.CreateResettableWorldState();
            using var _ = state.BeginScope(IWorldState.PreGenesis);
            VirtualMachine.WarmUpEvmInstructions(state, new EthereumCodeInfoRepository());
        }

        protected virtual IBlockProductionPolicy CreateBlockProductionPolicy() =>
            new BlockProductionPolicy(_api.Config<IMiningConfig>());

        protected virtual ITxPool CreateTxPool(IChainHeadInfoProvider chainHeadInfoProvider)
        {
            TxPool.TxPool txPool = new(_api.EthereumEcdsa!,
                _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
                chainHeadInfoProvider,
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator!,
                _api.LogManager,
                CreateTxPoolTxComparer(),
                _api.TxGossipPolicy,
                null,
                _api.HeadTxValidator,
                additionalPreHashFilters: compliantNodeFilters.Filters
            );

            _api.DisposeStack.Push(txPool);
            return txPool;
        }

        protected IComparer<Transaction> CreateTxPoolTxComparer() => _api.TransactionComparerProvider!.GetDefaultComparer();
    }
}
