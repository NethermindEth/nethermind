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
    public interface IFilterStore
    {
        bool FilterExists(int filterId);
        T[] GetFilters<T>() where T : FilterBase;
        BlockFilter CreateBlockFilter(long startBlockNumber, bool setId = true);
        PendingTransactionFilter CreatePendingTransactionFilter(bool setId = true);

        LogFilter CreateLogFilter(BlockParameter fromBlock, BlockParameter toBlock, object address = null,
            IEnumerable<object> topics = null, bool setId = true);

        void SaveFilter(FilterBase filter);
        void RemoveFilter(int filterId);
        FilterType GetFilterType(int filterId);

        event EventHandler<FilterEventArgs> FilterRemoved;
    }
}
