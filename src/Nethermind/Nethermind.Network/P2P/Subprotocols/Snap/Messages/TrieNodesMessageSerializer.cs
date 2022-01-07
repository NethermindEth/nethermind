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
// 

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessageSerializer: SnapSerializerBase<TrieNodesMessage>
    {
        public override void Serialize(IByteBuffer byteBuffer, TrieNodesMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            
            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.Nodes.Length; i++)
            {
                rlpStream.Encode(message.Nodes[i]);
            }
        }

        protected override TrieNodesMessage Deserialize(RlpStream rlpStream)
        {
            byte[][] result = rlpStream.DecodeArray(stream => stream.DecodeByteArray());
            return new TrieNodesMessage(result);
        }

        public override int GetLength(TrieNodesMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.Nodes.Length; i++)
            {
                contentLength += Rlp.LengthOf(message.Nodes[i]);
            }
            
            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
