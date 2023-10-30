// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class SinglePendingTxSelector : ITxSource
    {
        private readonly ITxSource _innerSource;

        public SinglePendingTxSelector(ITxSource innerSource)
        {
            _innerSource = innerSource;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null) =>
            _innerSource.GetTransactions(parent, gasLimit, payloadAttributes)
                .OrderBy(t => t.Nonce)
                .ThenByDescending(t => t.Timestamp)
                .Take(1);

        public override string ToString() => $"{nameof(SinglePendingTxSelector)} [ {_innerSource} ]";

    }
}
