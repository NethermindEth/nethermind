// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataDeliveryReceiptDetails : IEquatable<DataDeliveryReceiptDetails>
    {
        public Keccak Id { get; private set; }
        public uint Number { get; private set; }
        public Keccak DepositId { get; private set; }
        public Keccak SessionId { get; private set; }
        public Keccak DataAssetId { get; private set; }
        public PublicKey ConsumerNodeId { get; private set; }
        public DataDeliveryReceiptRequest Request { get; private set; }
        public DataDeliveryReceipt Receipt { get; private set; }
        public ulong Timestamp { get; private set; }
        public bool IsMerged { get; private set; }
        public bool IsClaimed { get; private set; }

        public DataDeliveryReceiptDetails(Keccak id, Keccak sessionId, Keccak dataAssetId, PublicKey consumerNodeId,
            DataDeliveryReceiptRequest request, DataDeliveryReceipt receipt, ulong timestamp, bool isClaimed)
        {
            Id = id;
            Number = request.Number;
            DepositId = request.DepositId;
            SessionId = sessionId;
            DataAssetId = dataAssetId;
            ConsumerNodeId = consumerNodeId;
            Request = request;
            Receipt = receipt;
            Timestamp = timestamp;
            IsMerged = request.ReceiptsToMerge.Any();
            IsClaimed = isClaimed;
        }

        public void Claim()
        {
            IsClaimed = true;
        }

        public bool Equals(DataDeliveryReceiptDetails? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Number == other.Number && Equals(DepositId, other.DepositId);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((DataDeliveryReceiptDetails)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Number * 397) ^ (DepositId != null ? DepositId.GetHashCode() : 0);
            }
        }
    }
}
