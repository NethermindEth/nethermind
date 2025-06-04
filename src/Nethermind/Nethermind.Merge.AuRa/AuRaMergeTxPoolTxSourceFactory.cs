// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Producers;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeTxPoolTxSourceFactory(Func<StartBlockProducerAuRa> startBlockProducerFactory) : ITxPoolTxSourceFactory
{
    public TxPoolTxSource Create()
    {
        return startBlockProducerFactory().CreateTxPoolTxSource();
    }
}
