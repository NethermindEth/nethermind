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
    public class PongMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<PongMessage>
    {
        public PongMessageSerializer(IEcdsa ecdsa, IPrivateKeyGenerator privateKeyGenerator, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver) : base(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver)
        {
        }

        public byte[] Serialize(PongMessage message)
        {
            byte[] typeBytes = { (byte)message.MessageType };

            var topicsBytes = new Rlp[message.Topics.Count()];
            Keccak topicsMdc;
            if (topicsBytes.Any())
            {
                for (int i = 0; i < message.Topics.Count(); i++)
                {
                    topicsBytes[i] = SerializeTopic(message.Topics[i]);
                }
                topicsMdc = Keccak.Compute(Rlp.Encode(topicsBytes).Bytes);
            } else {
                topicsMdc = Keccak.OfAnEmptySequenceRlp;
            }

            Rlp[] waitPeriods;
            if (message.WaitPeriods == null || !message.WaitPeriods.Any())
            {
                // default value of topics is Rlp.OfEmptySequence ASCII (192, 0x0c)
                // should branch to Rlp.Encode<t>(t[] item, ...) with t[0]/t = Rlp.OfAnEmptySequence
                waitPeriods = new Rlp[Rlp.LengthOfEmptyArrayRlp];
                waitPeriods[0] = null;
            } else {
                waitPeriods = new Rlp[message.WaitPeriods.Length];
                for (var i = 0; i < message.WaitPeriods.Length; i++)
                {
                    waitPeriods[i] = Rlp.Encode(message.WaitPeriods[i]);
                }
            }

            byte[] data = Rlp.Encode(
                Encode(message.FarAddress),
                Rlp.Encode(message.PingMdc),
                Rlp.Encode(message.ExpirationTime),
                Rlp.Encode(topicsMdc),
                Rlp.Encode(message.TicketSerial),
                Rlp.Encode(waitPeriods)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data);
            return serializedMsg;
        }

        public PongMessage Deserialize(byte[] msg)
        {
            var results = PrepareForDeserialization<PongMessage>(msg);

            var rlp = results.Data.AsRlpContext();

            rlp.ReadSequenceLength();
            rlp.ReadSequenceLength();

            // GetAddress(rlp.DecodeByteArray(), rlp.DecodeInt());
            rlp.DecodeByteArray();
            rlp.DecodeInt();

            rlp.DecodeInt(); // UDP port
            var token = rlp.DecodeByteArray();
            var expirationTime = rlp.DecodeLong();

            var topicMdc = rlp.DecodeByteArray();
            var ticketSerial = rlp.DecodeUInt256();
            var waitPeriods = rlp.DecodeArray(ctx => (uint)ctx.DecodeUInt256());

            var message = results.Message;
            message.PingMdc = token;
            message.ExpirationTime = expirationTime;
            message.TopicMdc = topicMdc;
            message.TicketSerial = (uint)ticketSerial;
            message.WaitPeriods = waitPeriods;

            return message;
        }
    }
}