// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class CompositeTxSource : ITxSource
    {
        private readonly IList<ITxSource> _transactionSources;

        public CompositeTxSource(params ITxSource[] transactionSources)
        {
            _transactionSources = transactionSources?.ToList() ?? throw new ArgumentNullException(nameof(transactionSources));
        }

        public void Then(ITxSource txSource)
        {
            _transactionSources.Add(txSource);
        }

        public void First(ITxSource txSource)
        {
            _transactionSources.Insert(0, txSource);
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            for (int i = 0; i < _transactionSources.Count; i++)
            {
                IEnumerable<Transaction> transactions = _transactionSources[i].GetTransactions(parent, gasLimit);
                foreach (Transaction tx in transactions)
                {
                    yield return tx;
                }
            }
        }

        public override string ToString()
            => $"{nameof(CompositeTxSource)} [ {(string.Join(", ", _transactionSources.Cast<object>()))} ]";
    }
}
