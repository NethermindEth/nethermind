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