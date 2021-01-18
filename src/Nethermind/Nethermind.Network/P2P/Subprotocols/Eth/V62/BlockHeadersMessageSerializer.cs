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
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class BlockHeadersMessageSerializer : IZeroMessageSerializer<BlockHeadersMessage>
    {
        private HeaderDecoder _headerDecoder = new HeaderDecoder();

        public void Serialize(IByteBuffer byteBuffer, BlockHeadersMessage message)
        {
            int contentLength = 0;
            for (int i = 0; i < message.BlockHeaders.Length; i++)
            {
                contentLength += _headerDecoder.GetLength(message.BlockHeaders[i], RlpBehaviors.None);
            }
            
            int length = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(length, true);
            
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHeaders.Length; i++)
            {
                rlpStream.Encode(message.BlockHeaders[i]);
            }
        }

        public BlockHeadersMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
        
        public static BlockHeadersMessage Deserialize(RlpStream rlpStream)
        {
            BlockHeadersMessage message = new BlockHeadersMessage();
            message.BlockHeaders = Rlp.DecodeArray<BlockHeader>(rlpStream);
            return message;
        }
    }
}
