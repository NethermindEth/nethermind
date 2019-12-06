using System;

namespace Nethermind.BeaconNode.Containers
{
    public struct Slot : IEquatable<Slot>, IComparable<Slot>
    {
        private readonly ulong _value;

        public Slot(ulong value)
        {
            _value = value;
        }

        public static Slot None => new Slot(ulong.MaxValue);

        public static Slot Zero => new Slot(0);

        public static explicit operator Slot(ulong value) => new Slot(value);

        public static explicit operator ulong(Slot slot) => slot._value;

        public static Slot operator -(Slot left, Slot right)
        {
            return new Slot(left._value - right._value);
        }

        public static bool operator !=(Slot left, Slot right)
        {
            return !(left == right);
        }

        public static Slot operator %(Slot left, Slot right)
        {
            return new Slot(left._value % right._value);
        }

        public static Slot operator *(Slot left, ulong right)
        {
            return new Slot(left._value * right);
        }

        public static ulong operator /(Slot left, Slot right)
        {
            return left._value / right._value;
        }

        public static Slot operator +(Slot left, Slot right)
        {
            return new Slot(left._value + right._value);
        }

        public static bool operator <(Slot left, Slot right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(Slot left, Slot right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator ==(Slot left, Slot right)
        {
            return left.Equals(right);
        }

        public static bool operator >(Slot left, Slot right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(Slot left, Slot right)
        {
            return left.CompareTo(right) >= 0;
        }

        public int CompareTo(Slot other)
        {
            return _value.CompareTo(other._value);
        }

        public override bool Equals(object obj)
        {
            return obj is Slot slot && Equals(slot);
        }

        public bool Equals(Slot other)
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
