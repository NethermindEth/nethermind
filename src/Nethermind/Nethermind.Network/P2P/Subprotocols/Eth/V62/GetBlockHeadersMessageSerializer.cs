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
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class GetBlockHeadersMessageSerializer : IZeroMessageSerializer<GetBlockHeadersMessage>
    {
       public static GetBlockHeadersMessage Deserialize(RlpStream rlpStream)
        {
            GetBlockHeadersMessage message = new GetBlockHeadersMessage();
            rlpStream.ReadSequenceLength();
            byte[] startingBytes = rlpStream.DecodeByteArray();
            if (startingBytes.Length == 32)
            {
                message.StartBlockHash = new Keccak(startingBytes);
            }
            else
            {
                message.StartBlockNumber = (long)new UInt256(startingBytes, true);
            }

            message.MaxHeaders = rlpStream.DecodeInt();
            message.Skip = rlpStream.DecodeInt();
            message.Reverse = rlpStream.DecodeByte();
            return message;
        }

        public void Serialize(IByteBuffer byteBuffer, GetBlockHeadersMessage message)
        {
            int contentLength = message.StartBlockHash == null
                ? Rlp.LengthOf(message.StartBlockNumber)
                : Rlp.LengthOf(message.StartBlockHash);
            contentLength += Rlp.LengthOf(message.MaxHeaders);
            contentLength += Rlp.LengthOf(message.Skip);
            contentLength += Rlp.LengthOf(message.Reverse);

            int totalLength = Rlp.GetSequenceRlpLength(contentLength);
            
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength, true);

            rlpStream.StartSequence(contentLength);
            if (message.StartBlockHash == null)
            {
                rlpStream.Encode(message.StartBlockNumber);
            }
            else
            {
                rlpStream.Encode(message.StartBlockHash);
            }
            
            rlpStream.Encode(message.MaxHeaders);
            rlpStream.Encode(message.Skip);
            rlpStream.Encode(message.Reverse);
        }

        public GetBlockHeadersMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}
