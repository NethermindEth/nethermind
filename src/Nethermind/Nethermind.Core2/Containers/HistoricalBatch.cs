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
using System.Linq;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class HistoricalBatch
    {
        public static int SszLength = 2 * Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length;

        public Hash32[] BlockRoots = Enumerable.Repeat(Hash32.Zero, Time.SlotsPerHistoricalRoot).ToArray();
        public Hash32[] StateRoots = Enumerable.Repeat(Hash32.Zero, Time.SlotsPerHistoricalRoot).ToArray();

        public bool Equals(HistoricalBatch other)
        {
            for (int i = 0; i < Time.SlotsPerHistoricalRoot; i++)
            {
                if (StateRoots[i] != other.StateRoots[i])
                {
                    return false;
                }

                if (BlockRoots[i] != other.BlockRoots[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((HistoricalBatch) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }
    }
}