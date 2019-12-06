﻿using System;

namespace Cortex.Containers
{
    public struct ValidatorIndex : IEquatable<ValidatorIndex>, IComparable<ValidatorIndex>
    {
        private readonly ulong _value;

        public ValidatorIndex(ulong value)
        {
            _value = value;
        }

        public static ValidatorIndex None => new ValidatorIndex(ulong.MaxValue);

        public static explicit operator ulong(ValidatorIndex validatorIndex) => validatorIndex._value;

        public static explicit operator ValidatorIndex(ulong value) => new ValidatorIndex(value);

        public static ValidatorIndex Max(ValidatorIndex val1, ValidatorIndex val2)
        {
            return new ValidatorIndex(Math.Max(val1._value, val2._value));
        }

        public static bool operator !=(ValidatorIndex left, ValidatorIndex right)
        {
            return !(left == right);
        }

        public static bool operator <(ValidatorIndex left, ValidatorIndex right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(ValidatorIndex left, ValidatorIndex right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator ==(ValidatorIndex left, ValidatorIndex right)
        {
            return left.Equals(right);
        }

        public static bool operator >(ValidatorIndex left, ValidatorIndex right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(ValidatorIndex left, ValidatorIndex right)
        {
            return left.CompareTo(right) >= 0;
        }

        public int CompareTo(ValidatorIndex other)
        {
            return _value.CompareTo(other._value);
        }

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
    }
}
