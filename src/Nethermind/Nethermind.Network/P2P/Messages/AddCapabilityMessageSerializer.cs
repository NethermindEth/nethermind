// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    /// <summary>
    /// Serializes P2P capability negotiation messages.
    /// </summary>
    public class AddCapabilityMessageSerializer : IZeroMessageSerializer<AddCapabilityMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<Capability>((int)1.KiB(), nameof(Capability.ProtocolCode));

        public void Serialize(IByteBuffer byteBuffer, AddCapabilityMessage msg)
        {
            int totalLength = GetLength(msg, out int contentLength);
            byteBuffer.EnsureWritable(totalLength);

            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);
            stream.Encode(msg.Capability.ProtocolCode.ToLowerInvariant());
            stream.Encode(msg.Capability.Version);
        }

        public AddCapabilityMessage Deserialize(IByteBuffer byteBuffer)
        {
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            ctx.ReadSequenceLength();
            string protocolCode = ctx.DecodeString(RlpLimit);
            byte version = ctx.DecodeByte();

            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return new AddCapabilityMessage(new Capability(protocolCode, version));
        }
        private static int GetLength(AddCapabilityMessage msg, out int contentLength)
        {
            contentLength = Rlp.LengthOf(msg.Capability.ProtocolCode.ToLowerInvariant());
            contentLength += Rlp.LengthOf(msg.Capability.Version);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
