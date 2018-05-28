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
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;

namespace Nethermind.Discovery.Serializers
{
    public class PingMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<PingMessage>
    {
        public PingMessageSerializer(ISigner signer, IPrivateKeyProvider privateKeyProvider, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKeyProvider, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(PingMessage message)
        {
            byte[] typeBytes = { (byte)message.MessageType };
            Rlp source = Encode(message.SourceAddress);
            Rlp destination = Encode(message.DestinationAddress);
            byte[] data = Rlp.Encode(
                Rlp.Encode(message.Version),
                source,
                destination,
                //verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data);
            return serializedMsg;
        }

        public PingMessage Deserialize(byte[] msg)
        {
            var results = Deserialize<PingMessage>(msg);
            
            var rlp = new Rlp(results.Data);
            DecodedRlp decodedRaw = Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);
            var version = decodedRaw.GetInt(0);

            var sourceRlp = (DecodedRlp)decodedRaw.Items[1];
            var source = GetAddress(sourceRlp.GetBytes(0), sourceRlp.GetBytes(1));

            var destinationRlp = (DecodedRlp)decodedRaw.Items[2];
            var destination = GetAddress(destinationRlp.GetBytes(0), destinationRlp.GetBytes(1));

            var expireTime = (decodedRaw.GetBytes(3)).ToInt64();

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