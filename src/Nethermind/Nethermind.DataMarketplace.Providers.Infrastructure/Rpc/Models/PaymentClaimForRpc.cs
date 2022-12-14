// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class PaymentClaimForRpc
    {
        public Keccak Id { get; set; }
        public Keccak DepositId { get; set; }
        public Keccak AssetId { get; set; }
        public string AssetName { get; set; }
        public uint Units { get; set; }
        public uint ClaimedUnits { get; set; }
        public UnitsRangeForRpc UnitsRange { get; set; }
        public UInt256 Value { get; set; }
        public UInt256 ClaimedValue { get; set; }
        public uint ExpiryTime { get; set; }
        public Address Provider { get; set; }
        public Address Consumer { get; set; }
        public IEnumerable<TransactionInfoForRpc> Transactions { get; set; }
        public TransactionInfoForRpc? Transaction { get; set; }
        public UInt256 TransactionCost { get; set; }
        public UInt256 Income { get; set; }
        public ulong Timestamp { get; set; }
        public string Status { get; set; }

        public PaymentClaimForRpc(PaymentClaim paymentClaim)
        {
            Id = paymentClaim.Id;
            DepositId = paymentClaim.DepositId;
            AssetId = paymentClaim.AssetId;
            AssetName = paymentClaim.AssetName;
            Units = paymentClaim.Units;
            ClaimedUnits = paymentClaim.ClaimedUnits;
            UnitsRange = new UnitsRangeForRpc(paymentClaim.UnitsRange);
            Value = paymentClaim.Value;
            ClaimedValue = paymentClaim.ClaimedValue;
            ExpiryTime = paymentClaim.ExpiryTime;
            Provider = paymentClaim.Provider;
            Consumer = paymentClaim.Consumer;
            Transaction = paymentClaim.Transaction is null ? null : new TransactionInfoForRpc(paymentClaim.Transaction);
            Transactions = paymentClaim.Transactions?.Select(t => new TransactionInfoForRpc(t))
                               .OrderBy(t => t.Timestamp) ??
                           Enumerable.Empty<TransactionInfoForRpc>();
            TransactionCost = paymentClaim.TransactionCost;
            Income = paymentClaim.Income;
            Timestamp = paymentClaim.Timestamp;
            Status = paymentClaim.Status.ToString().ToLowerInvariant();
        }
    }
}
