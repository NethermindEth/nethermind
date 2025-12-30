// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db.LogIndex;

namespace Nethermind.Facade.Find;

public static class LogIndexStorageRpcExtensions
{
    // TODO!: inline
    // TODO!: verify enumerator disposal when not all blocks are enumerated
    public static IEnumerable<int> EnumerateBlockNumbersFor(this ILogIndexStorage storage, LogFilter filter, long fromBlock, long toBlock)
    {
        return new LogIndexFilterVisitor(storage, filter, (int) fromBlock, (int) toBlock);
    }
}

// TODO: separate file
public class LogIndexFilterVisitor(ILogIndexStorage storage, LogFilter filter, int fromBlock, int toBlock): IEnumerable<int>
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

    public sealed class UnionEnumerator(IEnumerator<int> e1, IEnumerator<int> e2) : IEnumerator<int>
    {
        // TODO: reduce number of fields?
        private bool _has1;
        private bool _has2;
        private bool _initialized;

        public bool MoveNext()
        {
            if (!_initialized)
            {
                _has1 = e1.MoveNext();
                _has2 = e2.MoveNext();
                _initialized = true;
            }

            switch (_has1, _has2)
            {
                case (false, false):
                    return false;
                case (false, true):
                    Current = e2.Current;
                    _has2 = e2.MoveNext();
                    return true;
                case (true, false):
                    Current = e1.Current;
                    _has1 = e1.MoveNext();
                    return true;
            }

            var (c1, c2) = (e1.Current, e2.Current);
            switch (c1.CompareTo(c2))
            {
                case <0:
                    Current = c1;
                    _has1 = e1.MoveNext();
                    return true;
                case > 0:
                    Current = c2;
                    _has2 = e2.MoveNext();
                    return true;
                case 0:
                    Current = c1;
                    (_has1, _has2) = (e1.MoveNext(), e2.MoveNext());
                    return true;
            }
        }

        public void Reset()
        {
            e1.Reset();
            e2.Reset();

            _has1 = false;
            _has2 = false;
            _initialized = false;
        }

        public int Current { get; private set; }

        object? IEnumerator.Current => Current;

        public void Dispose()
        {
            e1.Dispose();
            e2.Dispose();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IEnumerator<int> GetEnumerator()
    {
        IEnumerator<int>? addressEnumerator = Visit(filter.AddressFilter);
        IEnumerator<int>? topicEnumerator = Visit(filter.TopicsFilter);

        if (addressEnumerator is null) return topicEnumerator!;
        if (topicEnumerator is null) return addressEnumerator!;

        return new IntersectEnumerator(addressEnumerator, topicEnumerator);
    }

    private IEnumerator<int>? Visit(AddressFilter addressFilter)
    {
        IEnumerator<int>? result = null;

        foreach (AddressAsKey address in addressFilter.Addresses)
        {
            IEnumerator<int> next = Visit(address);
            result = result is null ? next : new UnionEnumerator(result, next);
        }

        return result;
    }

    private IEnumerator<int>? Visit(TopicsFilter topicsFilter)
    {
        IEnumerator<int> result = null;

        for (var topicIndex = 0; topicIndex < topicsFilter.Expressions.Count; topicIndex++)
        {
            TopicExpression expression = topicsFilter.Expressions[topicIndex];

            if (Visit(topicIndex, expression) is not { } next)
                continue;

            result = result is null ? next : new IntersectEnumerator(result, next);
        }

        return result;
    }

    private IEnumerator<int>? Visit(int topicIndex, TopicExpression expression) => expression switch
    {
        AnyTopic anyTopic => null,
        OrExpression orExpression => Visit(topicIndex, orExpression),
        SpecificTopic specificTopic => Visit(topicIndex, specificTopic),
        _ => throw new ArgumentOutOfRangeException($"Unknown topic expression type: {expression.GetType().Name}.")
    };

    private IEnumerator<int>? Visit(int topicIndex, OrExpression orExpression)
    {
        IEnumerator<int>? result = null;

        foreach (Hash256 topic in orExpression.Topics)
        {
            IEnumerator<int> next = Visit(topicIndex, topic);
            result = result is null ? next : new UnionEnumerator(result, next);
        }

        return result;
    }

    private IEnumerator<int>? Visit(int topicIndex, SpecificTopic specificTopic)
    {
        IEnumerator<int>? result = null;

        foreach (Hash256 topic in specificTopic.Topics)
        {
            IEnumerator<int> next = Visit(topicIndex, topic);
            result = result is null ? next : new IntersectEnumerator(result, next);
        }

        return result;
    }

    private IEnumerator<int> Visit(Address address) =>
        storage.GetBlockNumbersEnumerator(address, fromBlock, toBlock);

    private IEnumerator<int> Visit(int topicIndex, Hash256 topic) =>
        storage.GetBlockNumbersEnumerator(topicIndex, topic, fromBlock, toBlock);
}
