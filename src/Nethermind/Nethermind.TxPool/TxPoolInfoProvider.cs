// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public class TxPoolInfoProvider(IAccountStateProvider accountStateProvider, ITxPool txPool) : ITxPoolInfoProvider
    {

        public TxPoolInfoProvider(IChainHeadInfoProvider chainHeadInfoProvider, ITxPool txPool) : this(chainHeadInfoProvider.ReadOnlyStateProvider, txPool) { }

        public TxPoolInfo GetInfo()
        {
            // only std txs are picked here. Should we add blobs?
            // BTW this class should be rewritten or removed - a lot of unnecessary allocations
            IDictionary<AddressAsKey, Transaction[]> groupedTransactions = txPool.GetPendingTransactionsBySender();
            Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> pendingTransactions = new();
            Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> queuedTransactions = new();
            foreach (KeyValuePair<AddressAsKey, Transaction[]> group in groupedTransactions)
            {
                Address address = group.Key;
                UInt256 accountNonce = accountStateProvider.GetNonce(address);
                UInt256 expectedNonce = accountNonce;
                Dictionary<ulong, Transaction> pending = new();
                Dictionary<ulong, Transaction> queued = new();
                IOrderedEnumerable<Transaction> transactionsOrderedByNonce = group.Value.OrderBy(static t => t.Nonce);

                foreach (Transaction? transaction in transactionsOrderedByNonce)
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
