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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats;

namespace Nethermind.Network.Discovery.Serializers
{
    public class TopicRegisterMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<TopicRegisterMessage>
    {
        public TopicRegisterMessageSerializer(ISigner signer, IPrivateKeyGenerator privateKeyGenerator, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKeyGenerator, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(TopicRegisterMessage message)
        {
            byte[] typeBytes = { (byte)message.MessageType };
            byte[] data = Rlp.Encode(
                Rlp.Encode<string>(message.Topics.Select(c => c.ToString()).ToArray()),
                Rlp.Encode(message.Idx),
                Rlp.Encode(message.Pong.FarAddress), //TODO: Convert to use PongMessageSerializer
                Rlp.Encode(message.Pong.PingMdc),
                Rlp.Encode(message.Pong.ExpirationTime),
                Rlp.Encode(message.Pong.TopicHash),
                Rlp.Encode(message.Pong.TicketSerial),
                Rlp.Encode(message.Pong.WaitPeriods)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data);
            return serializedMsg;
        }

        public TopicRegisterMessage Deserialize(byte[] msg)
        {
            var results = PrepareForDeserialization<TopicRegisterMessage>(msg);

            var rlp = results.Data.AsRlpContext();

            rlp.ReadSequenceLength();
            rlp.ReadSequenceLength();

            var topics = Array.ConvertAll(rlp.DecodeArray<string>(rlp), t => new Topic(t));
            var idx = rlp.DecodeShort();
            var pongFarAddress = rlp.DecodeByteArray();
            var pongFarAddressPort = rlp.DecodeInt();

            var pongFarAddressUDPPort = rlp.DecodeInt();
            var pingMdc = rlp.DecodeByteArray();
            var pongExpirationTime = rlp.DecodeUint();
            var pongWaitPeriods = rlp.DecodeArray(ctx => ctx.DecodeUInt());

            var message = results.Message;
            message.Topics = topics;
            message.Idx = idx;
            message.Pong = MessageFactory.CreateIncomingMessage<PongMessage>(message.FarPublicKey);
            message.Pong.PingMdc = pingMdc;
            message.Pong.ExpirationTime = pongExpirationTime;
            message.Pong.WaitPeriod = pongWaitPeriods;

            return message;
        }
    }
}