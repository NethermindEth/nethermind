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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DepositReportItemForRpc
    {
        public Keccak Id { get; set; }
        public Keccak AssetId { get; set; }
        public string AssetName { get; set; }
        public Address Provider { get; set; }
        public string ProviderName { get; set; }
        public UInt256 Value { get; set; }
        public uint Units { get; set; }
        public Address Consumer { get; set; }
        public bool Completed { get; set; }
        public uint Timestamp { get; set; }
        public uint ExpiryTime { get; set; }
        public bool Expired { get; set; }
        public Keccak TransactionHash { get; set; }
        public uint ConfirmationTimestamp { get; set; }
        public uint Confirmations { get; set; }
        public uint RequiredConfirmations { get; set; }
        public bool Confirmed { get; set; }
        public bool Rejected { get; set; }
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