﻿using System;

namespace Cortex.Containers
{
    public struct Epoch : IEquatable<Epoch>, IComparable<Epoch>
    {
        private readonly ulong _value;

        public Epoch(ulong value)
        {
            _value = value;
        }

        public static Epoch None => new Epoch(ulong.MaxValue);

        public static Epoch Zero => new Epoch(0);

        public static explicit operator Epoch(ulong value) => new Epoch(value);

        public static explicit operator ulong(Epoch slot) => slot._value;

        public static Epoch Max(Epoch val1, Epoch val2)
        {
            return new Epoch(Math.Max(val1._value, val2._value));
        }

        public static Epoch Min(Epoch val1, Epoch val2)
        {
            return new Epoch(Math.Min(val1._value, val2._value));
        }

        public static Epoch operator -(Epoch left, Epoch right)
        {
            return new Epoch(left._value - right._value);
        }

        public static bool operator !=(Epoch left, Epoch right)
        {
            return !(left == right);
        }

        public static Epoch operator %(Epoch left, Epoch right)
        {
            return new Epoch(left._value % right._value);
        }

        public static Epoch operator +(Epoch left, Epoch right)
        {
            return new Epoch(left._value + right._value);
        }

        public static bool operator <(Epoch left, Epoch right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(Epoch left, Epoch right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator ==(Epoch left, Epoch right)
        {
            return left.Equals(right);
        }

        public static bool operator >(Epoch left, Epoch right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(Epoch left, Epoch right)
        {
            return left.CompareTo(right) >= 0;
        }

        public int CompareTo(Epoch other)
        {
            return _value.CompareTo(other._value);
        }

        public override bool Equals(object obj)
        {
            return obj is Epoch slot && Equals(slot);
        }

        public bool Equals(Epoch other)
        {
            return _value == other._value;
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public override string ToString()
        {
            return _value.ToString();
        }
    }
}
