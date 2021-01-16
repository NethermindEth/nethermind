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

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IDataContract<T>
    {
        /// <summary>
        /// Gets all items in contract from block.
        /// </summary>
        /// <param name="blockHeader"></param>
        /// <returns></returns>
        IEnumerable<T> GetAllItemsFromBlock(BlockHeader blockHeader);

        /// <summary>
        /// Gets item changed in contract in that block.
        /// </summary>
        /// <param name="header"></param>
        /// <param name="receipts"></param>
        /// <param name="items"></param>
        /// <returns></returns>
        bool TryGetItemsChangedFromBlock(BlockHeader header, TxReceipt[] receipts, out IEnumerable<T> items);
        
        /// <summary>
        /// If changes in blocks are incremental.
        /// If 'true' values we extract from receipts are changes to be merged with previous state.
        /// If 'false' values we extract from receipts overwrite previous state.
        /// </summary>
        bool IncrementalChanges { get; } 
    }
}
