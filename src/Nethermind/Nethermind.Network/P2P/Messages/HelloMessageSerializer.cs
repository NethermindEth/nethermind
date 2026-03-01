// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
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
            (int totalLength, int innerLength) = GetLength(msg);
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(totalLength), force: true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(totalLength);
            stream.Encode(msg.P2PVersion);
            stream.Encode(msg.ClientId);
            stream.StartSequence(innerLength);
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

        private static (int, int) GetLength(HelloMessage msg)
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
            Rlp.ValueDecoderContext ctx = msgBytes.AsRlpContext();
            ctx.ReadSequenceLength();

            HelloMessage helloMessage = new();
            helloMessage.P2PVersion = ctx.DecodeByte();
            helloMessage.ClientId = ctx.DecodeString();
            helloMessage.Capabilities = ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) =>
            {
                c.ReadSequenceLength();
                ReadOnlySpan<byte> protocolSpan = c.DecodeByteArraySpan();
                if (!Contract.P2P.ProtocolParser.TryGetProtocolCode(protocolSpan, out string? protocolCode))
                {
                    protocolCode = Encoding.UTF8.GetString(protocolSpan);
                }
                int version = c.DecodeByte();
                return new Capability(protocolCode, version);
            });

            helloMessage.ListenPort = ctx.DecodeInt();

            ReadOnlySpan<byte> publicKeyBytes = ctx.DecodeByteArraySpan(RlpLimit.L64);
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

            msgBytes.SetReaderIndex(msgBytes.ReaderIndex + ctx.Position);
            return helloMessage;
        }
    }
}
