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
            int totalLength = Rlp.LengthOf(msg.Capability.ProtocolCode.ToLowerInvariant());
            totalLength += Rlp.LengthOf(msg.Capability.Version);
            totalLength = Rlp.LengthOfSequence(totalLength);
            
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(totalLength), true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(totalLength);
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
    }
}
