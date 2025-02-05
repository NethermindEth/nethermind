// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class SequenceTopicsFilter : TopicsFilter, IEquatable<SequenceTopicsFilter>
    {
        public static readonly SequenceTopicsFilter AnyTopic = new();

        private readonly TopicExpression[] _expressions;

        public ReadOnlyCollection<TopicExpression> Expressions => _expressions.AsReadOnly();

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

        public override bool AcceptsAnyBlock => _expressions.Length == 0 || _expressions.All(e => e.AcceptsAnyBlock);

        public override IEnumerable<Hash256> Topics => _expressions.SelectMany(e => e.Topics);

        public override HashSet<int> FilterBlockNumbers(IReadOnlyDictionary<Hash256, List<int>> byTopic)
        {
            HashSet<int> result = null;
            foreach (TopicExpression expression in _expressions)
            {
                if (result == null)
                    result = expression.FilterBlockNumbers(byTopic);
                else
                    result.IntersectWith(expression.FilterBlockNumbers(byTopic));
            }

            return result ?? [];
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
