// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DataDeliveryReceiptReportItemForRpc
    {
        public DataDeliveryReceiptReportItemForRpc(DataDeliveryReceiptReportItem receipt)
        {
            Id = receipt.Id;
            Number = receipt.Number;
            SessionId = receipt.SessionId;
            NodeId = receipt.NodeId;
            Request = new DataDeliveryReceiptRequestForRpc(receipt.Request);
            Receipt = new DataDeliveryReceiptForRpc(receipt.Receipt);
            Timestamp = receipt.Timestamp;
            IsMerged = receipt.IsMerged;
            IsClaimed = receipt.IsClaimed;
        }

        public Keccak Id { get; }
        public uint Number { get; }
        public Keccak SessionId { get; }
        public PublicKey NodeId { get; }
        public DataDeliveryReceiptRequestForRpc Request { get; }
        public DataDeliveryReceiptForRpc Receipt { get; }
        public ulong Timestamp { get; }
        public bool IsMerged { get; }
        public bool IsClaimed { get; }
    }
}
