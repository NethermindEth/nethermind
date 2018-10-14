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
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class TransactionStore : ITransactionStore
    {
        private readonly IDb _receiptsDb;
        private readonly ISpecProvider _specProvider;
        private readonly ConcurrentDictionary<Keccak, Transaction> _pending = new ConcurrentDictionary<Keccak, Transaction>();

        public TransactionStore(IDb receiptsDb, ISpecProvider specProvider)
        {
            _receiptsDb = receiptsDb;            
            _specProvider = specProvider;
        }
        
        public void StoreProcessedTransaction(Keccak txHash, TransactionReceipt receipt)
        {
            if(receipt == null) throw new ArgumentNullException(nameof(receipt));
            
            IReleaseSpec spec = _specProvider.GetSpec(receipt.BlockNumber);
            _receiptsDb.Set(txHash, Rlp.Encode(receipt, spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage: RlpBehaviors.Storage).Bytes);
        }

        public TransactionReceipt GetReceipt(Keccak txHash)
        {
            byte[] receiptData = _receiptsDb.Get(txHash);
            if (receiptData == null)
            {
                return null;
            }
            
            Rlp rlp = new Rlp(receiptData);
            return Rlp.Decode<TransactionReceipt>(rlp, RlpBehaviors.Storage);
        }

        public AddTransactionResult AddPending(Transaction transaction)
        {
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