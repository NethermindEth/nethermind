// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class OrExpression : TopicExpression, IEquatable<OrExpression>
    {
        private readonly TopicExpression[] _subexpressions;

        public OrExpression(params TopicExpression[] subexpressions)
        {
            _subexpressions = subexpressions;
        }

        public override bool Accepts(Keccak topic)
        {
            for (int i = 0; i < _subexpressions.Length; i++)
            {
                if (_subexpressions[i].Accepts(topic))
                {
                    return true;
                }
            }

            return false;
        }

        public override bool Accepts(ref KeccakStructRef topic)
        {
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
            if (ReferenceEquals(null, obj)) return false;
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
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return _subexpressions.SequenceEqual(other._subexpressions);
        }

        public override string ToString() => $"[{string.Join<TopicExpression>(',', _subexpressions)}]";
    }
}
