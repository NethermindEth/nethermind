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
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PermissionContractHeadTxComparer : PermissionTxComparerBase
    {
        private readonly IBlockTree _blockTree;
        private readonly IContractDataStore<Address> _sendersWhitelist;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities;

        public PermissionContractHeadTxComparer(IBlockTree blockTree,
            IContractDataStore<Address> sendersWhitelist, // expected HashSet based
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities) // expected SortedList based)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
            _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
        }

        protected override UInt256 GetPriority(Transaction tx) =>
            _priorities.TryGetValue(_blockTree.Head.Header, tx, out var destination)
                ? destination.Value
                : UInt256.Zero;

        protected override bool IsWhiteListed(Transaction tx) =>
            // if _sendersWhitelist is HashSetContractDataStore<Address> it returns a set that then uses HashSet.Contains avoiding multiple enumeration
            _sendersWhitelist.GetItemsFromContractAtBlock(_blockTree.Head.Header).Contains(tx.SenderAddress);
    }
}
