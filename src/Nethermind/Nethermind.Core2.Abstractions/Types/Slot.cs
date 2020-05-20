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
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core2.Types
{
    [DebuggerDisplay("{Number}")]
    public struct Slot : IEquatable<Slot>, IComparable<Slot>
    {
        private ulong _number;

        public Slot(ulong number)
        {
            _number = number;
        }

        public static Slot? None => default;
        
        public static Slot Zero => new Slot(0);
        
        public static Slot One => new Slot(1);

        public ulong Number => _number;

        public static bool operator <(Slot a, Slot b)
        {
            return a._number < b._number;
        }

        public static bool operator >(Slot a, Slot b)
        {
            return a._number > b._number;
        }

        public static bool operator <=(Slot a, Slot b)
        {
            return a._number <= b._number;
        }

        public static bool operator >=(Slot a, Slot b)
        {
            return a._number >= b._number;
        }

        public static bool operator ==(Slot a, Slot b)
        {
            return a._number == b._number;
        }

        public static bool operator !=(Slot a, Slot b)
        {
            return !(a == b);
        }

        public static Slot operator -(Slot left, Slot right)
        {
            return new Slot(left._number - right._number);
        }

        public static Slot operator %(Slot left, Slot right)
        {
            return new Slot(left._number % right._number);
        }

        public static Slot operator *(Slot left, ulong right)
        {
            return new Slot(left._number * right);
        }

        public static ulong operator /(Slot left, Slot right)
        {
            return left._number / right._number;
        }

        public static Slot operator +(Slot left, Slot right)
        {
            return new Slot(left._number + right._number);
        }

        public bool Equals(Slot other)
        {
            return _number == other._number;
        }

        public override bool Equals(object? obj)
        {
            return obj is Slot other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _number.GetHashCode();
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
            return slot._number;
        }

        public static explicit operator int(Slot slot)
        {
            return (int) slot._number;
        }
        
        public static Slot Max(Slot val1, Slot val2)
        {
            return val1 >= val2 ? val1 : val2;
        }
        
        public static Slot Min(Slot val1, Slot val2)
        {
            return val1 <= val2 ? val1 : val2;
        }

        public override string ToString()
        {
            return _number.ToString();
        }

        public int CompareTo(Slot other)
        {
            return _number.CompareTo(other._number);
        }

        public static Slot InterlockedCompareExchange(ref Slot location1, Slot value, Slot comparand)
        {
            // Interlocked doesn't support ulong yet (planned for .NET 5), but isomorphic with Int64
            ref long longRef = ref Unsafe.As<ulong, long>(ref location1._number);
            long originalNumber = Interlocked.CompareExchange(ref longRef, (long)value._number, (long)comparand._number);
            return new Slot((ulong)originalNumber);
        }
    }
}