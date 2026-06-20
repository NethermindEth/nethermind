// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Api
{
    public interface IApiWithBlockchain : IApiWithStores
    {
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForInit => (this, this);
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForBlockchain => (this, this);

        CompositeBlockPreprocessorStep BlockPreprocessor { get; }
        IBlockProducer? BlockProducer { get; set; }
        IBlockProducerRunner BlockProducerRunner { get; set; }

        IManualBlockProductionTrigger ManualBlockProductionTrigger { get; }
        IMainProcessingContext MainProcessingContext { get; }
        ITxSender? TxSender { get; set; }
        INonceManager? NonceManager { get; set; }
        ITxPool? TxPool { get; set; }

        ITransactionComparerProvider? TransactionComparerProvider { get; set; }

        [SkipServiceCollection]
        TxValidator? TxValidator { get; }

        [SkipServiceCollection]
        ITxValidator? HeadTxValidator { get; }

        IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        IBackgroundTaskScheduler BackgroundTaskScheduler { get; set; }
        ICensorshipDetector CensorshipDetector { get; set; }
    }
}
