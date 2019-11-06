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

using System.Diagnostics;

namespace Nethermind.Core2.Types
{
    [DebuggerDisplay("{Number}")]
    public struct CommitteeIndex
    {
        public const int SszLength = sizeof(ulong);

        public CommitteeIndex(ulong number)
        {
            Number = number;
        }
        
        public ulong Number { get; }
        
        public bool Equals(ValidatorIndex other)
        {
            return Number == other.Number;
        }
        
        public static bool operator ==(CommitteeIndex a, CommitteeIndex b)
        {
            return a.Number == b.Number;
        }

        public static bool operator !=(CommitteeIndex a, CommitteeIndex b)
        {
            return !(a == b);
        }

        public override bool Equals(object obj)
        {
            return obj is CommitteeIndex other && Equals(other);
        }
        
        public override int GetHashCode()
        {
            return Number.GetHashCode();
        }
        
        public override string ToString()
        {
            return Number.ToString();
        }
    }
}