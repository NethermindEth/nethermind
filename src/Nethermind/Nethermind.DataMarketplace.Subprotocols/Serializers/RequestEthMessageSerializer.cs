// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class RequestEthMessageSerializer : IMessageSerializer<RequestEthMessage>
    {
        public byte[] Serialize(RequestEthMessage message)
            => Rlp.Encode(Rlp.Encode(message.Address),
                Rlp.Encode(message.Value)).Bytes;

        public RequestEthMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var address = context.DecodeAddress();
            var value = context.DecodeUInt256();

            return new RequestEthMessage(address, value);
        }
    }
}
