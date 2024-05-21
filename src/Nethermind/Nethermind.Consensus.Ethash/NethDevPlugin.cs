// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Ethash
{
    public class NethDevPlugin : IConsensusPlugin
    {
        private INethermindApi? _nethermindApi;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => "NethDev";

        public string Description => "NethDev (Spaceneth)";

        public string Author => "Nethermind";

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
            return Task.CompletedTask;
        }

        public IBlockProducerEnvFactory? BuildBlockProducerEnvFactory()
        {
            return new NethBlockProducerEnvFactory(
                _nethermindApi.WorldStateManager!,
                _nethermindApi.BlockTree!,
                _nethermindApi.SpecProvider!,
                _nethermindApi.BlockValidator!,
                _nethermindApi.RewardCalculatorSource!,
                // So it does not have receipt here, but for some reason, by default `BlockProducerEnvFactory` have real receipt store.
                NullReceiptStorage.Instance,
                _nethermindApi.BlockPreprocessor,
                _nethermindApi.TxPool!,
                _nethermindApi.TransactionComparerProvider!,
                _nethermindApi.Config<IBlocksConfig>(),
                _nethermindApi.LogManager);
        }

        public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi!.SealEngineType != Core.SealEngineType.NethDev)
            {
                return null;
            }

            var (getFromApi, _) = _nethermindApi!.ForProducer;

            ILogger logger = getFromApi.LogManager.GetClassLogger();
            if (logger.IsInfo) logger.Info("Starting Neth Dev block producer & sealer");

            BlockProducerEnv env = _nethermindApi.BlockProducerEnvFactory.Create(additionalTxSource);

            IBlockProducer blockProducer = new DevBlockProducer(
                env.TxSource,
                env.ChainProcessor,
                env.ReadOnlyTxProcessingEnv.StateProvider,
                getFromApi.BlockTree,
                getFromApi.Timestamper,
                getFromApi.SpecProvider,
                getFromApi.Config<IBlocksConfig>(),
                getFromApi.LogManager);

            return blockProducer;
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.NethDev;
        public IBlockProducerRunner CreateBlockProducerRunner()
        {
            IBlockProductionTrigger trigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
                .IfPoolIsNotEmpty(_nethermindApi.TxPool)
                .Or(_nethermindApi.ManualBlockProductionTrigger);
            return new StandardBlockProducerRunner(
                trigger,
                _nethermindApi.BlockTree,
                _nethermindApi.BlockProducer!);
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }

        private class NethBlockProducerEnvFactory : BlockProducerEnvFactory
        {
            public NethBlockProducerEnvFactory(IWorldStateManager worldStateManager, IBlockTree blockTree, ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculatorSource rewardCalculatorSource, IReceiptStorage receiptStorage, IBlockPreprocessorStep blockPreprocessorStep, ITxPool txPool, ITransactionComparerProvider transactionComparerProvider, IBlocksConfig blocksConfig, ILogManager logManager, IBlockTransactionsExecutorFactory transactionExecutorFactory = null) : base(worldStateManager, blockTree, specProvider, blockValidator, rewardCalculatorSource, receiptStorage, blockPreprocessorStep, txPool, transactionComparerProvider, blocksConfig, logManager, transactionExecutorFactory)
            {
            }

            protected override ITxSource CreateTxSourceForProducer(
                ITxSource? additionalTxSource,
                ReadOnlyTxProcessingEnv processingEnv,
                ITxPool txPool,
                IBlocksConfig blocksConfig,
                ITransactionComparerProvider transactionComparerProvider,
                ILogManager logManager)
            {
                TxPoolTxSource txPoolSource = CreateTxPoolTxSource(processingEnv, txPool, blocksConfig, transactionComparerProvider, logManager);
                return additionalTxSource.Then(txPoolSource).ServeTxsOneByOne();
            }

            protected override ITxFilterPipeline CreateTxSourceFilter(IBlocksConfig blocksConfig)
            {
                return new TxFilterPipelineBuilder(_logManager)
                    .WithBaseFeeFilter(_specProvider)
                    .WithNullTxFilter()
                    .WithMinGasPriceFilter(blocksConfig, _specProvider)
                    .Build;
            }

            protected override IWithdrawalProcessor? CreateWithdrawalProcessor(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv)
            {
                return null;
            }
        }

    }
}
