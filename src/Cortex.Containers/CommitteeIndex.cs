using System;

namespace Cortex.Containers
{
    public struct CommitteeIndex : IEquatable<CommitteeIndex>, IComparable<CommitteeIndex>
    {
        private readonly ulong _value;

        public CommitteeIndex(ulong value)
        {
            _value = value;
        }

        public static explicit operator CommitteeIndex(ulong value) => new CommitteeIndex(value);

        public static explicit operator ulong(CommitteeIndex committeeIndex) => committeeIndex._value;

        public static CommitteeIndex operator -(CommitteeIndex left, CommitteeIndex right)
        {
            return new CommitteeIndex(left._value - right._value);
        }

        public static bool operator !=(CommitteeIndex left, CommitteeIndex right)
        {
            return !(left == right);
        }

        public static CommitteeIndex operator +(CommitteeIndex left, CommitteeIndex right)
        {
            return new CommitteeIndex(left._value + right._value);
        }

        public static bool operator <(CommitteeIndex left, CommitteeIndex right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(CommitteeIndex left, CommitteeIndex right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator ==(CommitteeIndex left, CommitteeIndex right)
        {
            return left.Equals(right);
        }

        public static bool operator >(CommitteeIndex left, CommitteeIndex right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(CommitteeIndex left, CommitteeIndex right)
        {
            return left.CompareTo(right) >= 0;
        }

        public int CompareTo(CommitteeIndex other)
        {
            return _value.CompareTo(other._value);
        }

        public override bool Equals(object obj)
        {
            return obj is CommitteeIndex slot && Equals(slot);
        }

        public bool Equals(CommitteeIndex other)
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
