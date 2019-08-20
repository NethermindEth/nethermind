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

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DataDeliveryReceiptReportItemForRpc
    {
        public Keccak Id { get; set; }
        public uint Number { get; set; }
        public Keccak SessionId { get; set; }
        public PublicKey NodeId { get; set; }
        public DataDeliveryReceiptRequestForRpc Request { get; set; }
        public DataDeliveryReceiptForRpc Receipt { get; set; }
        public ulong Timestamp { get; set; }
        public bool IsMerged { get; set; }
        public bool IsClaimed { get; set; }

        public DataDeliveryReceiptReportItemForRpc()
        {
        }

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
    }
}