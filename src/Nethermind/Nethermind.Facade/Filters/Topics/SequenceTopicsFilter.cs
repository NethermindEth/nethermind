// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class SequenceTopicsFilter : TopicsFilter, IEquatable<SequenceTopicsFilter>
    {
        public static readonly SequenceTopicsFilter AnyTopic = new();

        private readonly TopicExpression[] _expressions;

        public static IEnumerable<int> Any = [-1];

        public override IEnumerable<int> GetBlockNumbersFrom(LogIndexStorage logIndexStorage)
        {
            if (_expressions is null || _expressions.Length == 0)
            {
                yield return Any.First();
                yield break;
            }

            var blocks = _expressions.Select(e => e.GetBlockNumbersFrom(logIndexStorage));
            IEnumerator<int>[] enumerators = blocks.Select(b => b.GetEnumerator()).ToArray();

            try
            {
                DictionarySortedSet<int, IEnumerator<int>> transactions = new();

                for (int i = 0; i < enumerators.Length; i++)
                {
                    IEnumerator<int> enumerator = enumerators[i];
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }
                }


                while (transactions.Count == enumerators.Length)
                {
                    (int blockNumber, IEnumerator<int> enumerator) = transactions.Min;


                    (int blockNumber2, IEnumerator<int> enumerator2) = transactions.Max;

                    bool isIntersection = blockNumber == blockNumber2;
                    transactions.Remove(blockNumber);

                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }

                    if (isIntersection)
                    {
                        yield return blockNumber;
                    }
                }

            }
            finally
            {

                for (int i = 0; i < enumerators.Length; i++)
                {
                    enumerators[i].Dispose();
                }
            }

        }

        public SequenceTopicsFilter(params TopicExpression[] expressions)
        {
            _expressions = expressions;
        }

        public override bool Accepts(LogEntry entry) => Accepts(entry.Topics);

        private bool Accepts(Hash256[] topics)
        {
            if (_expressions.Length > topics.Length)
            {
                return false;
            }

            for (int i = 0; i < _expressions.Length; i++)
            {
                if (!_expressions[i].Accepts(topics[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Accepts(ref LogEntryStructRef entry)
        {
            if (entry.Topics is not null)
            {
                return Accepts(entry.Topics);
            }

            Span<byte> buffer = stackalloc byte[32];
            var iterator = new KeccaksIterator(entry.TopicsRlp, buffer);
            for (int i = 0; i < _expressions.Length; i++)
            {
                if (iterator.TryGetNext(out var keccak))
                {
                    if (!_expressions[i].Accepts(ref keccak))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Matches(Bloom bloom)
        {
            bool result = true;

            for (int i = 0; i < _expressions.Length; i++)
            {
                result = _expressions[i].Matches(bloom);
                if (!result)
                {
                    break;
                }
            }

            return result;
        }

        public override bool Matches(ref BloomStructRef bloom)
        {
            bool result = true;

            for (int i = 0; i < _expressions.Length; i++)
            {
                result = _expressions[i].Matches(ref bloom);
                if (!result)
                {
                    break;
                }
            }

            return result;
        }

        public bool Equals(SequenceTopicsFilter other) => _expressions.SequenceEqual(other._expressions);

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((SequenceTopicsFilter)obj);
        }

        public override int GetHashCode() => _expressions.GetHashCode();

        public override string ToString() => $"[{string.Join<TopicExpression>(',', _expressions)}]";
    }
}
