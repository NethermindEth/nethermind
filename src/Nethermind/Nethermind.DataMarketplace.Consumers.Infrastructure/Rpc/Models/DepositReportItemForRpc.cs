using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DepositReportItemForRpc
    {
        public Keccak Id { get; set; }
        public Keccak HeaderId { get; set; }
        public string HeaderName { get; set; }
        public Address Provider { get; set; }
        public string ProviderName { get; set; }
        public UInt256 Value { get; set; }
        public uint Units { get; set; }
        public bool Completed { get; set; }
        public uint Timestamp { get; set; }
        public uint ExpiryTime { get; set; }
        public bool Expired { get; set; }
        public Keccak TransactionHash { get; set; }
        public uint VerificationTimestamp { get; set; }
        public bool Verified { get; set; }
        public Keccak ClaimedRefundTransactionHash { get; set; }
        public bool RefundClaimed { get; set; }
        public uint ConsumedUnits { get; set; }
        public uint ClaimedUnits { get; set; }
        public uint RefundedUnits { get; set; }
        public uint RemainingUnits { get; set; }
        public UInt256 ClaimedValue { get; set; }
        public UInt256 RefundedValue { get; set; }
        public UInt256 RemainingValue { get; set; }
        public IEnumerable<DataDeliveryReceiptReportItemForRpc> Receipts { get; set; }
        
        public DepositReportItemForRpc()
        {
        }

        public DepositReportItemForRpc(DepositReportItem report)
        {
            Id = report.Id;
            HeaderId = report.HeaderId;
            HeaderName = report.HeaderName;
            Provider = report.Provider;
            ProviderName = report.ProviderName;
            Value = report.Value;
            Units = report.Units;
            Completed = report.Completed;
            Timestamp = report.Timestamp;
            ExpiryTime = report.ExpiryTime;
            Expired = report.Expired;
            TransactionHash = report.TransactionHash;
            VerificationTimestamp = report.VerificationTimestamp;
            Verified = report.Verified;
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