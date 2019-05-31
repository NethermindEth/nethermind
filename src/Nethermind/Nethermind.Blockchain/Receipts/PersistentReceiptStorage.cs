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
using Nethermind.Store;

namespace Nethermind.Blockchain.Receipts
{
    public class PersistentReceiptStorage : IReceiptStorage
    {
        private readonly IDb _database;
        private readonly ISpecProvider _specProvider;

        public PersistentReceiptStorage(IDb database, ISpecProvider specProvider)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public TxReceipt Find(Keccak hash)
        {
            var receiptData = _database.Get(hash);

            return receiptData == null
                ? null
                : Rlp.Decode<TxReceipt>(new Rlp(receiptData), RlpBehaviors.Storage);
        }

        public void Insert(TxReceipt txReceipt)
        {
            if (txReceipt == null)
            {
                throw new ArgumentNullException(nameof(txReceipt));
            }

            var spec = _specProvider.GetSpec(txReceipt.BlockNumber);
            _database.Set(txReceipt.TransactionHash,
                Rlp.Encode(txReceipt, spec.IsEip658Enabled
                    ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage
                    : RlpBehaviors.Storage).Bytes);
        }

        public long? LowestInsertedReceiptBlock => long.MaxValue;
    }
}