// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Era1;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Api
{
    public interface IApiWithBlockchain : IApiWithStores, IBlockchainBridgeFactory
    {
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForInit => (this, this);
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForBlockchain => (this, this);
        (IApiWithBlockchain GetFromApi, IApiWithBlockchain SetInApi) ForProducer => (this, this);

        CompositeBlockPreprocessorStep BlockPreprocessor { get; }
        IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        IBlockProducer? BlockProducer { get; set; }
        IBlockProducerRunner? BlockProducerRunner { get; set; }
        IBlockValidator? BlockValidator { get; set; }
        IEnode? Enode { get; set; }
        IFilterStore? FilterStore { get; set; }
        IFilterManager? FilterManager { get; set; }
        IUnclesValidator? UnclesValidator { get; set; }
        IHeaderValidator? HeaderValidator { get; set; }
        IManualBlockProductionTrigger ManualBlockProductionTrigger { get; }
        IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        /// <summary>
        /// PoS switcher for The Merge
        /// </summary>
        IPoSSwitcher PoSSwitcher { get; set; }
        ISealer? Sealer { get; set; }
        ISealValidator? SealValidator { get; set; }
        ISealEngine SealEngine { get; set; }
        IReadOnlyStateProvider? ChainHeadStateProvider { get; set; }
        IStateReader? StateReader { get; set; }

        IWorldStateManager? WorldStateManager { get; set; }
        INodeStorage? MainNodeStorage { get; set; }
        CompositePruningTrigger? PruningTrigger { get; set; }
        IVerifyTrieStarter? VerifyTrieStarter { get; set; }
        IMainProcessingContext? MainProcessingContext { get; set; }
        ITxSender? TxSender { get; set; }
        INonceManager? NonceManager { get; set; }
        ITxPool? TxPool { get; set; }
        ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        CompositeTxGossipPolicy TxGossipPolicy { get; }
        IHealthHintService? HealthHintService { get; set; }
        IRpcCapabilitiesProvider? RpcCapabilitiesProvider { get; set; }
        ITransactionComparerProvider? TransactionComparerProvider { get; set; }
        TxValidator? TxValidator { get; set; }

        /// <summary>
        /// Manager of block finalization
        /// </summary>
        /// <remarks>
        /// Currently supported in <see cref="SealEngineType.AuRa"/> and Eth2Merge.
        /// </remarks>
        IBlockFinalizationManager? FinalizationManager { get; set; }

        IGasLimitCalculator? GasLimitCalculator { get; set; }

        IBlockProducerEnvFactory? BlockProducerEnvFactory { get; set; }
        IBlockImprovementContextFactory? BlockImprovementContextFactory { get; set; }

        IGasPriceOracle? GasPriceOracle { get; set; }

        IEthSyncingInfo? EthSyncingInfo { get; set; }


        IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        BackgroundTaskScheduler BackgroundTaskScheduler { get; set; }
        CensorshipDetector CensorshipDetector { get; set; }

        IAdminEraService AdminEraService { get; set; }

        public ContainerBuilder ConfigureContainerBuilderFromApiWithBlockchain(ContainerBuilder builder)
        {
            return ConfigureContainerBuilderFromApiWithStores(builder)
                .AddPropertiesFrom<IApiWithBlockchain>(this)
                .AddSingleton<INodeStorage>(MainNodeStorage!);
        }
    }
}
