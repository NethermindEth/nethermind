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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public abstract class PermissionTxComparerBase : IComparer<Transaction>
    {
        private readonly IContractDataStore<Address> _sendersWhitelist;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities;

        public PermissionTxComparerBase(IContractDataStore<Address> sendersWhitelist, // expected HashSet based
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities) // expected SortedList based)
        {
            _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
            _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
        }
        
        protected abstract BlockHeader BlockHeader { get; }

        protected UInt256 GetPriority(Transaction tx) =>
            _priorities.TryGetValue(BlockHeader, tx, out var destination)
                ? destination.Value
                : UInt256.Zero;

        protected bool IsWhiteListed(Transaction tx) =>
            // if _sendersWhitelist is HashSetContractDataStore<Address> it returns a set that then uses HashSet.Contains avoiding multiple enumeration
            _sendersWhitelist.GetItemsFromContractAtBlock(BlockHeader).Contains(tx.SenderAddress);

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
            return GetPriority(y).CompareTo(GetPriority(x));
        }
    }
}
