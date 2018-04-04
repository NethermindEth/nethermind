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

using System.Collections.Generic;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public class TransactionStore : ITransactionStore
    {
        private readonly Dictionary<Keccak, TransactionInfo> _transactions = new Dictionary<Keccak, TransactionInfo>();

        private readonly ITransactionValidator _validator;

        public TransactionStore(ITransactionValidator validator)
        {
            _validator = validator;
        }

        public TransactionInfo Add(Transaction transaction, PublicKey receivedFrom)
        {
            _transactions.TryGetValue(transaction.Hash, out TransactionInfo info);
            if (info == null)
            {
                info = new TransactionInfo(transaction, receivedFrom);
                info.Quality = _validator.IsWellFormed(transaction) ? Quality.DataValid : Quality.Invalid;
            }

            return info;
        }

        public void MarkAsProcessed(Transaction transaction, bool isValid)
        {
            _transactions.TryGetValue(transaction.Hash, out TransactionInfo info);
            if (info != null)
            {
                info.Quality = isValid ? Quality.Processed : Quality.Invalid;
            }
        }
    }
}