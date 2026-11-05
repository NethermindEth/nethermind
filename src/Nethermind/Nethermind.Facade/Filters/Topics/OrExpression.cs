// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters.Topics
{
    public class OrExpression : TopicExpression, IEquatable<OrExpression>
    {
        private const int MinTopicsForLookup = 16;

        private readonly TopicExpression[] _subexpressions;
        private readonly HashSet<Hash256AsKey>.AlternateLookup<ValueHash256> _topics;

        public OrExpression(params TopicExpression[] subexpressions)
        {
            _subexpressions = subexpressions;
            HashSet<Hash256AsKey>? topics = BuildTopicsLookup(subexpressions);
            if (topics is not null)
            {
                _topics = topics.GetAlternateLookup<ValueHash256>();
            }
        }

        public IEnumerable<TopicExpression> SubExpressions => _subexpressions;
        public override bool AcceptsAnyBlock => _subexpressions.Any(static e => e.AcceptsAnyBlock);

        public override bool Accepts(Hash256 topic)
        {
            if (_topics.Set is { } topics)
            {
                return topics.Contains(topic);
            }

            for (int i = 0; i < _subexpressions.Length; i++)
            {
                if (_subexpressions[i].Accepts(topic))
                {
                    return true;
                }
            }

            return false;
        }

        public override bool Accepts(ref Hash256StructRef topic)
        {
            if (_topics.Set is not null)
            {
                return _topics.Contains(new ValueHash256(topic.Bytes));
            }

            for (int i = 0; i < _subexpressions.Length; i++)
            {
                if (_subexpressions[i].Accepts(ref topic))
                {
                    return true;
                }
            }

            return false;
        }

        public override bool Matches(Bloom bloom)
        {
            for (int i = 0; i < _subexpressions.Length; i++)
            {
                if (_subexpressions[i].Matches(bloom))
                {
                    return true;
                }
            }

            return false;
        }

        public override bool Matches(ref BloomStructRef bloom)
        {
            for (int i = 0; i < _subexpressions.Length; i++)
            {
                if (_subexpressions[i].Matches(ref bloom))
                {
                    return true;
                }
            }

            return false;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            return Equals(obj as OrExpression);
        }

        public override int GetHashCode()
        {
            HashCode hashCode = new();
            for (int i = 0; i < _subexpressions.Length; i++)
            {
                hashCode.Add(_subexpressions[i].GetHashCode());
            }

            return hashCode.ToHashCode();
        }

        public bool Equals(OrExpression? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return _subexpressions.SequenceEqual(other._subexpressions);
        }

        private static HashSet<Hash256AsKey>? BuildTopicsLookup(TopicExpression[] subexpressions)
        {
            if (subexpressions.Length < MinTopicsForLookup)
            {
                return null;
            }

            HashSet<Hash256AsKey> topics = new(subexpressions.Length, Hash256AsKeyComparer.Instance);
            for (int i = 0; i < subexpressions.Length; i++)
            {
                if (subexpressions[i] is not SpecificTopic specificTopic || specificTopic.GetType() != typeof(SpecificTopic))
                {
                    return null;
                }

                topics.Add(specificTopic.Topic);
            }

            return topics;
        }

        public override string ToString() => $"[{string.Join<TopicExpression>(',', _subexpressions)}]";
    }
}
