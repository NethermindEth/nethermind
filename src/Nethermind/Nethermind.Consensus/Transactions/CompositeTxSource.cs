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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class CompositeTxSource : ITxSource
    {
        private readonly ITxSource[] _transactionSources;

        public CompositeTxSource(params ITxSource[] transactionSources)
        {
            _transactionSources = transactionSources ?? throw new ArgumentNullException(nameof(transactionSources));
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            for (int i = 0; i < _transactionSources.Length; i++)
            {
                var transactions = _transactionSources[i].GetTransactions(parent, gasLimit);
                if (transactions != null)
                {
                    foreach (var tx in transactions)
                    {
                        gasLimit -= tx.GasLimit;
                        yield return tx;
                    }
                }
            }
        }
        
        private readonly int _id = ITxSource.IdCounter;
        public override string ToString() => $"{GetType().Name}_{_id} [ {(string.Join(", ", _transactionSources.Cast<object>()))} ]";

    }
}
