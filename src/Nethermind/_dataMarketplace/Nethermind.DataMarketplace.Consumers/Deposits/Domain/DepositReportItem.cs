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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Domain
{
    public class DepositReportItem
    {
        public Keccak Id { get; }
        public Keccak AssetId { get; }
        public string AssetName { get; }
        public Address Provider { get; }
        public string ProviderName { get; }
        public UInt256 Value { get; }
        public uint Units { get; }
        public Address Consumer { get; set; }
        public bool Completed { get; }
        public uint Timestamp { get; set; }
        public uint ExpiryTime { get; }
        public bool Expired { get; }
        public Keccak? TransactionHash { get; }
        public uint ConfirmationTimestamp { get; }
        public uint Confirmations { get; }
        public uint RequiredConfirmations { get; }
        public bool Confirmed { get; }
        public bool Rejected { get; }
        public Keccak? ClaimedRefundTransactionHash { get; }
        public bool RefundClaimed { get; }
        public uint ConsumedUnits { get; }
        public uint ClaimedUnits { get; }
        public uint RefundedUnits { get; }
        public uint RemainingUnits { get; }
        public UInt256 ClaimedValue { get; }
        public UInt256 RefundedValue { get; }
        public UInt256 RemainingValue { get; }
        public IEnumerable<DataDeliveryReceiptReportItem> Receipts { get; }

        public DepositReportItem(
            Keccak id,
            Keccak assetId,
            string assetName,
            Address provider,
            string providerName,
            UInt256 value,
            uint units,
            Address consumer,
            uint timestamp,
            uint expiryTime,
            bool expired,
            Keccak? transactionHash,
            uint confirmationTimestamp,
            uint confirmations,
            uint requiredConfirmations,
            bool confirmed,
            bool rejected,
            Keccak? claimedRefundTransactionHash,
            bool refundClaimed,
            uint consumedUnits,
            IEnumerable<DataDeliveryReceiptReportItem> receipts)
        {
            Id = id;
            AssetId = assetId;
            AssetName = assetName;
            Provider = provider;
            ProviderName = providerName;
            Value = value;
            Units = units;
            Consumer = consumer;
            Timestamp = timestamp;
            ExpiryTime = expiryTime;
            Expired = expired;
            TransactionHash = transactionHash;
            ConfirmationTimestamp = confirmationTimestamp;
            Confirmations = confirmations;
            RequiredConfirmations = requiredConfirmations;
            Confirmed = confirmed;
            Rejected = rejected;
            ClaimedRefundTransactionHash = claimedRefundTransactionHash;
            RefundClaimed = refundClaimed;
            ConsumedUnits = consumedUnits;
            Receipts = receipts;
            if (Receipts.Any())
            {
                ClaimedUnits = 1 + Receipts.Max(r => r.Request.UnitsRange.To) -
                               Receipts.Min(r => r.Request.UnitsRange.From);
                ClaimedValue = ClaimedUnits * Value / Units;
            }

            if (RefundClaimed)
            {
                RefundedUnits = Units - ClaimedUnits;
                RefundedValue = Value - ClaimedValue;
            }

            if (Value < ClaimedValue + RefundedValue)
            {
                throw new InvalidDataException(
                    $"Deposit {nameof(Value)} ({Value}) cannot be less than a sum of {nameof(ClaimedValue)} ({ClaimedValue}) and {nameof(RefundedValue)} ({RefundedValue})");
            }
            
            if (Units < ConsumedUnits)
            {
                ConsumedUnits = Units;
            }
            
            RemainingValue = Value - ClaimedValue - RefundedValue;
            RemainingUnits = Units - ConsumedUnits;
            Completed = ClaimedUnits + RefundedUnits >= Units;
        }
    }
}
