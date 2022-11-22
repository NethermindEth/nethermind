// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DepositReportItemForRpc
    {
        public Keccak Id { get; }
        public Keccak AssetId { get; }
        public string AssetName { get; }
        public Address Provider { get; }
        public string ProviderName { get; }
        public UInt256 Value { get; }
        public uint Units { get; }
        public Address Consumer { get; }
        public bool Completed { get; }
        public uint Timestamp { get; }
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
        public IEnumerable<DataDeliveryReceiptReportItemForRpc> Receipts { get; }

        public DepositReportItemForRpc(DepositReportItem report)
        {
            Id = report.Id;
            AssetId = report.AssetId;
            AssetName = report.AssetName;
            Provider = report.Provider;
            ProviderName = report.ProviderName;
            Value = report.Value;
            Units = report.Units;
            Consumer = report.Consumer;
            Completed = report.Completed;
            Timestamp = report.Timestamp;
            ExpiryTime = report.ExpiryTime;
            Expired = report.Expired;
            TransactionHash = report.TransactionHash;
            ConfirmationTimestamp = report.ConfirmationTimestamp;
            Confirmations = report.Confirmations;
            RequiredConfirmations = report.RequiredConfirmations;
            Confirmed = report.Confirmed;
            Rejected = report.Rejected;
            ClaimedRefundTransactionHash = report.ClaimedRefundTransactionHash;
            RefundClaimed = report.RefundClaimed;
            ConsumedUnits = report.ConsumedUnits;
            ClaimedUnits = report.ClaimedUnits;
            RefundedUnits = report.RefundedUnits;
            RemainingUnits = report.RemainingUnits;
            ClaimedValue = report.ClaimedValue;
            RefundedValue = report.RefundedValue;
            RemainingValue = report.RemainingValue;
            Receipts = report.Receipts.Select(r => new DataDeliveryReceiptReportItemForRpc(r));
        }
    }
}
