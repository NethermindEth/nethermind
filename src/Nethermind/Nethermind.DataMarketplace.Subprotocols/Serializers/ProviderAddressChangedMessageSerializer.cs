// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class ProviderAddressChangedMessageSerializer : IMessageSerializer<ProviderAddressChangedMessage>
    {
        public byte[] Serialize(ProviderAddressChangedMessage message)
            => Rlp.Encode(Rlp.Encode(message.Address)).Bytes;

        public ProviderAddressChangedMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var address = context.DecodeAddress();

            return new ProviderAddressChangedMessage(address);
        }
    }
}
