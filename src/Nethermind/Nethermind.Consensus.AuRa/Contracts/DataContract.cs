﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts
{
    internal class DataContract<T> : IDataContract<T>
    {
        private readonly Func<BlockHeader, IEnumerable<T>> _getAll;
        private readonly Func<BlockHeader, TxReceipt[], IEnumerable<T>> _getChangesFromBlock;

        public DataContract(
            Func<BlockHeader, IEnumerable<T>> getAll, 
            Func<BlockHeader, TxReceipt[], IEnumerable<T>> getChangesFromBlock,
            bool incrementalChanges)
        {
            IncrementalChanges = incrementalChanges;
            _getAll = getAll ?? throw new ArgumentNullException(nameof(getAll));
            _getChangesFromBlock = getChangesFromBlock ?? throw new ArgumentNullException(nameof(getChangesFromBlock));;
        }
        
        public IEnumerable<T> GetAllItemsFromBlock(BlockHeader blockHeader) => _getAll(blockHeader);

        public IEnumerable<T> GetItemsChangedFromBlock(BlockHeader header, TxReceipt[] receipts) => _getChangesFromBlock(header, receipts);

        public bool IncrementalChanges { get; }
    }
}
