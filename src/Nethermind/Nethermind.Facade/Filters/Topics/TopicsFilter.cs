// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters.Topics
{
    public class TopicsFilter : IEquatable<TopicsFilter>
    {
        public static readonly TopicsFilter AnyTopic = new();

        private readonly TopicExpression[] _expressions;

        public TopicsFilter(params TopicExpression[] expressions)
        {
            _expressions = expressions;
        }

        public bool Accepts(LogEntry entry) => Accepts(entry.Topics);

        private bool Accepts(Keccak[] topics)
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

        public bool Accepts(ref LogEntryStructRef entry)
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

        public bool Matches(Bloom bloom)
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

        public bool Matches(ref BloomStructRef bloom)
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

        public bool Equals(TopicsFilter other) => _expressions.SequenceEqual(other._expressions);

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((TopicsFilter)obj);
        }

        public override int GetHashCode() => _expressions.GetHashCode();

        public override string ToString() => $"[{string.Join<TopicExpression>(',', _expressions)}]";

        /// <summary>
        /// Returns a list of list of actual topics.
        /// First level represents AND logical condition, second level represents OR logical condition.
        /// </summary>
        /// <remarks>Example: [ [A], [B, C], [], [A, C] ] represents logical statement A AND (B OR C) AND (TRUE) AND (A or C)</remarks>
        public IEnumerable<IEnumerable<Keccak>> LogicalTopicsSequence => _expressions.Select(t => t.OrTopicExpression);
    }
}
