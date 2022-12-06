// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DataDeliveryReceiptMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DataDeliveryReceipt;
        public override string Protocol => "ndm";
        public Keccak DepositId { get; }
        public DataDeliveryReceipt Receipt { get; }

        public DataDeliveryReceiptMessage(Keccak depositId, DataDeliveryReceipt receipt)
        {
            DepositId = depositId;
            Receipt = receipt;
        }
    }
}
