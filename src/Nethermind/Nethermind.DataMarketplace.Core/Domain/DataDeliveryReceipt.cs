// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataDeliveryReceipt : IEquatable<DataDeliveryReceipt>
    {
        public StatusCodes StatusCode { get; }
        public uint ConsumedUnits { get; }
        public uint UnpaidUnits { get; }
        public Signature? Signature { get; }

        public DataDeliveryReceipt(StatusCodes statusCode, uint consumedUnits, uint unpaidUnits, Signature signature)
        {
            StatusCode = statusCode;
            ConsumedUnits = consumedUnits;
            UnpaidUnits = unpaidUnits;
            Signature = signature;
        }

        public bool Equals(DataDeliveryReceipt? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return StatusCode == other.StatusCode && ConsumedUnits == other.ConsumedUnits &&
                   UnpaidUnits == other.UnpaidUnits && Equals(Signature, other.Signature);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((DataDeliveryReceipt)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (int)StatusCode;
                hashCode = (hashCode * 397) ^ (int)ConsumedUnits;
                hashCode = (hashCode * 397) ^ (int)UnpaidUnits;
                hashCode = (hashCode * 397) ^ (Signature != null ? Signature.GetHashCode() : 0);
                return hashCode;
            }
        }
    }
}
