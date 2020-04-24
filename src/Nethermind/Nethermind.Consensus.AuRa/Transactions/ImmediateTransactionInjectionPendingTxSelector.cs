//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class InjectionPendingTxSelector : IPendingTxSelector
    {
        private readonly IPendingTxSelector _innerPendingTxSelector;
        private readonly ITransactionFiller _transactionFiller;
        private readonly IImmediateTransactionSource[] _immediateTransactionSources;

        public InjectionPendingTxSelector(IPendingTxSelector innerPendingTxSelector, ITransactionFiller transactionFiller, params IImmediateTransactionSource[] immediateTransactionSources)
        {
            _innerPendingTxSelector = innerPendingTxSelector ?? throw new ArgumentNullException(nameof(innerPendingTxSelector));
            _transactionFiller = transactionFiller ?? throw new ArgumentNullException(nameof(_transactionFiller));
            _immediateTransactionSources = immediateTransactionSources;
        }

        public IEnumerable<Transaction> SelectTransactions(BlockHeader parent, long gasLimit)
        {
            for (int i = 0; i < _immediateTransactionSources.Length; i++)
            {
                if (_immediateTransactionSources[i].TryCreateTransaction(parent, gasLimit, out var tx))
                {
                    gasLimit -= (long) tx.GasPrice;
                    _transactionFiller.Fill(parent, tx);
                    yield return tx;
                }
            }
            
            foreach (var tx in _innerPendingTxSelector.SelectTransactions(parent, gasLimit)) yield return tx;
        }
    }
}