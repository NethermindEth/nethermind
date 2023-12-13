// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            // only std txs are picked here. Should we add blobs?
            // BTW this class should be rewritten or removed - a lot of unnecessary allocations
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
                    ulong transactionNonce = (ulong)transaction.Nonce;
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

                if (pending.Count != 0)
                {
                    pendingTransactions[address] = pending;
                }

                if (queued.Count != 0)
                {
                    queuedTransactions[address] = queued;
                }
            }

            return new TxPoolInfo(pendingTransactions, queuedTransactions);
        }
    }
}
