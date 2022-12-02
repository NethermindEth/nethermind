// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class EarlyRefundTicketMessageSerializer : IMessageSerializer<EarlyRefundTicketMessage>
    {
        public byte[] Serialize(EarlyRefundTicketMessage message)
            => Rlp.Encode(Rlp.Encode(message.Ticket),
                Rlp.Encode((int)message.Reason)).Bytes;

        public EarlyRefundTicketMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var ticket = Rlp.Decode<EarlyRefundTicket>(context);
            var reason = (RefundReason)context.DecodeInt();

            return new EarlyRefundTicketMessage(ticket, reason);
        }
    }
}
