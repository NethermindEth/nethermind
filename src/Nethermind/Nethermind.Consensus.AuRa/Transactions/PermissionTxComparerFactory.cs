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
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PermissionTxComparerFactory : ITransactionComparerFactory
    {
        private readonly IContractDataStore<Address> _sendersWhitelist;
        private readonly IDictionaryContractDataStore<TxPriorityContract.Destination> _priorities;
        
        public PermissionTxComparerFactory(IContractDataStore<Address> sendersWhitelist, // expected HashSet based
            IDictionaryContractDataStore<TxPriorityContract.Destination> priorities) // expected SortedList based
        {
            _sendersWhitelist = sendersWhitelist ?? throw new ArgumentNullException(nameof(sendersWhitelist));
            _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
        }
        
        public IComparer<Transaction> CreateComparer(BlockHeader blockHeader)
        {
            UInt256 GetPriority(Transaction tx) =>
                _priorities.TryGetValue(blockHeader, tx, out var destination)
                    ? destination.Value
                    : UInt256.Zero;
            
            ISet<Address> sendersWhitelist = _sendersWhitelist.GetItemsFromContractAtBlock(blockHeader).AsSet();
            return new PermissionTxComparer(t => sendersWhitelist.Contains(t.SenderAddress), GetPriority);
        }
    }
}
