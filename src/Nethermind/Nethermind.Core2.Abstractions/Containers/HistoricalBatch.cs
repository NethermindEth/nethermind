//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Containers
{
    public class HistoricalBatch
    {
        private readonly Hash32[] _blockRoots;
        private readonly Hash32[] _stateRoots;

        public HistoricalBatch(Hash32[] blockRoots, Hash32[] stateRoots)
        {
            _blockRoots = blockRoots;
            _stateRoots = stateRoots;
        }

        public IReadOnlyList<Hash32> BlockRoots { get { return _blockRoots; } }

        public IReadOnlyList<Hash32> StateRoots { get { return _stateRoots; } }
        
        public bool Equals(HistoricalBatch other)
        {
            return BlockRoots.SequenceEqual(other.BlockRoots)
                   && StateRoots.SequenceEqual(other.StateRoots);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is HistoricalBatch other && Equals(other);
        }

        public override int GetHashCode()
        {
            HashCode hashCode = new HashCode();
            foreach (Hash32 value in BlockRoots)
            {
                hashCode.Add(value);
            }
            foreach (Hash32 value in StateRoots)
            {
                hashCode.Add(value);
            }

            return hashCode.ToHashCode();
        }

    }
}
