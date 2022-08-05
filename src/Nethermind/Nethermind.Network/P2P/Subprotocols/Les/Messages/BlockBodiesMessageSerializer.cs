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

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class BlockBodiesMessageSerializer: IZeroMessageSerializer<BlockBodiesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            Eth.V62.Messages.BlockBodiesMessageSerializer ethSerializer = new();
            int ethMessageTotalLength = ethSerializer.GetLength(message.EthMessage, out int ethMessageContentLength);
            int contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOf(message.BufferValue) + ethMessageTotalLength;

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.BufferValue);
            ethSerializer.Serialize(byteBuffer, message.EthMessage);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        private static BlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            BlockBodiesMessage blockBodiesMessage = new();
            rlpStream.ReadSequenceLength();
            blockBodiesMessage.RequestId = rlpStream.DecodeLong();
            blockBodiesMessage.BufferValue = rlpStream.DecodeInt();
            blockBodiesMessage.EthMessage = Eth.V62.Messages.BlockBodiesMessageSerializer.Deserialize(rlpStream);
            return blockBodiesMessage;
        }
    }
}
