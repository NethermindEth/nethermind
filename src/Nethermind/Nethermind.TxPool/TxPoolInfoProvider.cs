//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Linq;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    public class TxPoolInfoProvider : ITxPoolInfoProvider
    {
        private readonly IAccountStateProvider _stateReader;
        private readonly ITxPool _txPool;

        public TxPoolInfoProvider(IAccountStateProvider accountStateProvider, ITxPool txPool)
        {
            _stateReader = accountStateProvider ?? throw new ArgumentNullException(nameof(accountStateProvider));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public TxPoolInfo GetInfo()
        {
            var groupedTransactions = _txPool.GetPendingTransactionsBySender();
            var pendingTransactions = new Dictionary<Address, IDictionary<ulong, Transaction>>();
            var queuedTransactions = new Dictionary<Address, IDictionary<ulong, Transaction>>();
            foreach (KeyValuePair<Address, Transaction[]> group in groupedTransactions)
            {
                Address address = group.Key;
                var accountNonce = _stateReader.GetAccount(address).Nonce;
                var expectedNonce = accountNonce;
                var pending = new Dictionary<ulong, Transaction>();
                var queued = new Dictionary<ulong, Transaction>();
                var transactionsOrderedByNonce = group.Value.OrderBy(t => t.Nonce);

                foreach (var transaction in transactionsOrderedByNonce)
                {
                    ulong transactionNonce = (ulong) transaction.Nonce;
                    if (transaction.Nonce == expectedNonce)
                    {
                        pending.Add(transactionNonce, transaction);
                        expectedNonce = transaction.Nonce + 1;
                    }
                    else
                    {
                        queued.Add(transactionNonce, transaction);    
                    }
                }

                if (pending.Any())
                {
                    pendingTransactions[address] = pending;
                }

                if (queued.Any())
                {
                    queuedTransactions[address] = queued;
                }
            }

            return new TxPoolInfo(pendingTransactions, queuedTransactions);
        }
    }
}
