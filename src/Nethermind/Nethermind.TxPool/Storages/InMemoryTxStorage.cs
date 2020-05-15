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

using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool.Storages
{
    public class InMemoryTxStorage : ITxStorage
    {
        private readonly ConcurrentDictionary<Keccak, Transaction> _transactions =
            new ConcurrentDictionary<Keccak, Transaction>();

        public Transaction Get(Keccak hash)
        {
            _transactions.TryGetValue(hash, out var transaction);

            return transaction;
        }

        public Transaction[] GetAll() => _transactions.Values.ToArray();

        public void Add(Transaction transaction)
            => _transactions.TryAdd(transaction.Hash, transaction);

        public void Delete(Keccak hash) => _transactions.TryRemove(hash, out _);
    }
}