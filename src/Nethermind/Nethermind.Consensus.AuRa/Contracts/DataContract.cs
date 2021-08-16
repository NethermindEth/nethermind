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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts
{
    internal class DataContract<T> : IDataContract<T>
    {
        public delegate bool TryGetChangesFromBlockDelegate(BlockHeader blockHeader, TxReceipt[] receipts, out IEnumerable<T> items);
        
        private readonly Func<BlockHeader, IEnumerable<T>> _getAll;
        private readonly TryGetChangesFromBlockDelegate _tryGetChangesFromBlock;

        public DataContract(
            Func<BlockHeader, IEnumerable<T>> getAll, 
            TryGetChangesFromBlockDelegate tryGetChangesFromBlock)
        {
            IncrementalChanges = false;
            _getAll = getAll ?? throw new ArgumentNullException(nameof(getAll));
            _tryGetChangesFromBlock = tryGetChangesFromBlock ?? throw new ArgumentNullException(nameof(tryGetChangesFromBlock));
        }

        public DataContract(
            Func<BlockHeader, IEnumerable<T>> getAll,
            Func<BlockHeader, TxReceipt[], IEnumerable<T>> getChangesFromBlock) 
            : this(getAll, GetTryGetChangesFromBlock(getChangesFromBlock))
        {
            IncrementalChanges = true;
        }

        private static TryGetChangesFromBlockDelegate GetTryGetChangesFromBlock(Func<BlockHeader,TxReceipt[],IEnumerable<T>> getChangesFromBlock)
        {
            return (BlockHeader blockHeader, TxReceipt[] receipts, out IEnumerable<T> items) =>
            {
                items = getChangesFromBlock(blockHeader, receipts).ToArray();
                return items.Any();
            };
        }

        public IEnumerable<T> GetAllItemsFromBlock(BlockHeader blockHeader) => _getAll(blockHeader);

        public bool TryGetItemsChangedFromBlock(BlockHeader header, TxReceipt[] receipts, out IEnumerable<T> items) => _tryGetChangesFromBlock(header, receipts, out items);

        public bool IncrementalChanges { get; }
    }
}
