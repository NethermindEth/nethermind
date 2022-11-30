// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;

namespace Nethermind.Core2.Types
{
    [DebuggerDisplay("{Number}")]
    public struct CommitteeIndex : IEquatable<CommitteeIndex>, IComparable<CommitteeIndex>
    {
        public CommitteeIndex(ulong number)
        {
            Number = number;
        }

        public ulong Number { get; }

        public static CommitteeIndex? None => null;

        public static CommitteeIndex One => new CommitteeIndex(1);

        public static CommitteeIndex Zero => new CommitteeIndex(0);

        public static explicit operator CommitteeIndex(ulong value) => new CommitteeIndex(value);

        public static explicit operator ulong(CommitteeIndex committeeIndex) => committeeIndex.Number;

        public static CommitteeIndex operator -(CommitteeIndex left, CommitteeIndex right)
        {
            return new CommitteeIndex(left.Number - right.Number);
        }

        public static bool operator !=(CommitteeIndex left, CommitteeIndex right)
        {
            return !(left == right);
        }

        public static CommitteeIndex operator +(CommitteeIndex left, CommitteeIndex right)
        {
            return new CommitteeIndex(left.Number + right.Number);
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
            return Number.CompareTo(other.Number);
        }

        public override bool Equals(object? obj)
        {
            return obj is CommitteeIndex slot && Equals(slot);
        }

        public bool Equals(CommitteeIndex other)
        {
            return Number == other.Number;
        }

        public override int GetHashCode()
        {
            return Number.GetHashCode();
        }

        public override string ToString()
        {
            return Number.ToString();
        }
    }
}
