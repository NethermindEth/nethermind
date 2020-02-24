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

        public bool Equals(DataDeliveryReceiptToMerge other)
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
            return Equals((DataDeliveryReceiptToMerge) obj);
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