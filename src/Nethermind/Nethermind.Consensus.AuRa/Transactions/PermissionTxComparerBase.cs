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
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public abstract class PermissionTxComparerBase : IComparer<Transaction>
    {
        protected abstract bool IsWhiteListed(Transaction tx);
        
        protected abstract UInt256 GetPriority(Transaction tx);

        public int Compare(Transaction x, Transaction y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;
                
            // we already have nonce ordered by previous code, we don't deal with it here
                
            // first order by whitelisted
            int whitelistedComparision = IsWhiteListed(y).CompareTo(IsWhiteListed(x));
            if (whitelistedComparision != 0) return whitelistedComparision;
                
            // then order by priority descending
            int priorityComparision = GetPriority(y).CompareTo(GetPriority(x));
            if (priorityComparision != 0) return priorityComparision;
                
            // then by gas price descending
            int gasPriceComparison = y.GasPrice.CompareTo(x.GasPrice);
            if (gasPriceComparison != 0) return gasPriceComparison;
                
            // then by gas limit ascending
            return x.GasLimit.CompareTo(y.GasLimit);
        }
    }
}
