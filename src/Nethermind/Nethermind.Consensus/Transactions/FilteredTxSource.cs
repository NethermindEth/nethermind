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
// 

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class FilteredTxSource : ITxSource
    {
        private readonly ITxSource _innerSource;
        private readonly ITxFilter _txFilter;

        public FilteredTxSource(ITxSource innerSource, ITxFilter txFilter)
        {
            _innerSource = innerSource;
            _txFilter = txFilter;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            foreach (Transaction transaction in _innerSource.GetTransactions(parent, gasLimit))
            {
                if (_txFilter.IsAllowed(transaction, parent))
                {
                    yield return transaction;
                }
            }
        }

        public override string ToString() => $"{nameof(FilteredTxSource)} [ {_innerSource} ]";
    }
}
