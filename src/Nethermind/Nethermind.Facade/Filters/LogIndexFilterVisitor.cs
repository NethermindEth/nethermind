// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db.LogIndex;

namespace Nethermind.Facade.Filters;

public class LogIndexFilterVisitor(ILogIndexStorage storage, LogFilter filter, int fromBlock, int toBlock) : IEnumerable<int>
{
    public sealed class IntersectEnumerator(IEnumerator<int> e1, IEnumerator<int> e2) : IEnumerator<int>
    {
        public bool MoveNext()
        {
            bool has1 = e1.MoveNext();
            bool has2 = e2.MoveNext();

            while (has1 && has2)
            {
                var (c1, c2) = (e1.Current, e2.Current);
                if (c1 == c2)
                {
                    Current = c1;
                    return true;
                }

                if (c1 < c2) has1 = e1.MoveNext();
                else has2 = e2.MoveNext();
            }

            return false;
        }

        public void Reset()
        {
            e1.Reset();
            e2.Reset();
        }

        public int Current { get; private set; }

        object? IEnumerator.Current => Current;

        public void Dispose()
        {
            e1.Dispose();
            e2.Dispose();
        }
    }

    public sealed class UnionEnumerator(IEnumerable<IEnumerator<int>> enumerators) : IEnumerator<int>
    {
        private readonly IEnumerator<int>[] _enumerators = enumerators as IEnumerator<int>[] ?? [.. enumerators];
        private DictionarySortedSet<int, List<IEnumerator<int>>>? _sortedSet;

        public UnionEnumerator(params IEnumerator<int>[] enumerators) : this(enumerators.AsEnumerable()) { }

        public bool MoveNext()
        {
            _sortedSet ??= Initialize();

            if (_sortedSet.Count == 0)
                return false;

            (int value, List<IEnumerator<int>> enumerators) = _sortedSet.Min;
            _sortedSet.Remove(value);
            Current = value;

            foreach (IEnumerator<int> enumerator in enumerators.Where(static e => e.MoveNext()))
                AddToSortedSet(enumerator.Current, enumerator);

            return true;
        }

        private DictionarySortedSet<int, List<IEnumerator<int>>> Initialize()
        {
            DictionarySortedSet<int, List<IEnumerator<int>>> sortedSet = [];

            foreach (IEnumerator<int> enumerator in _enumerators)
            {
                if (enumerator.MoveNext())
                    AddToSortedSet(sortedSet, enumerator.Current, enumerator);
            }

            return sortedSet;
        }

        private void AddToSortedSet(int value, IEnumerator<int> enumerator) =>
            AddToSortedSet(_sortedSet!, value, enumerator);

        private static void AddToSortedSet(DictionarySortedSet<int, List<IEnumerator<int>>> sortedSet, int value, IEnumerator<int> enumerator)
        {
            if (sortedSet.TryGetValue(value, out List<IEnumerator<int>> list))
                list.Add(enumerator);
            else
                sortedSet.Add(value, [enumerator]);
        }

        public void Reset()
        {
            _sortedSet = null;

            foreach (IEnumerator<int> enumerator in _enumerators)
                enumerator.Reset();
        }

        public int Current { get; private set; }
        object? IEnumerator.Current => Current;

        public void Dispose()
        {
            foreach (IEnumerator<int> enumerator in _enumerators)
                enumerator.Dispose();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IEnumerator<int> GetEnumerator()
    {
        IEnumerator<int>? addressEnumerator = Visit(filter.AddressFilter);
        IEnumerator<int>? topicEnumerator = Visit(filter.TopicsFilter);

        if (addressEnumerator is not null && topicEnumerator is not null)
            return new IntersectEnumerator(addressEnumerator, topicEnumerator);

        return addressEnumerator ?? topicEnumerator ?? throw new InvalidOperationException("Provided filter covers whole block range.");
    }

    private IEnumerator<int>? Visit(AddressFilter addressFilter) => addressFilter.Addresses.Count switch
    {
        0 => null,
        1 => Visit(addressFilter.Addresses.First()),
        _ => new UnionEnumerator(addressFilter.Addresses.Select(a => Visit(a)))
    };

    private IEnumerator<int>? Visit(TopicsFilter topicsFilter)
    {
        IEnumerator<int> result = null;

        var topicIndex = 0;
        foreach (TopicExpression expression in topicsFilter.Expressions)
        {
            if (Visit(topicIndex++, expression) is not { } next)
                continue;

            result = result is null ? next : new IntersectEnumerator(result, next);
        }

        return result;
    }

    private IEnumerator<int>? Visit(int topicIndex, TopicExpression expression) => expression switch
    {
        AnyTopic => null,
        OrExpression orExpression => Visit(topicIndex, orExpression),
        SpecificTopic specificTopic => Visit(topicIndex, specificTopic.Topic),
        _ => throw new ArgumentOutOfRangeException($"Unknown topic expression type: {expression.GetType().Name}.")
    };

    private IEnumerator<int>? Visit(int topicIndex, OrExpression orExpression) => orExpression.SubExpressions.Count switch
    {
        0 => null,
        1 => Visit(topicIndex, orExpression.SubExpressions.First()),
        _ => new UnionEnumerator(orExpression.SubExpressions.Select(t => Visit(topicIndex, t)))
    };

    private IEnumerator<int> Visit(Address address) =>
        storage.GetEnumerator(address, fromBlock, toBlock);

    private IEnumerator<int> Visit(int topicIndex, Hash256 topic) =>
        storage.GetEnumerator(topicIndex, topic, fromBlock, toBlock);
}

public static class LogIndexFilterVisitorExtensions
{
    public static IEnumerable<int> EnumerateBlockNumbersFor(this ILogIndexStorage storage, LogFilter filter, long fromBlock, long toBlock)
    {
        return new LogIndexFilterVisitor(storage, filter, (int)fromBlock, (int)toBlock);
    }
}
