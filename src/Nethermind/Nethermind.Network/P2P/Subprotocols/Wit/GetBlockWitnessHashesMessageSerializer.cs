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
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Wit
{
    public class GetBlockWitnessHashesMessageSerializer : IZeroMessageSerializer<GetBlockWitnessHashesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetBlockWitnessHashesMessage message)
        {
            NettyRlpStream nettyRlpStream = new NettyRlpStream(byteBuffer);
            int contentLength =
                Rlp.LengthOf(message.RequestId)
                + (message.BlockHash == null ? 1 : Rlp.LengthOfKeccakRlp);
            nettyRlpStream.StartSequence(contentLength);
            nettyRlpStream.Encode(message.RequestId);
            nettyRlpStream.Encode(message.BlockHash);
        }

        public GetBlockWitnessHashesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            rlpStream.ReadSequenceLength();
            long requestId = rlpStream.DecodeLong();
            var hash = rlpStream.DecodeKeccak();
            return new GetBlockWitnessHashesMessage(requestId, hash);
        }
    }
}
