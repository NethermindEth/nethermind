// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class EthRequestedMessageSerializer : IMessageSerializer<EthRequestedMessage>
    {
        public byte[] Serialize(EthRequestedMessage message)
            => Rlp.Encode(Rlp.Encode(message.Response)).Bytes;

        public EthRequestedMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var response = Rlp.Decode<FaucetResponse>(context);

            return new EthRequestedMessage(response);
        }
    }
}
