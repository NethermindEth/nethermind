//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class SequenceTopicsFilter : TopicsFilter
    {
        public static SequenceTopicsFilter AnyTopic { get; } = new();

        private readonly TopicExpression[] _expressions;

        public SequenceTopicsFilter(params TopicExpression[] expressions)
        {
            _expressions = expressions;
        }

        public override bool Accepts(LogEntry entry) => Accepts(entry.Topics);

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

        public override bool Accepts(ref LogEntryStructRef entry)
        {
            if (entry.Topics != null)
            {
                return Accepts(entry.Topics);
            }
            
            var iterator = new KeccaksIterator(entry.TopicsRlp);
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
    }
}
