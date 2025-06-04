// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeBlockProducerTxSourceFactory(Func<StartBlockProducerAuRa> startBlockProducerFactory) : IBlockProducerTxSourceFactory
{
    public ITxSource Create()
    {
        return startBlockProducerFactory().CreateTxPoolTxSource();
    }
}
