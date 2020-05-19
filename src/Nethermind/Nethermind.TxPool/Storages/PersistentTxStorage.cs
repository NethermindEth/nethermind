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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool.Storages
{
    public class PersistentTxStorage : ITxStorage
    {
        private readonly IDb _database;

        public PersistentTxStorage(IDb database)
        {
            _database = database;
        }

        public Transaction Get(Keccak hash)
            => Decode(_database.Get(hash));

        public Transaction[] GetAll()
        {
            var transactionsBytes = _database.GetAllValues().ToArray();
            if (transactionsBytes.Length == 0)
            {
                return Array.Empty<Transaction>();
            }

            var transactions = new Transaction[transactionsBytes.Length];
            for (var i = 0; i < transactionsBytes.Length; i++)
            {
                transactions[i] = Decode(transactionsBytes[i]);
            }

            return transactions;
        }

        private static Transaction Decode(byte[] bytes)
            => bytes == null ? null : Rlp.Decode<Transaction>(new Rlp(bytes));

        public void Add(Transaction transaction)
        {
            if (transaction == null)
            {
                throw new ArgumentNullException(nameof(transaction));
            }
            
            _database.Set(transaction.Hash, Rlp.Encode(transaction, RlpBehaviors.None).Bytes);
        }

        public void Delete(Keccak hash)
            => _database.Remove(hash.Bytes);
    }
}