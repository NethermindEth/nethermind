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
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class HelloMessageSerializer : IZeroMessageSerializer<HelloMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, HelloMessage msg)
        {
            (int totalLength, int innerLength) length = GetLength(msg);
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(length.totalLength), true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(length.totalLength);
            stream.Encode(msg.P2PVersion);
            stream.Encode(msg.ClientId);
            stream.StartSequence(length.innerLength);
            foreach (Capability? capability in msg.Capabilities)
            {
                string protocolCode = capability.ProtocolCode.ToLowerInvariant();
                int capabilityLength = Rlp.LengthOf(protocolCode);
                capabilityLength += Rlp.LengthOf(capability.Version);
                stream.StartSequence(capabilityLength);
                stream.Encode(protocolCode);
                stream.Encode(capability.Version);
            }

            stream.Encode(msg.ListenPort);
            stream.Encode(msg.NodeId.Bytes);
        }

        private (int, int) GetLength(HelloMessage msg)
        {
            int contentLength = 0;
            contentLength += Rlp.LengthOf(msg.P2PVersion);
            contentLength += Rlp.LengthOf(msg.ClientId);
            int innerContentLength = 0;
            foreach (Capability? capability in msg.Capabilities)
            {
                int capabilityLength = Rlp.LengthOf(capability.ProtocolCode.ToLowerInvariant());
                capabilityLength += Rlp.LengthOf(capability.Version);
                innerContentLength += Rlp.LengthOfSequence(capabilityLength);
            }
            contentLength += Rlp.LengthOfSequence(innerContentLength);
            contentLength += Rlp.LengthOf(msg.ListenPort);
            contentLength += Rlp.LengthOf(msg.NodeId.Bytes);
            return (contentLength, innerContentLength);
        }

        public HelloMessage Deserialize(IByteBuffer msgBytes)
        {
            NettyRlpStream rlpStream = new (msgBytes);
            rlpStream.ReadSequenceLength();

            HelloMessage helloMessage = new();
            helloMessage.P2PVersion = rlpStream.DecodeByte();
            helloMessage.ClientId = string.Intern(rlpStream.DecodeString());
            helloMessage.Capabilities = rlpStream.DecodeArray(ctx =>
            {
                ctx.ReadSequenceLength();
                string protocolCode = string.Intern(ctx.DecodeString());
                int version = ctx.DecodeByte();
                return new Capability(protocolCode, version);
            }).ToList();

            helloMessage.ListenPort = rlpStream.DecodeInt();

            ReadOnlySpan<byte> publicKeyBytes = rlpStream.DecodeByteArraySpan();
            if (publicKeyBytes.Length != PublicKey.LengthInBytes &&
                publicKeyBytes.Length != PublicKey.PrefixedLengthInBytes)
            {
                throw new NetworkingException(
                    $"Client {helloMessage.ClientId} sent an invalid public key format " +
                    $"(length was {publicKeyBytes.Length})",
                    NetworkExceptionType.HandshakeOrInit);
            }
            else
            {
                helloMessage.NodeId = new PublicKey(publicKeyBytes);
            }

            return helloMessage;
        }
    }
}
