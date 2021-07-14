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

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66
{
    public class NodeDataMessageSerializer : IZeroMessageSerializer<NodeDataMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, NodeDataMessage message)
        {
            Eth.V63.NodeDataMessageSerializer ethSerializer = new();
            Rlp ethMessage = new Rlp(ethSerializer.Serialize(message.EthMessage));
            int contentLength = Rlp.LengthOf(message.RequestId) + ethMessage.Length;

            int totalLength = Rlp.GetSequenceRlpLength(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(ethMessage);
        }

        public NodeDataMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
        
        private static NodeDataMessage Deserialize(RlpStream rlpStream)
        {
            NodeDataMessage nodeDataMessage = new NodeDataMessage();
            rlpStream.ReadSequenceLength();
            nodeDataMessage.RequestId = rlpStream.DecodeLong();
            nodeDataMessage.EthMessage = Eth.V63.NodeDataMessageSerializer.Deserialize(rlpStream);
            return nodeDataMessage;
        }
    }
}
