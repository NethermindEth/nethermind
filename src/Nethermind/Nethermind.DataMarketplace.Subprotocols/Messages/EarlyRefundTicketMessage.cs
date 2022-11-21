// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class EarlyRefundTicketMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.EarlyRefundTicket;
        public override string Protocol => "ndm";
        public EarlyRefundTicket Ticket { get; }
        public RefundReason Reason { get; }

        public EarlyRefundTicketMessage(EarlyRefundTicket ticket, RefundReason reason)
        {
            Ticket = ticket;
            Reason = reason;
        }
    }
}
