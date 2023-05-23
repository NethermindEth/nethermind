// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    /// <summary>
    /// This is probably used in NDM
    /// </summary>
    public class AddCapabilityMessageSerializer : IZeroMessageSerializer<AddCapabilityMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, AddCapabilityMessage msg)
        {
            int totalLength = GetLength(msg, out int contentLength);
            byteBuffer.EnsureWritable(totalLength, true);

            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);
            stream.Encode(msg.Capability.ProtocolCode.ToLowerInvariant());
            stream.Encode(msg.Capability.Version);
        }

        public AddCapabilityMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream context = new(byteBuffer);
            context.ReadSequenceLength();
            string protocolCode = context.DecodeString();
            byte version = context.DecodeByte();

            return new AddCapabilityMessage(new Capability(protocolCode, version));
        }
        private int GetLength(AddCapabilityMessage msg, out int contentLength)
        {
            contentLength = Rlp.LengthOf(msg.Capability.ProtocolCode.ToLowerInvariant());
            contentLength += Rlp.LengthOf(msg.Capability.Version);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
