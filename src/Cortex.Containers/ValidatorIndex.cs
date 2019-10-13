using System;

namespace Cortex.Containers
{
    public struct ValidatorIndex : IEquatable<ValidatorIndex>
    {
        private readonly ulong _value;

        public ValidatorIndex(ulong value)
        {
            _value = value;
        }

        public static implicit operator ulong(ValidatorIndex slot) => slot._value;

        public static implicit operator ValidatorIndex(ulong value) => new ValidatorIndex(value);

        public override bool Equals(object obj)
        {
            return obj is ValidatorIndex slot && Equals(slot);
        }

        public bool Equals(ValidatorIndex other)
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

        public static bool operator ==(ValidatorIndex left, ValidatorIndex right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ValidatorIndex left, ValidatorIndex right)
        {
            return !(left == right);
        }
    }
}
