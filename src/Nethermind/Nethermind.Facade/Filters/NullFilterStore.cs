// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public IEnumerable<T> GetFilters<T>() where T : FilterBase
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

        public LogFilter CreateLogFilter(BlockParameter fromBlock, BlockParameter toBlock, object? address = null, IEnumerable<object>? topics = null, bool setId = true)
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

        public void RefreshFilter(int filterId)
        {
            throw new InvalidOperationException($"{nameof(NullFilterStore)} does not support filter refreshing");
        }

        public FilterType GetFilterType(int filterId)
        {
            throw new InvalidOperationException($"{nameof(NullFilterStore)} does not support filter creation");
        }

        public T? GetFilter<T>(int filterId) where T : FilterBase
        {
            return null;
        }

        public event EventHandler<FilterEventArgs> FilterRemoved
        {
            add { }
            remove { }
        }

        public void Dispose() { }
    }
}
