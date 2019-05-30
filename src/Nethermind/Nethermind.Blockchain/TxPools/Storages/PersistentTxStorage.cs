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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Blockchain.TxPools.Storages
{
    public class PersistentTxStorage : ITxStorage
    {
        private readonly IDb _database;
        private readonly ISpecProvider _specProvider;

        public PersistentTxStorage(IDb database, ISpecProvider specProvider)
        {
            _database = database;
            _specProvider = specProvider;
        }

        public Transaction Get(Keccak hash)
            => Decode(_database.Get(hash));

        public Transaction[] GetAll()
        {
            var transactionsBytes = _database.GetAll();
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
            => bytes == null
                ? null
                : Rlp.Decode<Transaction>(new Rlp(bytes), RlpBehaviors.Storage);

        public void Add(Transaction transaction, long blockNumber)
        {
            if (transaction == null)
            {
                throw new ArgumentNullException(nameof(transaction));
            }

            var spec = _specProvider.GetSpec(blockNumber);
            _database.Set(transaction.Hash,
                Rlp.Encode(transaction, spec.IsEip658Enabled
                    ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage
                    : RlpBehaviors.Storage).Bytes);
        }

        public void Delete(Keccak hash)
            => _database.Remove(hash.Bytes);
    }
}