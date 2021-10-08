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

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class UnitsRange : IEquatable<UnitsRange>
    {
        public uint From { get; private set; }
        public uint To { get; private set; }
        public uint Units { get; private set; }

        public UnitsRange(uint from, uint to)
        {
            if (from > to)
            {
                throw new ArgumentException($"Invalid range: [{from}, {to}]", nameof(from));
            }

            From = from;
            To = to;
            Units = To - From + 1;
        }

        public bool IntersectsWith(UnitsRange unitsRange)
            => From <= unitsRange.To && To >= unitsRange.From;
        
        public bool IsSubsetOf(UnitsRange unitsRange)
            => From >= unitsRange.From && To <= unitsRange.To;

        public bool Equals(UnitsRange? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return From == other.From && To == other.To;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((UnitsRange) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int) From * 397) ^ (int) To;
            }
        }
    }
}
