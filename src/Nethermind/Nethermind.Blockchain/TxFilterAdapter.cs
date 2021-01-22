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
// 

using System;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Blockchain
{
    public class TxFilterAdapter : FilteredTxPool.ITxPoolFilter
    {

        private readonly ITxFilter _txFilter;
        private readonly IBlockTree _blockTree;

        public TxFilterAdapter(IBlockTree blockTree, ITxFilter txFilter)
        {
            _txFilter = txFilter ?? throw new ArgumentNullException(nameof(txFilter));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }
        
        public (bool Accepted, string Reason) Accept(Transaction tx)
        {
            var parentHeader = _blockTree.Head?.Header;
            return parentHeader == null 
                ? (true, string.Empty) 
                : _txFilter.IsAllowed(tx, parentHeader);
        }
    }
}
