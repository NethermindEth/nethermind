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

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class GetBlockBodiesMessageSerializer: IZeroMessageSerializer<GetBlockBodiesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetBlockBodiesMessage message)
        {
            Eth.V62.GetBlockBodiesMessageSerializer ethSerializer = new Eth.V62.GetBlockBodiesMessageSerializer();
            Rlp ethMessage = new Rlp(ethSerializer.Serialize(message.EthMessage));
            int contentLength = Rlp.LengthOf(message.RequestId) + ethMessage.Length;

            int totalLength = Rlp.GetSequenceRlpLength(contentLength);
            
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(ethMessage);
        }

        public GetBlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        private static GetBlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            GetBlockBodiesMessage getBlockBodiesMessage = new GetBlockBodiesMessage();
            rlpStream.ReadSequenceLength();
            getBlockBodiesMessage.RequestId = rlpStream.DecodeLong();
            getBlockBodiesMessage.EthMessage = Eth.V62.GetBlockBodiesMessageSerializer.Deserialize(rlpStream);
            return getBlockBodiesMessage;
        }
    }
}
