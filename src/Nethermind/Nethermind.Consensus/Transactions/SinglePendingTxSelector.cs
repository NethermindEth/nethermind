//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Int256;

namespace Nethermind.Consensus.Transactions
{
    public class SinglePendingTxSelector : ITxSource
    {
        private readonly ITxSource _innerSource;

        public SinglePendingTxSelector(ITxSource innerSource)
        {
            _innerSource = innerSource ?? throw new ArgumentNullException(nameof(innerSource));
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit) => 
            _innerSource.GetTransactions(parent, gasLimit)
                .OrderBy(t => t.Nonce)
                .ThenByDescending(t => t.Timestamp)
                .Take(1);
        
        public override string ToString() => $"{nameof(SinglePendingTxSelector)} [ {_innerSource} ]";

    }
}
