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
    public struct Slot : IEquatable<Slot>, IComparable<Slot>
    {
        public Slot(ulong number)
        {
            Number = number;
        }

        public static Slot None => new Slot(ulong.MaxValue - 1);
        
        public static Slot Zero => new Slot(0);
        
        public static Slot One => new Slot(1);

        public ulong Number { get; }

        public static bool operator <(Slot a, Slot b)
        {
            return a.Number < b.Number;
        }

        public static bool operator >(Slot a, Slot b)
        {
            return a.Number > b.Number;
        }

        public static bool operator <=(Slot a, Slot b)
        {
            return a.Number <= b.Number;
        }

        public static bool operator >=(Slot a, Slot b)
        {
            return a.Number >= b.Number;
        }

        public static bool operator ==(Slot a, Slot b)
        {
            return a.Number == b.Number;
        }

        public static bool operator !=(Slot a, Slot b)
        {
            return !(a == b);
        }

        public static Slot operator -(Slot left, Slot right)
        {
            return new Slot(left.Number - right.Number);
        }

        public static Slot operator %(Slot left, Slot right)
        {
            return new Slot(left.Number % right.Number);
        }

        public static Slot operator *(Slot left, ulong right)
        {
            return new Slot(left.Number * right);
        }

        public static ulong operator /(Slot left, Slot right)
        {
            return left.Number / right.Number;
        }

        public static Slot operator +(Slot left, Slot right)
        {
            return new Slot(left.Number + right.Number);
        }

        public bool Equals(Slot other)
        {
            return Number == other.Number;
        }

        public override bool Equals(object? obj)
        {
            return obj is Slot other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Number.GetHashCode();
        }

        public static explicit operator Slot(ulong value)
        {
            return new Slot(value);
        }

        public static explicit operator Slot(int value)
        {
            if (value < 0)
            {
                throw new ArgumentException("Slot number must be > 0", nameof(value));
            }

            return new Slot((ulong) value);
        }

        public static implicit operator ulong(Slot slot)
        {
            return slot.Number;
        }

        public static explicit operator int(Slot slot)
        {
            return (int) slot.Number;
        }
        
        public static Slot Min(Slot val1, Slot val2)
        {
            return val1 <= val2 ? val1 : val2;
        }

        public override string ToString()
        {
            return Number.ToString();
        }

        public int CompareTo(Slot other)
        {
            return Number.CompareTo(other.Number);
        }
    }
}