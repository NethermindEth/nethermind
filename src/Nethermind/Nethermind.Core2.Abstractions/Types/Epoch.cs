// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
