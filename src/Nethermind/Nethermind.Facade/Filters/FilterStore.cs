// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters
{
    public class FilterStore : IFilterStore
    {
        private int _currentFilterId = -1;
        private object _locker = new object();

        private readonly ConcurrentDictionary<int, FilterBase> _filters = new();

        public bool FilterExists(int filterId) => _filters.ContainsKey(filterId);

        public FilterType GetFilterType(int filterId)
        {
            /* so far ok to use block filter if none */
            if (!_filters.TryGetValue(filterId, out var filter))
            {
                return FilterType.BlockFilter;
            }

            switch (filter)
            {
                case LogFilter _: return FilterType.LogFilter;
                case BlockFilter _: return FilterType.BlockFilter;
                case PendingTransactionFilter _: return FilterType.PendingTransactionFilter;
                default: return FilterType.BlockFilter;
            }
        }

        // Stop gap method to reduce allocations from non-struct enumerator
        // https://github.com/dotnet/runtime/pull/38296
        private IEnumerator<KeyValuePair<int, FilterBase>>? _enumerator;

        public IEnumerable<T> GetFilters<T>() where T : FilterBase
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

        public BlockFilter CreateBlockFilter(long startBlockNumber, bool setId = true) =>
            new(GetFilterId(setId), startBlockNumber);

        public PendingTransactionFilter CreatePendingTransactionFilter(bool setId = true) =>
            new(GetFilterId(setId));

        public LogFilter CreateLogFilter(BlockParameter fromBlock, BlockParameter toBlock,
            object? address = null, IEnumerable<object?>? topics = null, bool setId = true) =>
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
            if (_filters.ContainsKey(filter.Id))
            {
                throw new InvalidOperationException($"Filter with ID {filter.Id} already exists");
            }

            lock (_locker)
            {
                _currentFilterId = Math.Max(filter.Id, _currentFilterId);
            }

            _filters[filter.Id] = filter;
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

        private TopicsFilter GetTopicsFilter(IEnumerable<object?>? topics = null)
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

        private TopicExpression GetTopicExpression(FilterTopic? filterTopic)
        {
            if (filterTopic is null)
            {
                return AnyTopic.Instance;
            }
            else if (filterTopic.Topic is not null)
            {
                return new SpecificTopic(filterTopic.Topic);
            }
            else if (filterTopic.Topics?.Length > 0)
            {
                return new OrExpression(filterTopic.Topics.Select(t => new SpecificTopic(t)).ToArray<TopicExpression>());
            }
            else
            {
                return AnyTopic.Instance;
            }
        }

        private static AddressFilter GetAddress(object? address)
        {
            if (address is null)
            {
                return AddressFilter.AnyAddress;
            }

            if (address is string s)
            {
                return new AddressFilter(new Address(s));
            }

            if (address is IEnumerable<string> e)
            {
                return new AddressFilter(e.Select(a => new Address(a)).ToHashSet());
            }

            throw new InvalidDataException("Invalid address filter format");
        }

        private static FilterTopic?[]? GetFilterTopics(IEnumerable<object>? topics)
        {
            return topics?.Select(GetTopic).ToArray();
        }

        private static FilterTopic GetTopic(object obj)
        {
            switch (obj)
            {
                case null:
                    return null;
                case string topic:
                    return new FilterTopic
                    {
                        Topic = new Keccak(topic)
                    };
                case Keccak keccak:
                    return new FilterTopic
                    {
                        Topic = keccak
                    };
            }

            var topics = obj as IEnumerable<string>;
            if (topics is null)
            {
                return null;
            }
            else
            {
                return new FilterTopic
                {
                    Topics = topics.Select(t => new Keccak(t)).ToArray()
                };
            }
        }

        private class FilterTopic
        {
            public Keccak? Topic { get; set; }
            public Keccak[]? Topics { get; set; }

        }
    }
}
