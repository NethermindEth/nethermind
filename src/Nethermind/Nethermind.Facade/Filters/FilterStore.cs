// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NonBlocking;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using ITimer = Nethermind.Core.Timers.ITimer;

namespace Nethermind.Blockchain.Filters
{
    public sealed class FilterStore : IDisposable
    {
        private readonly TimeSpan _timeout;
        private int _currentFilterId = -1;
        private readonly Lock _locker = new();
        private readonly ConcurrentDictionary<int, FilterBase> _filters = new();
        private readonly ITimer _timer;

        public bool FilterExists(int filterId) => _filters.ContainsKey(filterId);

        public void RefreshFilter(int filterId)
        {
            if (_filters.TryGetValue(filterId, out FilterBase filter))
            {
                filter.LastUsed = DateTimeOffset.UtcNow;
            }
        }

        public FilterType GetFilterType(int filterId)
        {
            /* so far ok to use block filter if none */
            if (!_filters.TryGetValue(filterId, out var filter))
            {
                return FilterType.BlockFilter;
            }

            return filter switch
            {
                LogFilter _ => FilterType.LogFilter,
                BlockFilter _ => FilterType.BlockFilter,
                PendingTransactionFilter _ => FilterType.PendingTransactionFilter,
                _ => FilterType.BlockFilter,
            };
        }

        // Stop gap method to reduce allocations from non-struct enumerator
        // https://github.com/dotnet/runtime/pull/38296
        private IEnumerator<KeyValuePair<int, FilterBase>>? _enumerator;

        public FilterStore(ITimerFactory timerFactory, int timeout = 15 * 60 * 1000, int cleanupInterval = 5 * 60 * 1000)
        {
            _timeout = TimeSpan.FromMilliseconds(timeout);
            _timer = timerFactory.CreateTimer(TimeSpan.FromMilliseconds(cleanupInterval));
            _timer.AutoReset = false;
            _timer.Enabled = true;
            _timer.Elapsed += OnElapsed;
        }

        private void OnElapsed(object? sender, EventArgs e)
        {
            CleanupStaleFilters();
            _timer.Enabled = true;
        }

        private void CleanupStaleFilters()
        {
            foreach (KeyValuePair<int, FilterBase> filter in _filters)
            {
                DateTimeOffset filterTimeout = filter.Value.LastUsed + _timeout;
                if (filterTimeout < DateTimeOffset.UtcNow)
                {
                    RemoveFilter(filter.Key);
                }
            }
        }

        public IEnumerable<T> GetFilters<T>() where T : FilterBase
        {
            // Return Array.Empty<T>() to avoid allocating enumerator
            // and which has a non-allocating fast-path for
            // foreach via IEnumerable<T>
            if (_filters.IsEmpty) return Array.Empty<T>();

            return GetFiltersEnumerate<T>();
        }

        private IEnumerable<T> GetFiltersEnumerate<T>() where T : FilterBase
        {
            // Reuse the enumerator
            var enumerator = Interlocked.Exchange(ref _enumerator, null) ?? _filters.GetEnumerator();

            while (enumerator.MoveNext())
            {
                FilterBase value = enumerator.Current.Value;
                if (value is T t)
                {
                    yield return t;
                }
            }

            // Stop gap method to reduce allocations from non-struct enumerator
            // https://github.com/dotnet/runtime/pull/38296
            enumerator.Reset();
            _enumerator = enumerator;
        }

        public T? GetFilter<T>(int filterId) where T : FilterBase => _filters.TryGetValue(filterId, out var filter)
                ? filter as T
                : null;

        public BlockFilter CreateBlockFilter(bool setId = true) =>
            new(GetFilterId(setId));

        public PendingTransactionFilter CreatePendingTransactionFilter(bool setId = true) =>
            new(GetFilterId(setId));

        public LogFilter CreateLogFilter(BlockParameter fromBlock, BlockParameter toBlock,
            AddressAsKey[]? address = null, IEnumerable<Hash256[]?>? topics = null, bool setId = true) =>
            new(GetFilterId(setId),
                fromBlock,
                toBlock,
                GetAddress(address),
                GetTopicsFilter(topics));

        public void RemoveFilter(int filterId)
        {
            _filters.TryRemove(filterId, out _);
            FilterRemoved?.Invoke(this, new FilterEventArgs(filterId));
        }

        public event EventHandler<FilterEventArgs>? FilterRemoved;

        public void SaveFilter(FilterBase filter)
        {
            if (!_filters.TryAdd(filter.Id, filter))
            {
                throw new InvalidOperationException($"Filter with ID {filter.Id} already exists");
            }

            lock (_locker)
            {
                _currentFilterId = Math.Max(filter.Id, _currentFilterId);
            }
        }

        private int GetFilterId(bool generateId)
        {
            if (generateId)
            {
                lock (_locker)
                {
                    return ++_currentFilterId;
                }
            }

            return 0;
        }

        private static TopicsFilter GetTopicsFilter(IEnumerable<Hash256[]?>? topics = null)
        {
            if (topics is null)
            {
                return SequenceTopicsFilter.AnyTopic;
            }

            FilterTopic?[]? filterTopics = GetFilterTopics(topics);
            List<TopicExpression> expressions = new();

            for (int i = 0; i < filterTopics?.Length; i++)
            {
                expressions.Add(GetTopicExpression(filterTopics[i]));
            }

            return new SequenceTopicsFilter(expressions.ToArray());
        }

        private static TopicExpression GetTopicExpression(FilterTopic? filterTopic)
        {
            if (filterTopic is not null)
            {
                if (filterTopic.Topic is not null)
                {
                    return new SpecificTopic(filterTopic.Topic);
                }

                if (filterTopic.Topics?.Length > 0)
                {
                    return new OrExpression(filterTopic.Topics.Select(
                        static t => new SpecificTopic(t)).ToArray<TopicExpression>());
                }
            }

            return AnyTopic.Instance;
        }

        private static AddressFilter GetAddress(AddressAsKey[]? address)
        {
            if (address is null)
            {
                return AddressFilter.AnyAddress;
            }

            return new AddressFilter(address);
        }

        private static FilterTopic?[]? GetFilterTopics(IEnumerable<Hash256[]?>? topics) => topics?.Select(GetTopic).ToArray();

        private static FilterTopic? GetTopic(Hash256[]? topics)
        {
            return new FilterTopic
            {
                Topics = topics
            };
        }

        private class FilterTopic
        {
            public Hash256? Topic { get; init; }
            public Hash256[]? Topics { get; init; }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
