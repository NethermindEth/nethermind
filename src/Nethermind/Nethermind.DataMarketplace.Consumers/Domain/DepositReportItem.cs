/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Domain
{
    public class DepositReportItem
    {
        public Keccak Id { get; }
        public Keccak HeaderId { get; }
        public string HeaderName { get; }
        public Address Provider { get; }
        public string ProviderName { get; }
        public UInt256 Value { get; }
        public uint Units { get; }
        public bool Completed { get; }
        public uint Timestamp { get; set; }
        public uint ExpiryTime { get; }
        public bool Expired { get; }
        public Keccak TransactionHash { get; }
        public uint VerificationTimestamp { get; }
        public bool Verified { get; }
        public Keccak ClaimedRefundTransactionHash { get; }
        public bool RefundClaimed { get; }
        public uint ConsumedUnits { get; }
        public uint ClaimedUnits { get; }
        public uint RefundedUnits { get; }
        public uint RemainingUnits { get; }
        public UInt256 ClaimedValue { get; }
        public UInt256 RefundedValue { get; }
        public UInt256 RemainingValue { get; }
        public IEnumerable<DataDeliveryReceiptReportItem> Receipts { get; }

        public DepositReportItem(Keccak id, Keccak headerId, string headerName, Address provider, string providerName,
            UInt256 value, uint units, uint timestamp, uint expiryTime, bool expired, Keccak transactionHash,
            uint verificationTimestamp, Keccak claimedRefundTransactionHash, uint consumedUnits,
            IEnumerable<DataDeliveryReceiptReportItem> receipts)
        {
            Id = id;
            HeaderId = headerId;
            HeaderName = headerName;
            Provider = provider;
            ProviderName = providerName;
            Value = value;
            Units = units;
            Timestamp = timestamp;
            ExpiryTime = expiryTime;
            Expired = expired;
            TransactionHash = transactionHash;
            VerificationTimestamp = verificationTimestamp;
            Verified = verificationTimestamp > 0;
            ClaimedRefundTransactionHash = claimedRefundTransactionHash;
            RefundClaimed = !(claimedRefundTransactionHash is null);
            ConsumedUnits = consumedUnits;
            Receipts = receipts ?? Enumerable.Empty<DataDeliveryReceiptReportItem>();
            if (Receipts.Any())
            {
                ClaimedUnits = 1 + Receipts.Max(r => r.Request.UnitsRange.To) -
                               Receipts.Min(r => r.Request.UnitsRange.From);
                ClaimedValue = (UInt256) (ClaimedUnits * (BigInteger) Value / Units);
            }

            if (RefundClaimed)
            {
                RefundedUnits = Units - ClaimedUnits;
                RefundedValue = Value - ClaimedValue;
            }

            RemainingValue = Value - ClaimedValue - RefundedValue;
            RemainingUnits = Units - ConsumedUnits;
            Completed = ClaimedUnits + RefundedUnits >= Units;
        }
    }
}