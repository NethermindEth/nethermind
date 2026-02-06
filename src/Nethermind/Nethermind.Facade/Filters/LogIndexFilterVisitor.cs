// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db.LogIndex;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Facade.Filters;

/// <summary>
/// Converts <see cref="LogFilter"/> tree and block range into an enumerator of block numbers from <see cref="LogIndexStorage"/>,
/// by building corresponding "tree of enumerators".
/// </summary>
public class LogIndexFilterVisitor(ILogIndexStorage storage, LogFilter filter, uint fromBlock, uint toBlock) : IEnumerable<uint>
{
    internal sealed class IntersectEnumerator(IEnumerator<uint> e1, IEnumerator<uint> e2) : IEnumerator<uint>
    {
        public bool MoveNext()
        {
            bool has1 = e1.MoveNext();
            bool has2 = e2.MoveNext();

            while (has1 && has2)
            {
                uint c1 = e1.Current;
                uint c2 = e2.Current;
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

        public uint Current { get; private set; }

        object? IEnumerator.Current => Current;

        public void Dispose()
        {
            e1.Dispose();
            e2.Dispose();
        }
    }

    internal sealed class UnionEnumerator(IEnumerator<uint> e1, IEnumerator<uint> e2) : IEnumerator<uint>
    {
        private bool _has1 = e1.MoveNext();
        private bool _has2 = e2.MoveNext();

        public bool MoveNext() =>
            (_has1, _has2) switch
            {
                (true, true) => e1.Current.CompareTo(e2.Current) switch
                {
                    0 => MoveNext(e1, out _has1) && MoveNext(e2, out _has2),
                    < 0 => MoveNext(e1, out _has1),
                    > 0 => MoveNext(e2, out _has2)
                },
                (true, false) => MoveNext(e1, out _has1),
                (false, true) => MoveNext(e2, out _has2),
                (false, false) => false
            };

        private bool MoveNext(IEnumerator<uint> enumerator, out bool has)
        {
            Current = enumerator.Current;
            has = enumerator.MoveNext();
            return true;
        }

        public void Reset()
        {
            e1.Reset();
            e2.Reset();
            _has1 = e1.MoveNext();
            _has2 = e2.MoveNext();
        }

        public uint Current { get; private set; }

        object? IEnumerator.Current => Current;

        public void Dispose()
        {
            e1.Dispose();
            e2.Dispose();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IEnumerator<uint> GetEnumerator()
    {
        IEnumerator<uint>? addressEnumerator = Visit(filter.AddressFilter);
        IEnumerator<uint>? topicEnumerator = Visit(filter.TopicsFilter);

        if (addressEnumerator is not null && topicEnumerator is not null)
            return new IntersectEnumerator(addressEnumerator, topicEnumerator);

        return addressEnumerator ?? topicEnumerator ?? throw new InvalidOperationException("Provided filter covers whole block range.");
    }

    private IEnumerator<uint>? Visit(AddressFilter addressFilter)
    {
        IEnumerator<uint>? result = null;

        foreach (AddressAsKey address in addressFilter.Addresses)
        {
            IEnumerator<uint> next = Visit(address);
            result = result is null ? next : new UnionEnumerator(result, next);
        }

        return result;
    }

    private IEnumerator<uint>? Visit(TopicsFilter topicsFilter)
    {
        IEnumerator<uint> result = null;

        var topicIndex = 0;
        foreach (TopicExpression expression in topicsFilter.Expressions)
        {
            if (Visit(topicIndex++, expression) is not { } next)
                continue;

            result = result is null ? next : new IntersectEnumerator(result, next);
        }

        return result;
    }

    private IEnumerator<uint>? Visit(int topicIndex, TopicExpression expression) => expression switch
    {
        AnyTopic => null,
        OrExpression orExpression => Visit(topicIndex, orExpression),
        SpecificTopic specificTopic => Visit(topicIndex, specificTopic.Topic),
        _ => throw new ArgumentOutOfRangeException($"Unknown topic expression type: {expression.GetType().Name}.")
    };

    private IEnumerator<uint>? Visit(int topicIndex, OrExpression orExpression)
    {
        IEnumerator<uint>? result = null;

        foreach (TopicExpression expression in orExpression.SubExpressions)
        {
            if (Visit(topicIndex, expression) is not { } next)
                continue;

            result = result is null ? next : new UnionEnumerator(result, next);
        }

        return result;
    }

    private IEnumerator<uint> Visit(Address address) =>
        storage.GetEnumerator(address, fromBlock, toBlock);

    private IEnumerator<uint> Visit(int topicIndex, Hash256 topic) =>
        storage.GetEnumerator(topicIndex, topic, fromBlock, toBlock);
}

public static class LogIndexFilterVisitorExtensions
{
    public static IEnumerable<long> EnumerateBlockNumbersFor(this ILogIndexStorage storage, LogFilter filter, uint fromBlock, uint toBlock) =>
        new LogIndexFilterVisitor(storage, filter, fromBlock, toBlock).Select(static i => (long)i);
}
