//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

        public static explicit operator int(ValidatorIndex validatorIndex) => (int) validatorIndex.Number;

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