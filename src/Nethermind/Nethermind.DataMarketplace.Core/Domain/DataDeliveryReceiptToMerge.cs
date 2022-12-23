// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataDeliveryReceiptToMerge : IEquatable<DataDeliveryReceiptToMerge>
    {
        public UnitsRange UnitsRange { get; }
        public Signature Signature { get; }

        public DataDeliveryReceiptToMerge(UnitsRange unitsRange, Signature signature)
        {
            UnitsRange = unitsRange;
            Signature = signature;
        }

        public bool Equals(DataDeliveryReceiptToMerge? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(UnitsRange, other.UnitsRange) && Equals(Signature, other.Signature);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((DataDeliveryReceiptToMerge)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((UnitsRange != null ? UnitsRange.GetHashCode() : 0) * 397) ^ (Signature != null ? Signature.GetHashCode() : 0);
            }
        }
    }
}
