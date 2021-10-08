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