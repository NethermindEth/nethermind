// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            return new Slot((ulong)value);
        }

        public static implicit operator ulong(Slot slot)
        {
            return slot._number;
        }

        public static explicit operator int(Slot slot)
        {
            return (int)slot._number;
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
