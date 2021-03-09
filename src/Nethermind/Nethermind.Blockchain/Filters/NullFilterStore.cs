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
using Nethermind.Blockchain.Find;

namespace Nethermind.Blockchain.Filters
{
    public class NullFilterStore : IFilterStore
    {
        private NullFilterStore()
        {
        }

        public static NullFilterStore Instance { get; } = new();

        public bool FilterExists(int filterId)
        {
            return false;
        }

        public T[] GetFilters<T>() where T : FilterBase
        {
            return Array.Empty<T>();
        }

        public BlockFilter CreateBlockFilter(long startBlockNumber, bool setId = true)
        {
            throw new InvalidOperationException($"{nameof(NullFilterStore)} does not support filter creation");
        }

        public PendingTransactionFilter CreatePendingTransactionFilter(bool setId = true)
        {
            throw new InvalidOperationException($"{nameof(NullFilterStore)} does not support filter creation");
        }

        public LogFilter CreateLogFilter(BlockParameter fromBlock, BlockParameter toBlock, object address = null, IEnumerable<object> topics = null, bool setId = true)
        {
            throw new InvalidOperationException($"{nameof(NullFilterStore)} does not support filter creation");
        }

        public void SaveFilter(FilterBase filter)
        {
            throw new InvalidOperationException($"{nameof(NullFilterStore)} does not support filter creation");
        }

        public void RemoveFilter(int filterId)
        {
            throw new InvalidOperationException($"{nameof(NullFilterStore)} does not support filter creation");
        }

        public FilterType GetFilterType(int filterId)
        {
            throw new InvalidOperationException($"{nameof(NullFilterStore)} does not support filter creation");
        }

        public event EventHandler<FilterEventArgs> FilterRemoved
        {
            add { }
            remove { }
        }
    }
}
