// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;

namespace Nethermind.Optimism;

public class OptimismTxPoolTxSource : ITxSource
{
    private readonly ITxSource _baseTxSource;

    public OptimismTxPoolTxSource(ITxSource baseTxSource)
    {
        _baseTxSource = baseTxSource;
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes) =>
        payloadAttributes is OptimismPayloadAttributes { NoTxPool: false }
            ? _baseTxSource.GetTransactions(parent, gasLimit, payloadAttributes)
            : Enumerable.Empty<Transaction>();
}
