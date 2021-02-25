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

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class GetBlockBodiesMessageSerializer : IZeroMessageSerializer<GetBlockBodiesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetBlockBodiesMessage message)
        {
            NettyRlpStream nettyRlpStream = new(byteBuffer);

            int contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.BlockHashes[i]);
            }

            int totalLength = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            
            nettyRlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHashes.Count; i++)
            {
                nettyRlpStream.Encode(message.BlockHashes[i]);
            }
        }

        public GetBlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }
        
        public static GetBlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            Keccak[] hashes = rlpStream.DecodeArray(ctx => rlpStream.DecodeKeccak(), false);
            return new GetBlockBodiesMessage(hashes);
        }
    }
}
