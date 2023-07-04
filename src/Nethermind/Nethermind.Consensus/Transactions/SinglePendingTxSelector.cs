// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class SinglePendingTxSelector : ITxSource
    {
        private readonly ITxSource _innerSource;

        public SinglePendingTxSelector(ITxSource innerSource)
        {
            _innerSource = innerSource ?? throw new ArgumentNullException(nameof(innerSource));
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit) =>
            _innerSource.GetTransactions(parent, gasLimit)
                .OrderBy(t => t.Nonce)
                .ThenByDescending(t => t.Timestamp)
                .Take(1);

        public override string ToString() => $"{nameof(SinglePendingTxSelector)} [ {_innerSource} ]";

    }
}
