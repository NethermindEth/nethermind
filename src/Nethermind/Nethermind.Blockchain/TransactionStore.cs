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
        private readonly IDb _txDb;
        private readonly IDb _receiptsDb;
        private readonly ISpecProvider _specProvider;
        private readonly ConcurrentDictionary<Keccak, Transaction> _pending = new ConcurrentDictionary<Keccak, Transaction>();

        public TransactionStore(IDb receiptsDb, IDb txDb, ISpecProvider specProvider)
        {
            _receiptsDb = receiptsDb;
            _txDb = txDb;
            _specProvider = specProvider;
        }
        
        public void StoreProcessedTransaction(Transaction transaction, TransactionReceipt receipt, Keccak blockHash, UInt256 blockNumber, int index)
        {
            if(receipt == null) throw new ArgumentNullException(nameof(receipt));
            if(transaction == null) throw new ArgumentNullException(nameof(transaction));
            
            IReleaseSpec spec = _specProvider.GetSpec(blockNumber);
            _receiptsDb.Set(transaction.Hash, Rlp.Encode(receipt, spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None).Bytes);
            
            TxInfo txInfo = new TxInfo(blockHash, blockNumber, index);
            _txDb.Set(transaction.Hash, Rlp.Encode(txInfo).Bytes);
        }

        public TransactionReceipt GetReceipt(Keccak txHash)
        {
            Rlp rlp = new Rlp(_receiptsDb.Get(txHash));
            return Rlp.Decode<TransactionReceipt>(rlp);
        }

        public TxInfo GetTxInfo(Keccak txHash)
        {
            Rlp rlp = new Rlp(_txDb.Get(txHash));
            return Rlp.Decode<TxInfo>(rlp);
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