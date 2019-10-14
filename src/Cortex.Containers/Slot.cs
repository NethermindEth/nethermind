using System;

namespace Cortex.Containers
{
    public struct Slot : IEquatable<Slot>, IComparable<Slot>
    {
        private readonly ulong _value;

        public Slot(ulong value)
        {
            _value = value;
        }

        public static explicit operator Slot(ulong value) => new Slot(value);

        public static explicit operator ulong(Slot slot) => slot._value;

        public static bool operator !=(Slot left, Slot right)
        {
            return !(left == right);
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
