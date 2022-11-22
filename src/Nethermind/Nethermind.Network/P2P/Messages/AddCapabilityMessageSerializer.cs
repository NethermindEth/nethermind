// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    /// <summary>
    /// This is probably used in NDM
    /// </summary>
    public class AddCapabilityMessageSerializer : IMessageSerializer<AddCapabilityMessage>
    {
        public byte[] Serialize(AddCapabilityMessage msg)
            => Rlp.Encode(Rlp.Encode(msg.Capability.ProtocolCode.ToLowerInvariant()),
                Rlp.Encode(msg.Capability.Version)).Bytes;

        public AddCapabilityMessage Deserialize(byte[] msgBytes)
        {
            RlpStream context = msgBytes.AsRlpStream();
            context.ReadSequenceLength();
            string protocolCode = context.DecodeString();
            byte version = context.DecodeByte();

            return new AddCapabilityMessage(new Capability(protocolCode, version));
        }
    }
}
