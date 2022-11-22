// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class RequestDataDeliveryReceiptMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.RequestDataDeliveryReceipt;
        public override string Protocol => "ndm";
        public DataDeliveryReceiptRequest Request { get; }

        public RequestDataDeliveryReceiptMessage(DataDeliveryReceiptRequest request)
        {
            Request = request;
        }
    }
}
