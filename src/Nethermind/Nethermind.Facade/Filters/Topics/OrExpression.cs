// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class OrExpression : TopicExpression, IEquatable<OrExpression>
    {
        private readonly TopicExpression[] _subexpressions;

        public override IEnumerable<long> GetBlockNumbersFrom(LogIndexStorage logIndexStorage)
        {

            var blocks = _subexpressions.Select(e => e.GetBlockNumbersFrom(logIndexStorage));
            IEnumerator<long>[] enumerators = blocks.Select(b => b.GetEnumerator()).ToArray();



            try
            {

                DictionarySortedSet<long, IEnumerator<long>> transactions = new();

                for (int i = 0; i < enumerators.Length; i++)
                {
                    IEnumerator<long> enumerator = enumerators[i];
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }
                }


                while (transactions.Count > 0)
                {
                    (long blockNumber, IEnumerator<long> enumerator) = transactions.Min;

                    transactions.Remove(blockNumber);
                    bool isRepeated = false;

                    if (transactions.Count > 0)
                    {
                        (long blockNumber2, IEnumerator<long> enumerator2) = transactions.Min;
                        isRepeated = blockNumber == blockNumber2;
                    }

                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }
                    if (!isRepeated)
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

        public OrExpression(params TopicExpression[] subexpressions)
        {
            _subexpressions = subexpressions;
        }

        public override bool Accepts(Hash256 topic)
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

        public override bool Accepts(ref Hash256StructRef topic)
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

        public override string ToString() => $"[{string.Join<TopicExpression>(',', _subexpressions)}]";
    }
}
