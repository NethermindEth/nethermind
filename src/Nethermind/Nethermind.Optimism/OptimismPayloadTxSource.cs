// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;

namespace Nethermind.Optimism;

public class OptimismPayloadTxSource : ITxSource
{
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes)
    {
        if (payloadAttributes is OptimismPayloadAttributes optimismPayloadAttributes)
        {
            Transaction[]? transactions = optimismPayloadAttributes.GetTransactions();
            if (transactions is not null)
            {
                return transactions;
            }
        }

        return Enumerable.Empty<Transaction>();
    }
}
