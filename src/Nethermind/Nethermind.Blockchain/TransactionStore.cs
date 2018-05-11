/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public class TransactionStore : ITransactionStore
    {
        private readonly ConcurrentDictionary<Keccak, Transaction> _pending = new ConcurrentDictionary<Keccak, Transaction>();
        private readonly ConcurrentDictionary<Keccak, Transaction> _transactions = new ConcurrentDictionary<Keccak, Transaction>();
        private readonly ConcurrentDictionary<Keccak, TransactionReceipt> _transactionRecepits = new ConcurrentDictionary<Keccak, TransactionReceipt>();
        private readonly ConcurrentBag<Keccak> _processedTransactions = new ConcurrentBag<Keccak>();
        private readonly ConcurrentDictionary<Keccak, Keccak> _blockHashes = new ConcurrentDictionary<Keccak, Keccak>();

        public void AddTransaction(Transaction transaction)
        {
            if (transaction.Hash == null)
            {
                throw new InvalidOperationException("Transaction hash is null when adding to the store.");
            }

            _transactions[transaction.Hash] = transaction;
        }

        public void AddTransactionReceipt(Keccak transactionHash, TransactionReceipt transactionReceipt, Keccak blockHash)
        {
            _transactionRecepits[transactionHash] = transactionReceipt;
            _blockHashes[transactionHash] = blockHash;
            _processedTransactions.Add(transactionHash);
        }

        public Transaction GetTransaction(Keccak transactionHash)
        {
            return _transactions.TryGetValue(transactionHash, out var transaction) ? transaction : null;
        }

        public TransactionReceipt GetTransactionReceipt(Keccak transactionHash)
        {
            return _transactionRecepits.TryGetValue(transactionHash, out var transaction) ? transaction : null;
        }

        public bool WasProcessed(Keccak transactionHash)
        {
            return _processedTransactions.Contains(transactionHash);
        }

        public Keccak GetBlockHash(Keccak transactionHash)
        {
            return _blockHashes.TryGetValue(transactionHash, out var blockHash) ? blockHash : null;
        }

        public AddTransactionResult AddPending(Transaction transaction)
        {
            if (_processedTransactions.Contains(transaction.Hash))
            {
                return AddTransactionResult.AlreadyProcessed;
            }

            if (_pending.ContainsKey(transaction.Hash))
            {
                return AddTransactionResult.AlreadyKnown;
            }

            _pending[transaction.Hash] = transaction;
            NewPending?.Invoke(this, new TransactionEventArgs(transaction));
            return AddTransactionResult.Added;
        }

        public void RemovePending(Transaction transaction)
        {
            if (_pending.ContainsKey(transaction.Hash))
            {
                _pending.TryRemove(transaction.Hash, out Transaction _);
            }
        }

        public Transaction[] GetAllPending()
        {
            var result = _pending.Values.ToArray();
            return result;
        }

        public event EventHandler<TransactionEventArgs> NewPending;
    }
}