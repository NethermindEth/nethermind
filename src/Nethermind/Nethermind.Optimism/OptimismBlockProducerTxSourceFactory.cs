// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Optimism;

public class OptimismBlockProducerTxSourceFactory(IBlockProducerTxSourceFactory baseTxSource) : IBlockProducerTxSourceFactory
{
    public ITxSource Create()
    {
        return new OptimismTxPoolTxSource(baseTxSource.Create());
    }
}
