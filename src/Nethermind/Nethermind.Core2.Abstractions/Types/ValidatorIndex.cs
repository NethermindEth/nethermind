// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;

namespace Nethermind.Core2.Types
{
    [DebuggerDisplay("{Number}")]
    public readonly struct ValidatorIndex : IEquatable<ValidatorIndex>, IComparable<ValidatorIndex>
    {
        public static ValidatorIndex? None => default;

        public static ValidatorIndex Zero => new ValidatorIndex(0);

        public ValidatorIndex(ulong number)
        {
            Number = number;
        }

        public ulong Number { get; }

        public int CompareTo(ValidatorIndex other)
        {
            return Number.CompareTo(other.Number);
        }

        public bool Equals(ValidatorIndex other)
        {
            return Number == other.Number;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValidatorIndex other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Number.GetHashCode();
        }

        public static ValidatorIndex Max(ValidatorIndex val1, ValidatorIndex val2)
        {
            return new ValidatorIndex(Math.Max(val1.Number, val2.Number));
        }

        public static bool operator ==(ValidatorIndex a, ValidatorIndex b)
        {
            return a.Number == b.Number;
        }

        public static explicit operator int(ValidatorIndex validatorIndex) => (int)validatorIndex.Number;

        public static implicit operator ulong(ValidatorIndex validatorIndex) => validatorIndex.Number;

        public static implicit operator ValidatorIndex(ulong value) => new ValidatorIndex(value);

        public static bool operator !=(ValidatorIndex a, ValidatorIndex b)
        {
            return !(a == b);
        }

        public override string ToString()
        {
            return Number.ToString();
        }
    }
}
