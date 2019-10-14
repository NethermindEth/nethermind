using System;

namespace Cortex.Containers
{
    public struct Epoch : IEquatable<Epoch>, IComparable<Epoch>
    {
        private readonly ulong _value;

        public Epoch(ulong value)
        {
            _value = value;
        }

        public static explicit operator Epoch(ulong value) => new Epoch(value);

        public static explicit operator ulong(Epoch slot) => slot._value;

        public static bool operator !=(Epoch left, Epoch right)
        {
            return !(left == right);
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
