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
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Ethash
{
    public class NethDevPlugin(ChainSpec chainSpec) : IConsensusPlugin
    {
        public const string NethDev = "NethDev";
        private INethermindApi? _nethermindApi;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => NethDev;

        public string Description => $"{NethDev} (Spaceneth)";

        public string Author => "Nethermind";

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
            return Task.CompletedTask;
        }

        public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
        {
            var (getFromApi, _) = _nethermindApi!.ForProducer;

            ILogger logger = getFromApi.LogManager.GetClassLogger();
            if (logger.IsInfo) logger.Info("Starting Neth Dev block producer & sealer");

            BlockProducerEnv env = getFromApi.BlockProducerEnvFactory.Create(additionalTxSource);
            IBlockProducer blockProducer = new DevBlockProducer(
                env.TxSource,
                env.ChainProcessor,
                env.ReadOnlyStateProvider,
                getFromApi.BlockTree,
                getFromApi.Timestamper,
                getFromApi.SpecProvider,
                getFromApi.Config<IBlocksConfig>(),
                getFromApi.LogManager);

            return blockProducer;
        }

        public string SealEngineType => NethDev;

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
        {
            IBlockProductionTrigger trigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
                .IfPoolIsNotEmpty(_nethermindApi.TxPool)
                .Or(_nethermindApi.ManualBlockProductionTrigger);
            return new StandardBlockProducerRunner(
                trigger,
                _nethermindApi.BlockTree,
                blockProducer);
        }

        public IModule Module => new NethDevPluginModule();

        private class NethDevPluginModule : Module
        {
            protected override void Load(ContainerBuilder builder)
            {
                base.Load(builder);

                builder
                    .AddSingleton<ITxPoolTxSourceFactory, NethDevTxPoolTxSourceFactory>()
                    .AddSingleton<IBlockProducerEnvFactory, NethDevBlockProducerEnvFactory>()
                    ;
            }
        }
    }

    public class NethDevTxPoolTxSourceFactory(
        ISpecProvider specProvider,
        ITxPool txPool,
        ITransactionComparerProvider transactionComparerProvider,
        IBlocksConfig blocksConfig,
        ILogManager logManager) : ITxPoolTxSourceFactory
    {
        public TxPoolTxSource Create()
        {
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(logManager)
                .WithBaseFeeFilter(specProvider)
                .WithNullTxFilter()
                .WithMinGasPriceFilter(blocksConfig, specProvider)
                .Build;

            return new TxPoolTxSource (
                txPool,
                specProvider,
                transactionComparerProvider!,
                logManager,
                txFilterPipeline);
        }
    }

    public class NethDevBlockProducerEnvFactory : BlockProducerEnvFactory
    {
        public NethDevBlockProducerEnvFactory(IWorldStateManager worldStateManager, IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IBlockTree blockTree, ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculatorSource rewardCalculatorSource, IBlockPreprocessorStep blockPreprocessorStep, IBlocksConfig blocksConfig, ITxPoolTxSourceFactory txPoolTxSourceFactory, ILogManager logManager) : base(worldStateManager, readOnlyTxProcessingEnvFactory, blockTree, specProvider, blockValidator, rewardCalculatorSource, blockPreprocessorStep, blocksConfig, txPoolTxSourceFactory, logManager)
        {
        }

        protected override ITxSource CreateTxSourceForProducer(ITxSource? additionalTxSource)
        {
            return base.CreateTxSourceForProducer(additionalTxSource).ServeTxsOneByOne();
        }

        protected override BlockProcessor CreateBlockProcessor(IReadOnlyTxProcessingScope scope)
        {
            return new BlockProcessor(
                _specProvider,
                _blockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockProductionTransactionsExecutor(scope, _specProvider, _logManager),
                scope.WorldState,
                NullReceiptStorage.Instance,
                new BeaconBlockRootHandler(scope.TransactionProcessor, scope.WorldState),
                new BlockhashStore(_specProvider, scope.WorldState),
                _logManager,
                // TODO: Parent use `BlockProductionWithdrawalProcessor`. Should this be the same also?
                new WithdrawalProcessor(scope.WorldState, _logManager),
                new ExecutionRequestsProcessor(scope.TransactionProcessor)
            );
        }
    }
}
