// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Api
{
    public interface IApiWithBlockchain : IApiWithStores
    {
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForInit => (this, this);
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForBlockchain => (this, this);
        (IApiWithBlockchain GetFromApi, IApiWithBlockchain SetInApi) ForProducer => (this, this);

        CompositeBlockPreprocessorStep BlockPreprocessor { get; }
        IGenesisPostProcessor GenesisPostProcessor { get; set; }
        IBlockProcessingQueue BlockProcessingQueue { get; }
        IBlockProducer? BlockProducer { get; set; }
        IBlockProducerRunner BlockProducerRunner { get; set; }

        IEnode? Enode { get; set; }

        IManualBlockProductionTrigger ManualBlockProductionTrigger { get; }
        ISealer Sealer { get; }
        ISealEngine SealEngine { get; }
        IStateReader? StateReader { get; }

        IWorldStateManager? WorldStateManager { get; }
        IMainProcessingContext MainProcessingContext { get; }
        ITxSender? TxSender { get; set; }
        INonceManager? NonceManager { get; set; }
        ITxPool? TxPool { get; set; }
        CompositeTxGossipPolicy TxGossipPolicy { get; }
        ITransactionComparerProvider? TransactionComparerProvider { get; set; }

        [SkipServiceCollection]
        TxValidator? TxValidator { get; }

        [SkipServiceCollection]
        ITxValidator? HeadTxValidator { get; }

        /// <summary>
        /// Manager of block finalization
        /// </summary>
        /// <remarks>
        /// Currently supported in <see cref="SealEngineType.AuRa"/> and Eth2Merge.
        /// </remarks>
        IBlockFinalizationManager? FinalizationManager { get; set; }

        IBlockProducerEnvFactory BlockProducerEnvFactory { get; }

        IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        IBackgroundTaskScheduler BackgroundTaskScheduler { get; set; }
        ICensorshipDetector CensorshipDetector { get; set; }
    }
}
