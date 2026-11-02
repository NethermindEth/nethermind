// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class HelloMessageSerializer : IMessageSerializer<HelloMessage>
    {
        private static readonly RlpLimit ClientIdRlpLimit = RlpLimit.For<HelloMessage>(1_024, nameof(HelloMessage.ClientId));

        public int GetLength(HelloMessage msg) => Rlp.LengthOfSequence(GetLengths(msg).ContentLength);

        public int Serialize(Span<byte> destination, HelloMessage msg)
        {
            (int totalLength, int innerLength) = GetLengths(msg);
            RlpWriter writer = new(destination);
            writer.StartSequence(totalLength);
            writer.Encode(msg.P2PVersion);
            writer.Encode(msg.ClientId);
            writer.StartSequence(innerLength);
            foreach (Capability? capability in msg.Capabilities.AsSpan())
            {
                string protocolCode = capability.ProtocolCode.ToLowerInvariant();
                int capabilityLength = Rlp.LengthOf(protocolCode);
                capabilityLength += Rlp.LengthOf(capability.Version);
                writer.StartSequence(capabilityLength);
                writer.Encode(protocolCode);
                writer.Encode(capability.Version);
            }

            writer.Encode(msg.ListenPort);
            writer.Encode(msg.NodeId.Bytes);
            return writer.Position;
        }

        private static (int ContentLength, int CapabilitiesLength) GetLengths(HelloMessage msg)
        {
            int contentLength = 0;
            contentLength += Rlp.LengthOf(msg.P2PVersion);
            contentLength += Rlp.LengthOf(msg.ClientId);
            int innerContentLength = 0;
            foreach (Capability? capability in msg.Capabilities.AsSpan())
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

        public HelloMessage Deserialize(ReadOnlySpan<byte> msgBytes)
        {
            RlpReader ctx = new(msgBytes);
            ctx.ReadSequenceLength();

            HelloMessage helloMessage = new();
            helloMessage.P2PVersion = ctx.DecodeByte();
            helloMessage.ClientId = ctx.DecodeString(ClientIdRlpLimit);

            helloMessage.Capabilities = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) =>
            {
                int length = c.ReadSequenceLength();
                int checkPosition = c.Position + length;

                ReadOnlySpan<byte> protocolSpan = c.DecodeByteArraySpan(RlpLimit.L8);
                if (!Contract.P2P.ProtocolParser.TryGetProtocolCode(protocolSpan, out string? protocolCode))
                {
                    protocolCode = Encoding.UTF8.GetString(protocolSpan);
                }
                int version = c.DecodeByte();

                c.Check(checkPosition);
                return new Capability(protocolCode, version);
            }, limit: RlpLimit.L64);

            helloMessage.ListenPort = ctx.DecodePositiveInt();

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

            return helloMessage;
        }
    }
}
