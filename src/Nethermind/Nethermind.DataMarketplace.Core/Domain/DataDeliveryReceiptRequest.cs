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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataDeliveryReceiptRequest : IEquatable<DataDeliveryReceiptRequest>
    {
        public uint Number { get; private set; }
        public Keccak DepositId { get; private set; }
        public UnitsRange UnitsRange { get; private set; }
        public IEnumerable<DataDeliveryReceiptToMerge> ReceiptsToMerge { get; private set; }
        public bool IsSettlement { get; private set; }

        public DataDeliveryReceiptRequest(uint number, Keccak depositId, UnitsRange unitsRange,
            bool isSettlement = false, IEnumerable<DataDeliveryReceiptToMerge>? receiptsToMerge = null)
        {
            Number = number;
            DepositId = depositId;
            UnitsRange = unitsRange;
            IsSettlement = isSettlement;
            ReceiptsToMerge = receiptsToMerge ?? Enumerable.Empty<DataDeliveryReceiptToMerge>();
        }

        public DataDeliveryReceiptRequest WithRange(UnitsRange range, uint number)
            => new DataDeliveryReceiptRequest(number, DepositId, range, IsSettlement, ReceiptsToMerge);

        public bool Equals(DataDeliveryReceiptRequest? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Number == other.Number && Equals(DepositId, other.DepositId);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((DataDeliveryReceiptRequest) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int) Number * 397) ^ (DepositId != null ? DepositId.GetHashCode() : 0);
            }
        }
    }
}
