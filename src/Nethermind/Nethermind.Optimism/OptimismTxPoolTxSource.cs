// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism;

public class OptimismTxPoolTxSource : ITxSource
{
    private readonly ITxSource _baseTxSource;

    public bool SupportsBlobs => _baseTxSource.SupportsBlobs;

    public OptimismTxPoolTxSource(ITxSource baseTxSource)
    {
        _baseTxSource = baseTxSource;
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes, bool filterSource) =>
        payloadAttributes is OptimismPayloadAttributes { NoTxPool: true }
            ? []
            : _baseTxSource.GetTransactions(parent, gasLimit, payloadAttributes, filterSource);
}
