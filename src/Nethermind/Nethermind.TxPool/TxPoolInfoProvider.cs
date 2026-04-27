// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public class TxPoolInfoProvider(IAccountStateProvider accountStateProvider, ITxPool txPool) : ITxPoolInfoProvider
    {

        public TxPoolInfoProvider(IChainHeadInfoProvider chainHeadInfoProvider, ITxPool txPool) : this(chainHeadInfoProvider.ReadOnlyStateProvider, txPool) { }

        public TxPoolInfo GetInfo()
        {
            // BTW this class should be rewritten or removed - a lot of unnecessary allocations
            Dictionary<AddressAsKey, Transaction[]> groupedTransactions = new();
            foreach ((AddressAsKey sender, Transaction[] transactions) in txPool.GetPendingTransactionsBySender())
            {
                groupedTransactions[sender] = Copy(transactions);
            }

            foreach ((AddressAsKey sender, Transaction[] blobTransactions) in txPool.GetPendingLightBlobTransactionsBySender())
            {
                if (groupedTransactions.TryGetValue(sender, out Transaction[]? existing))
                {
                    Transaction[] combined = new Transaction[existing.Length + blobTransactions.Length];
                    Array.Copy(existing, combined, existing.Length);
                    Array.Copy(blobTransactions, 0, combined, existing.Length, blobTransactions.Length);
                    groupedTransactions[sender] = combined;
                }
                else
                {
                    groupedTransactions[sender] = Copy(blobTransactions);
                }
            }

            Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> pendingTransactions = new();
            Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> queuedTransactions = new();
            foreach (KeyValuePair<AddressAsKey, Transaction[]> group in groupedTransactions)
            {
                Address address = group.Key;
                UInt256 accountNonce = accountStateProvider.GetNonce(address);
                UInt256 expectedNonce = accountNonce;
                Dictionary<ulong, Transaction> pending = new();
                Dictionary<ulong, Transaction> queued = new();
                Transaction[] transactionsOrderedByNonce = group.Value;
                Array.Sort(transactionsOrderedByNonce, static (left, right) => left.Nonce.CompareTo(right.Nonce));

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

        private static Transaction[] Copy(Transaction[] transactions)
        {
            Transaction[] copy = new Transaction[transactions.Length];
            Array.Copy(transactions, copy, transactions.Length);
            return copy;
        }
    }
}
