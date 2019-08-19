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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery.Serializers
{
    public class PingMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<PingMessage>
    {
        public PingMessageSerializer(IEcdsa ecdsa, IPrivateKeyGenerator privateKeyGenerator, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver) : base(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver)
        {
        }

        public byte[] Serialize(PingMessage message)
        {
            byte typeByte = (byte)message.MessageType;
            Rlp source = Encode(message.SourceAddress);
            Rlp destination = Encode(message.DestinationAddress);
            byte[] data = Rlp.Encode(
                Rlp.Encode(message.Version),
                source,
                destination,
                //verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeByte, data);
            return serializedMsg;
        }

        public PingMessage Deserialize(byte[] msg)
        {
            var results = PrepareForDeserialization<PingMessage>(msg);
            
            var rlp = results.Data.AsRlpStream();
            rlp.ReadSequenceLength();
            var version = rlp.DecodeInt();

            rlp.ReadSequenceLength();
            byte[] sourceAddress = rlp.DecodeByteArray();
            var source = GetAddress(sourceAddress, rlp.DecodeInt());
            rlp.DecodeInt(); // UDP port
            rlp.ReadSequenceLength();
            byte[] destinationAddress = rlp.DecodeByteArray();
            var destination = GetAddress(destinationAddress, rlp.DecodeInt());
            rlp.DecodeInt(); // UDP port

            var expireTime = rlp.DecodeLong();

            var message = results.Message;
            message.SourceAddress = source;
            message.DestinationAddress = destination;
            message.Mdc = results.Mdc;
            message.Version = version;
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}