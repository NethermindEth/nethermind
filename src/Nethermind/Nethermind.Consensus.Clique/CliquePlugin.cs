// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Clique
{
    public class CliquePlugin(ChainSpec chainSpec) : IConsensusPlugin
    {
        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus Engine";

        public string Author => "Nethermind";

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;

            (IApiWithStores getFromApi, IApiWithBlockchain setInApi) = _nethermindApi.ForInit;

            _snapshotManager = nethermindApi.Context.Resolve<ISnapshotManager>();
            _cliqueConfig = nethermindApi.Context.Resolve<ICliqueConfig>();

            setInApi.BlockPreprocessor.AddLast(new AuthorRecoveryStep(_snapshotManager));

            return Task.CompletedTask;
        }

        public IBlockProducer InitBlockProducer()
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.Clique)
            {
                return null;
            }

            (IApiWithBlockchain getFromApi, IApiWithBlockchain setInApi) = _nethermindApi!.ForProducer;

            _blocksConfig = getFromApi.Config<IBlocksConfig>();
            IMiningConfig miningConfig = getFromApi.Config<IMiningConfig>();

            if (!miningConfig.Enabled)
            {
                throw new InvalidOperationException("Request to start block producer while mining disabled.");
            }

            BlockProducerEnv env = getFromApi.BlockProducerEnvFactory.Create();

            IBlockchainProcessor chainProcessor = env.ChainProcessor;

            ITxSource txPoolTxSource = env.TxSource;

            IGasLimitCalculator gasLimitCalculator = new TargetAdjustedGasLimitCalculator(getFromApi.SpecProvider, _blocksConfig);

            CliqueBlockProducer blockProducer = new(
                txPoolTxSource,
                chainProcessor,
                env.ReadOnlyStateProvider,
                getFromApi.Timestamper,
                getFromApi.CryptoRandom,
                _snapshotManager!,
                getFromApi.Sealer!,
                gasLimitCalculator,
                getFromApi.SpecProvider,
                _cliqueConfig!,
                getFromApi.LogManager);

            return blockProducer;
        }

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
        {
            _blockProducerRunner = new CliqueBlockProducerRunner(
                _nethermindApi.BlockTree,
                _nethermindApi.Timestamper,
                _nethermindApi.CryptoRandom,
                _snapshotManager,
                (CliqueBlockProducer)blockProducer,
                _cliqueConfig,
                _nethermindApi.LogManager);
            _nethermindApi.DisposeStack.Push(_blockProducerRunner);
            return _blockProducerRunner;
        }

        public Task InitRpcModules()
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.Clique)
            {
                return Task.CompletedTask;
            }

            (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;
            CliqueRpcModule cliqueRpcModule = new(
                _blockProducerRunner,
                _snapshotManager!,
                getFromApi.BlockTree!);

            SingletonModulePool<ICliqueRpcModule> modulePool = new(cliqueRpcModule);
            getFromApi.RpcModuleProvider!.Register(modulePool);

            return Task.CompletedTask;
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.Clique;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        private INethermindApi? _nethermindApi;

        private ISnapshotManager? _snapshotManager;

        private ICliqueConfig? _cliqueConfig;

        private IBlocksConfig? _blocksConfig;
        private CliqueBlockProducerRunner _blockProducerRunner = null!;

        public IModule Module => new CliqueModule();
    }

    public class CliqueModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder
                .Map<CliqueChainSpecEngineParameters, ChainSpec>(chainSpec =>
                    chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<CliqueChainSpecEngineParameters>())

                .AddDecorator<ICliqueConfig>((ctx, cfg) =>
                {
                    CliqueChainSpecEngineParameters? param = ctx.Resolve<CliqueChainSpecEngineParameters>();
                    cfg.BlockPeriod = param.Period;
                    cfg.Epoch = param.Epoch;

                    return cfg;
                })

                .AddSingleton<ISnapshotManager, SnapshotManager>()
                .AddSingleton<ISealValidator, CliqueSealValidator>()
                .AddSingleton<ISealer, CliqueSealer>()

                .AddSingleton<IHealthHintService, CliqueHealthHintService>()
                ;
        }
    }
}
