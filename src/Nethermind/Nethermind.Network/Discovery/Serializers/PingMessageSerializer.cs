//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Net;
using DotNetty.Common.Utilities;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers
{
    public class PingMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<PingMessage>
    {
        public PingMessageSerializer(IEcdsa ecdsa, IPrivateKeyGenerator privateKeyGenerator, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver)
            : base(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver)
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
            message.Mdc = serializedMsg.Slice(0, 32);
            
            return serializedMsg;
        }

        public PingMessage Deserialize(byte[] msg)
        {
            (PingMessage Message, byte[] Mdc, byte[] Data) results = PrepareForDeserialization<PingMessage>(msg);
            
            RlpStream rlp = results.Data.AsRlpStream();
            rlp.ReadSequenceLength();
            int version = rlp.DecodeInt();

            rlp.ReadSequenceLength();
            ReadOnlySpan<byte> sourceAddress = rlp.DecodeByteArraySpan();
            
            // TODO: please note that we decode only one field for port and if the UDP is different from TCP then
            // our discovery messages will not be routed correctly (the fix will not be part of this commit)
            rlp.DecodeInt(); // UDP port
            int tcpPort = rlp.DecodeInt(); // we assume here that UDP and TCP port are same 

            IPEndPoint source = GetAddress(sourceAddress, tcpPort);
            rlp.ReadSequenceLength();
            ReadOnlySpan<byte> destinationAddress = rlp.DecodeByteArraySpan();
            IPEndPoint destination = GetAddress(destinationAddress, rlp.DecodeInt());
            rlp.DecodeInt(); // UDP port

            long expireTime = rlp.DecodeLong();

            PingMessage message = results.Message;
            message.SourceAddress = source;
            message.DestinationAddress = destination;
            message.Mdc = results.Mdc;
            message.Version = version;
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}
