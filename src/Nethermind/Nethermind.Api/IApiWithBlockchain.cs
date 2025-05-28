// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
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

        [SkipServiceCollection]
        IBlockValidator BlockValidator { get; }

        IEnode? Enode { get; set; }
        IFilterStore? FilterStore { get; set; }
        IFilterManager? FilterManager { get; set; }

        [SkipServiceCollection]
        IUnclesValidator? UnclesValidator { get; }

        [SkipServiceCollection]
        IHeaderValidator? HeaderValidator { get; }
        IManualBlockProductionTrigger ManualBlockProductionTrigger { get; }
        IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        ISealer? Sealer { get; set; }
        ISealValidator? SealValidator { get; set; }
        ISealEngine SealEngine { get; set; }
        IStateReader? StateReader { get; }

        IWorldStateManager? WorldStateManager { get; }
        IMainProcessingContext? MainProcessingContext { get; set; }
        ITxSender? TxSender { get; set; }
        INonceManager? NonceManager { get; set; }
        ITxPool? TxPool { get; set; }
        ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        CompositeTxGossipPolicy TxGossipPolicy { get; }
        IHealthHintService? HealthHintService { get; set; }
        IRpcCapabilitiesProvider? RpcCapabilitiesProvider { get; set; }
        ITransactionComparerProvider? TransactionComparerProvider { get; set; }

        [SkipServiceCollection]
        TxValidator? TxValidator { get; }

        /// <summary>
        /// Manager of block finalization
        /// </summary>
        /// <remarks>
        /// Currently supported in <see cref="SealEngineType.AuRa"/> and Eth2Merge.
        /// </remarks>
        IBlockFinalizationManager? FinalizationManager { get; set; }

        IBlockProducerEnvFactory? BlockProducerEnvFactory { get; set; }
        IBlockImprovementContextFactory? BlockImprovementContextFactory { get; set; }
        IReadOnlyTxProcessingEnvFactory ReadOnlyTxProcessingEnvFactory { get; }

        IGasPriceOracle? GasPriceOracle { get; set; }

        [SkipServiceCollection]
        IEthSyncingInfo? EthSyncingInfo { get; }


        IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        BackgroundTaskScheduler BackgroundTaskScheduler { get; set; }
        CensorshipDetector CensorshipDetector { get; set; }
    }
}
