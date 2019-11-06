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
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Containers
{
    public class HistoricalBatch
    {
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

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((HistoricalBatch) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }

        public static int SszLength = 2 * Time.SlotsPerHistoricalRoot * Sha256.SszLength;

        public Sha256[] BlockRoots = new Sha256[Time.SlotsPerHistoricalRoot];
        public Sha256[] StateRoots = new Sha256[Time.SlotsPerHistoricalRoot];
    }
}