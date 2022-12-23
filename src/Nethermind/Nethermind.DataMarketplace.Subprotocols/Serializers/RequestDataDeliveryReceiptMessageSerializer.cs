// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class RequestDataDeliveryReceiptMessageSerializer : IMessageSerializer<RequestDataDeliveryReceiptMessage>
    {
        public byte[] Serialize(RequestDataDeliveryReceiptMessage message)
            => Rlp.Encode(Rlp.Encode(message.Request)).Bytes;

        public RequestDataDeliveryReceiptMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var request = Rlp.Decode<DataDeliveryReceiptRequest>(context);

            return new RequestDataDeliveryReceiptMessage(request);
        }
    }
}
