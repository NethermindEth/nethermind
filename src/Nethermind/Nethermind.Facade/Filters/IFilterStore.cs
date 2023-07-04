// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Find;

namespace Nethermind.Blockchain.Filters
{
    public interface IFilterStore
    {
        bool FilterExists(int filterId);
        IEnumerable<T> GetFilters<T>() where T : FilterBase;
        T? GetFilter<T>(int filterId) where T : FilterBase;
        BlockFilter CreateBlockFilter(long startBlockNumber, bool setId = true);
        PendingTransactionFilter CreatePendingTransactionFilter(bool setId = true);

        LogFilter CreateLogFilter(
            BlockParameter fromBlock,
            BlockParameter toBlock,
            object? address = null,
            IEnumerable<object>? topics = null,
            bool setId = true);

        void SaveFilter(FilterBase filter);
        void RemoveFilter(int filterId);
        FilterType GetFilterType(int filterId);

        event EventHandler<FilterEventArgs> FilterRemoved;
    }
}
