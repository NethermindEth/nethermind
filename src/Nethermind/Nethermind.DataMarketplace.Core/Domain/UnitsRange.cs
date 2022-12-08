// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class UnitsRange : IEquatable<UnitsRange>
    {
        public uint From { get; private set; }
        public uint To { get; private set; }
        public uint Units { get; private set; }

        public UnitsRange(uint from, uint to)
        {
            if (from > to)
            {
                throw new ArgumentException($"Invalid range: [{from}, {to}]", nameof(from));
            }

            From = from;
            To = to;
            Units = To - From + 1;
        }

        public bool IntersectsWith(UnitsRange unitsRange)
            => From <= unitsRange.To && To >= unitsRange.From;

        public bool IsSubsetOf(UnitsRange unitsRange)
            => From >= unitsRange.From && To <= unitsRange.To;

        public bool Equals(UnitsRange? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return From == other.From && To == other.To;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((UnitsRange)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)From * 397) ^ (int)To;
            }
        }
    }
}
