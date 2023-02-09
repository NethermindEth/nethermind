// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class HelloMessageSerializer : IMessageSerializer<HelloMessage>
    {
        public byte[] Serialize(HelloMessage msg)
        {
            return Rlp.Encode(
                Rlp.Encode(msg.P2PVersion),
                Rlp.Encode(msg.ClientId),
                Rlp.Encode(msg.Capabilities.Select(c => Rlp.Encode(
                    Rlp.Encode(c.ProtocolCode.ToLowerInvariant()),
                    Rlp.Encode(c.Version))).ToArray()),
                Rlp.Encode(msg.ListenPort),
                Rlp.Encode(msg.NodeId.Bytes)
            ).Bytes;
        }

        public HelloMessage Deserialize(byte[] msgBytes)
        {
            RlpStream rlpStream = msgBytes.AsRlpStream();
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
