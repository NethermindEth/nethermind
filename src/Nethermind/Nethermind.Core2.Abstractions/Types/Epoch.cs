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
    public struct Epoch : IEquatable<Epoch>, IComparable<Epoch>
    {
        public Epoch(ulong number)
        {
            Number = number;
        }
        
        public ulong Number { get; }

        public static bool operator <(Epoch a, Epoch b)
        {
            return a.Number < b.Number;
        }

        public static bool operator >(Epoch a, Epoch b)
        {
            return a.Number > b.Number;
        }
        
        public static bool operator <=(Epoch a, Epoch b)
        {
            return a.Number <= b.Number;
        }

        public static bool operator >=(Epoch a, Epoch b)
        {
            return a.Number >= b.Number;
        }
        
        public static bool operator ==(Epoch a, Epoch b)
        {
            return a.Number == b.Number;
        }

        public static bool operator !=(Epoch a, Epoch b)
        {
            return !(a == b);
        }

        public bool Equals(Epoch other)
        {
            return Number == other.Number;
        }

        public override bool Equals(object? obj)
        {
            return obj is Epoch other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Number.GetHashCode();
        }
        
        public override string ToString()
        {
            return Number.ToString();
        }

        public static Epoch? None => null;

        public static Epoch Zero => new Epoch(0);
        
        public static Epoch One => new Epoch(1);

        public static explicit operator Epoch(ulong value) => new Epoch(value);

        public static implicit operator ulong(Epoch slot) => slot.Number;
        
        public static implicit operator int(Epoch slot) => (int)slot.Number;

        public static Epoch Max(Epoch val1, Epoch val2)
        {
            return new Epoch(Math.Max(val1.Number, val2.Number));
        }

        public static Epoch Min(Epoch val1, Epoch val2)
        {
            return new Epoch(Math.Min(val1.Number, val2.Number));
        }

        public static Epoch operator -(Epoch left, Epoch right)
        {
            return new Epoch(left.Number - right.Number);
        }

        public static Epoch operator %(Epoch left, Epoch right)
        {
            return new Epoch(left.Number % right.Number);
        }

        public static Epoch operator +(Epoch left, Epoch right)
        {
            return new Epoch(left.Number + right.Number);
        }

        public int CompareTo(Epoch other)
        {
            return Number.CompareTo(other.Number);
        }
    }
}