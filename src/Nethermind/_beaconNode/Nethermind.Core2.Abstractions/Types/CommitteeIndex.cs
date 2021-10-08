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
using System.Diagnostics;

namespace Nethermind.Core2.Types
{
    [DebuggerDisplay("{Number}")]
    public struct CommitteeIndex : IEquatable<CommitteeIndex>, IComparable<CommitteeIndex>
    {
        public CommitteeIndex(ulong number)
        {
            Number = number;
        }
        
        public ulong Number { get; }

        public static CommitteeIndex? None => null;

        public static CommitteeIndex One => new CommitteeIndex(1);

        public static CommitteeIndex Zero => new CommitteeIndex(0);

        public static explicit operator CommitteeIndex(ulong value) => new CommitteeIndex(value);

        public static explicit operator ulong(CommitteeIndex committeeIndex) => committeeIndex.Number;

        public static CommitteeIndex operator -(CommitteeIndex left, CommitteeIndex right)
        {
            return new CommitteeIndex(left.Number - right.Number);
        }

        public static bool operator !=(CommitteeIndex left, CommitteeIndex right)
        {
            return !(left == right);
        }

        public static CommitteeIndex operator +(CommitteeIndex left, CommitteeIndex right)
        {
            return new CommitteeIndex(left.Number + right.Number);
        }

        public static bool operator <(CommitteeIndex left, CommitteeIndex right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(CommitteeIndex left, CommitteeIndex right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator ==(CommitteeIndex left, CommitteeIndex right)
        {
            return left.Equals(right);
        }

        public static bool operator >(CommitteeIndex left, CommitteeIndex right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(CommitteeIndex left, CommitteeIndex right)
        {
            return left.CompareTo(right) >= 0;
        }

        public int CompareTo(CommitteeIndex other)
        {
            return Number.CompareTo(other.Number);
        }

        public override bool Equals(object? obj)
        {
            return obj is CommitteeIndex slot && Equals(slot);
        }

        public bool Equals(CommitteeIndex other)
        {
            return Number == other.Number;
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