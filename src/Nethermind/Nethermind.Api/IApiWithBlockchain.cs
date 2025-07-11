// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Nethermind.Blockchain;
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
        IBlockProducerRunner BlockProducerRunner { get; set; }

        [SkipServiceCollection]
        IBlockValidator BlockValidator { get; }

        IEnode? Enode { get; set; }

        IManualBlockProductionTrigger ManualBlockProductionTrigger { get; }
        IRewardCalculatorSource RewardCalculatorSource { get; }
        ISealer Sealer { get; }
        ISealValidator SealValidator { get; }
        ISealEngine SealEngine { get; }
        IStateReader? StateReader { get; }

        IWorldStateManager? WorldStateManager { get; }
        IMainProcessingContext? MainProcessingContext { get; set; }
        ITxSender? TxSender { get; set; }
        INonceManager? NonceManager { get; set; }
        ITxPool? TxPool { get; set; }
        CompositeTxGossipPolicy TxGossipPolicy { get; }
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

        IBlockProducerEnvFactory BlockProducerEnvFactory { get; }
        IReadOnlyTxProcessingEnvFactory ReadOnlyTxProcessingEnvFactory { get; }

        IGasPriceOracle GasPriceOracle { get; }

        IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        BackgroundTaskScheduler BackgroundTaskScheduler { get; set; }
        ICensorshipDetector CensorshipDetector { get; set; }
    }
}
