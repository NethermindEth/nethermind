// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class ConsumerAddressChangedMessageSerializer : IMessageSerializer<ConsumerAddressChangedMessage>
    {
        public byte[] Serialize(ConsumerAddressChangedMessage message)
            => Rlp.Encode(Rlp.Encode(message.Address)).Bytes;

        public ConsumerAddressChangedMessage Deserialize(byte[] bytes)
        {
            RlpStream context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Address? address = context.DecodeAddress();
            if (address == null)
            {
                throw new InvalidDataException($"{nameof(ConsumerAddressChangedMessage)}.{nameof(ConsumerAddressChangedMessage.Address)} cannot be null");
            }

            return new ConsumerAddressChangedMessage(address);
        }
    }
}
