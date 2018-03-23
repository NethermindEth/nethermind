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

using System.Linq;
using System.Numerics;

namespace Nethermind.Core.Specs
{
    public class CustomSpecProvider : ISpecProvider
    {
        private readonly (BigInteger BlockNumber, IReleaseSpec Release)[] _transitions;

        public CustomSpecProvider(params (BigInteger BlockNumber, IReleaseSpec Release)[] transitions)
        {
            _transitions = transitions.OrderBy(r => r.BlockNumber).ToArray();
        }

        public IReleaseSpec GetSpec(BigInteger blockNumber)
        {
            foreach ((BigInteger BlockNumber, IReleaseSpec Release) transition in _transitions)
            {
                if (transition.BlockNumber > blockNumber)
                {
                    return transition.Release;
                }
            }

            return _transitions.Last().Release;
        }
    }
}