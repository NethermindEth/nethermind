// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
