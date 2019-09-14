/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class TopicsFilter
    {
        public static TopicsFilter AnyTopic { get; } = new TopicsFilter();

        private readonly TopicExpression[] _expressions;

        public TopicsFilter(params TopicExpression[] expressions)
        {
            _expressions = expressions;
        }

        public bool Accepts(LogEntry entry)
        {
            if (_expressions.Length > entry.Topics.Length)
            {
                return false;
            }
            
            for (int i = 0; i < _expressions.Length; i++)
            {
                if (!_expressions[i].Accepts(entry.Topics[i]))
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
    }
}