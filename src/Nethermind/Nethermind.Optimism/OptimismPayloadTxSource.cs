// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism;

public class OptimismPayloadTxSource : ITxSource
{
    public bool SupportsBlobs => false;

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes, bool filterSource)
    {
        if (payloadAttributes is OptimismPayloadAttributes optimismPayloadAttributes)
        {
            Transaction[]? transactions = optimismPayloadAttributes.GetTransactions();
            if (transactions is not null)
            {
                return transactions;
            }
        }

        return [];
    }
}
