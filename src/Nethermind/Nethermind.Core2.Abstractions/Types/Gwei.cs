// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;

namespace Nethermind.Core2.Types
{
    [DebuggerDisplay("{Amount}")]
    public struct Gwei : IEquatable<Gwei>, IComparable<Gwei>
    {
        public static Gwei Zero = default;

        public static Gwei One = new Gwei(1);

        public Gwei(ulong amount)
        {
            Amount = amount;
        }

        public ulong Amount { get; }

        public static bool operator <(Gwei a, Gwei b)
        {
            return a.Amount < b.Amount;
        }

        public static bool operator >(Gwei a, Gwei b)
        {
            return a.Amount > b.Amount;
        }

        public static bool operator <=(Gwei a, Gwei b)
        {
            return a.Amount <= b.Amount;
        }

        public static bool operator >=(Gwei a, Gwei b)
        {
            return a.Amount >= b.Amount;
        }

        public static bool operator ==(Gwei a, Gwei b)
        {
            return a.Amount == b.Amount;
        }

        public static bool operator !=(Gwei a, Gwei b)
        {
            return !(a == b);
        }

        public bool Equals(Gwei other)
        {
            return Amount == other.Amount;
        }

        public override bool Equals(object? obj)
        {
            return obj is Gwei other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Amount.GetHashCode();
        }

        public static explicit operator Gwei(ulong value) => new Gwei(value);

        public static implicit operator ulong(Gwei slot) => slot.Amount;

        public static Gwei Min(Gwei val1, Gwei val2)
        {
            return new Gwei(Math.Min(val1.Amount, val2.Amount));
        }

        public static Gwei Max(Gwei val1, Gwei val2)
        {
            return new Gwei(Math.Max(val1.Amount, val2.Amount));
        }

        public static Gwei operator -(Gwei left, Gwei right)
        {
            return new Gwei(left.Amount - right.Amount);
        }

        public static Gwei operator %(Gwei left, Gwei right)
        {
            return new Gwei(left.Amount % right.Amount);
        }

        public static Gwei operator *(Gwei left, ulong right)
        {
            return new Gwei(left.Amount * right);
        }

        public static Gwei operator /(Gwei left, ulong right)
        {
            return new Gwei(left.Amount / right);
        }

        public static Gwei operator +(Gwei left, Gwei right)
        {
            return new Gwei(left.Amount + right.Amount);
        }

        public Gwei IntegerSquareRoot()
        {
            return (Gwei)Amount.SquareRoot();
        }

        public override string ToString()
        {
            return Amount.ToString();
        }

        public int CompareTo(Gwei other)
        {
            return Amount.CompareTo(other.Amount);
        }
    }
}
